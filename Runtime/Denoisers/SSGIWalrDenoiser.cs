using System;
using Cone.SSGI;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

namespace Cone.SSGI.Denoisers
{
    internal sealed class SSGIWalrDenoiser : ISSGIDenoiser<SSGIWalrDenoiser.Settings>
    {
        private static readonly int _TexSize             = Shader.PropertyToID("_TexSize");
        private static readonly int _Iterations          = Shader.PropertyToID("_Iterations");
        private static readonly int _BaseStep            = Shader.PropertyToID("_BaseStep");
        private static readonly int _SigmaDepth          = Shader.PropertyToID("_SigmaDepth");
        private static readonly int _SigmaNormal         = Shader.PropertyToID("_SigmaNormal");
        private static readonly int _SigmaAlbedo         = Shader.PropertyToID("_SigmaAlbedo");
        private static readonly int _AlbedoWeight        = Shader.PropertyToID("_AlbedoWeight");
        private static readonly int _MinW                = Shader.PropertyToID("_MinW");
        private static readonly int _WalrZBufferParams   = Shader.PropertyToID("_WalrZBufferParams");
        private static readonly int _InvProj             = Shader.PropertyToID("_InvProj");
        private static readonly int _InvView             = Shader.PropertyToID("_InvView");
        private static readonly int _NoisyGI             = Shader.PropertyToID("_NoisyGI");
        private static readonly int _DepthTexture        = Shader.PropertyToID("_DepthTexture");
        private static readonly int _NormalTexture       = Shader.PropertyToID("_NormalTexture");
        private static readonly int _AlbedoTexture       = Shader.PropertyToID("_AlbedoTexture");
        private static readonly int _OutGI               = Shader.PropertyToID("_OutGI");

        private const string k_ShaderResource = "SSGI_WALR_DiffuseGI";
        private const string k_InitKernel    = "WALR_Init";
        private const string k_IterKernel    = "WALR_Iter";
        private const string k_SolveKernel   = "WALR_Solve";

        private ComputeShader m_Shader;
        private int m_KInit = -1, m_KIter = -1, m_KSolve = -1;
        private bool m_WarnedMissingShader, m_WarnedMissingKernel;

        // Statistic textures (IDs are constants; we will keep local src/dst IDs when ping‑ponging)
        private static readonly int _XX0A = Shader.PropertyToID("_XX0A");
        private static readonly int _XX1A = Shader.PropertyToID("_XX1A");
        private static readonly int _XX2A = Shader.PropertyToID("_XX2A");
        private static readonly int _XX3A = Shader.PropertyToID("_XX3A");
        private static readonly int _YXR_A = Shader.PropertyToID("_YXR_A");
        private static readonly int _YXG_A = Shader.PropertyToID("_YXG_A");
        private static readonly int _YXB_A = Shader.PropertyToID("_YXB_A");
        private static readonly int _WA    = Shader.PropertyToID("_WA");

        private static readonly int _XX0B = Shader.PropertyToID("_XX0B");
        private static readonly int _XX1B = Shader.PropertyToID("_XX1B");
        private static readonly int _XX2B = Shader.PropertyToID("_XX2B");
        private static readonly int _XX3B = Shader.PropertyToID("_XX3B");
        private static readonly int _YXR_B = Shader.PropertyToID("_YXR_B");
        private static readonly int _YXG_B = Shader.PropertyToID("_YXG_B");
        private static readonly int _YXB_B = Shader.PropertyToID("_YXB_B");
        private static readonly int _WB    = Shader.PropertyToID("_WB");

        public struct Settings
        {
            public int Iterations;    // T
            public int BaseStep;      // s0
            public float SigmaDepth;  // σ_d for plane distance
            public float SigmaNormal; // σ_n
            public float SigmaAlbedo; // σ_a
            public float AlbedoWeight;// mix for albedo guide
            public float MinWeight;   // clamp to avoid degenerate systems
        }

        public SSGIWalrDenoiser()
        {
            profilingSampler = new ProfilingSampler("SSGI WALR (paper)");
        }

        public override ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm Algorithm =>
            ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.WeightedAtrousLinearRegression;

        public override string DisplayName => "Weighted À‑Trous Linear Regression (paper)";
        public override Type SettingsType => typeof(Settings);

        internal override void Configure(ScreenSpaceGlobalIlluminationURP _)
        {
            ComputeShader shader = m_Shader ?? Resources.Load<ComputeShader>(k_ShaderResource);
            if (!ReferenceEquals(shader, m_Shader))
            {
                m_Shader = shader;
                m_KInit  = (shader && shader.HasKernel(k_InitKernel))  ? shader.FindKernel(k_InitKernel)  : -1;
                m_KIter  = (shader && shader.HasKernel(k_IterKernel))  ? shader.FindKernel(k_IterKernel)  : -1;
                m_KSolve = (shader && shader.HasKernel(k_SolveKernel)) ? shader.FindKernel(k_SolveKernel) : -1;
                m_WarnedMissingShader = false;
                m_WarnedMissingKernel = false;
            }
#if UNITY_EDITOR || DEBUG
            if (!m_WarnedMissingShader && m_Shader == null)
            {
                Debug.LogWarning("SSGI WALR: Missing compute shader 'SSGI_WALR_DiffuseGI'. Falling back to copy.");
                m_WarnedMissingShader = true;
            }
            else if (!m_WarnedMissingKernel && m_Shader != null && (m_KInit < 0 || m_KIter < 0 || m_KSolve < 0))
            {
                Debug.LogWarning("SSGI WALR: Compute shader does not expose kernels WALR_Init/WALR_Iter/WALR_Solve. Falling back to copy.");
                m_WarnedMissingKernel = true;
            }
#endif
        }

        public override bool IsSupported => SystemInfo.supportsComputeShaders && m_Shader && m_KInit >= 0 && m_KIter >= 0 && m_KSolve >= 0;

        public override Settings CreateSettings(ScreenSpaceGlobalIlluminationVolume volume)
        {
            if (volume == null) return default;
            return new Settings
            {
                Iterations    = Mathf.Clamp(volume.walrIterations.value, 1, 6),
                BaseStep      = Mathf.Max(1, volume.walrBaseStep.value),
                SigmaDepth    = Mathf.Max(0.0f, volume.walrSigmaDepth.value),
                SigmaNormal   = Mathf.Max(0.0f, volume.walrSigmaNormal.value),
                SigmaAlbedo   = Mathf.Max(0.0f, volume.walrSigmaAlbedo.value),
                AlbedoWeight  = Mathf.Clamp01(volume.walrAlbedoWeight.value),
                MinWeight     = Mathf.Max(1e-6f, volume.walrMinWeight.value)
            };
        }

        internal override void ConfigurePass(ScreenSpaceGlobalIlluminationURP feature, ScreenSpaceGlobalIlluminationPass pass, ScreenSpaceGlobalIlluminationVolume volume)
        {
            pass.walrDenoiser = this;
        }

        private static RenderTargetIdentifier GetRT(RTHandle h)
        {
            if (h == null) return new RenderTargetIdentifier();
            return h.rt ? new RenderTargetIdentifier(h.rt) : h.nameID;
        }

        internal bool Execute(
            CommandBuffer cmd,
            ref RenderingData renderingData,
            Settings settings,
            RTHandle source,
            RTHandle destination,
            RenderTargetIdentifier depthRT,
            RenderTargetIdentifier normalRT,
            RenderTargetIdentifier albedoRT,
            RenderTargetIdentifier fallbackAlbedo,
            bool hasAlbedo,
            Vector4 zParams)
        {
            if (!IsSupported || source?.rt == null || destination?.rt == null)
            {
                if (source != null && destination != null) cmd.CopyTexture(source, destination);
                return false;
            }

            int w = destination.rt.width;
            int h = destination.rt.height;
            if (w <= 0 || h <= 0)
            {
                cmd.CopyTexture(source, destination);
                return false;
            }

            // Allocate ping‑pong stat targets (R32G32B32A32F)
            RenderTextureDescriptor d4 = new RenderTextureDescriptor(w, h, GraphicsFormat.R32G32B32A32_SFloat, 0)
            { enableRandomWrite = true, msaaSamples = 1, sRGB = false, depthBufferBits = 0 };

            int[] ids = { _XX0A,_XX1A,_XX2A,_XX3A,_YXR_A,_YXG_A,_YXB_A,_WA,
                          _XX0B,_XX1B,_XX2B,_XX3B,_YXR_B,_YXG_B,_YXB_B,_WB };
            foreach (int id in ids)
                cmd.GetTemporaryRT(id, d4);

            // Common params
            cmd.SetComputeVectorParam(m_Shader, _TexSize, new Vector4(w, h, 0, 0));
            cmd.SetComputeIntParam(m_Shader, _Iterations, Mathf.Max(1, settings.Iterations));
            cmd.SetComputeIntParam(m_Shader, _BaseStep, Mathf.Max(1, settings.BaseStep));
            cmd.SetComputeFloatParam(m_Shader, _SigmaDepth,  Mathf.Max(0.0f, settings.SigmaDepth));
            cmd.SetComputeFloatParam(m_Shader, _SigmaNormal, Mathf.Max(0.0f, settings.SigmaNormal));
            cmd.SetComputeFloatParam(m_Shader, _SigmaAlbedo, Mathf.Max(0.0f, settings.SigmaAlbedo));
            cmd.SetComputeFloatParam(m_Shader, _AlbedoWeight, Mathf.Clamp01(settings.AlbedoWeight));
            cmd.SetComputeFloatParam(m_Shader, _MinW, Mathf.Max(1e-6f, settings.MinWeight));

            // Matrices for plane distance (view‑space reconstruction)
            var cam = renderingData.cameraData.camera;
            Matrix4x4 invProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true).inverse; // fix: GL API is available across URP versions
            Matrix4x4 invView = cam.worldToCameraMatrix.inverse; // cameraToWorld
            cmd.SetComputeMatrixParam(m_Shader, _InvProj, invProj);
            cmd.SetComputeMatrixParam(m_Shader, _InvView, invView);

            // Depth params
            cmd.SetComputeVectorParam(m_Shader, _WalrZBufferParams, zParams);

            // Kernel dispatch sizes
            int gx = Mathf.CeilToInt(w / 8.0f);
            int gy = Mathf.CeilToInt(h / 8.0f);

            // Init: seed per‑pixel stats from the center sample (x = [1, N])
            cmd.SetComputeTextureParam(m_Shader, m_KInit, _NoisyGI,  GetRT(source));
            cmd.SetComputeTextureParam(m_Shader, m_KInit, _DepthTexture, depthRT);
            cmd.SetComputeTextureParam(m_Shader, m_KInit, _NormalTexture, normalRT);
            cmd.SetComputeTextureParam(m_Shader, m_KInit, _AlbedoTexture, hasAlbedo ? albedoRT : fallbackAlbedo);
            cmd.SetComputeTextureParam(m_Shader, m_KInit, _XX0A, _XX0A);
            cmd.SetComputeTextureParam(m_Shader, m_KInit, _XX1A, _XX1A);
            cmd.SetComputeTextureParam(m_Shader, m_KInit, _XX2A, _XX2A);
            cmd.SetComputeTextureParam(m_Shader, m_KInit, _XX3A, _XX3A);
            cmd.SetComputeTextureParam(m_Shader, m_KInit, _YXR_A, _YXR_A);
            cmd.SetComputeTextureParam(m_Shader, m_KInit, _YXG_A, _YXG_A);
            cmd.SetComputeTextureParam(m_Shader, m_KInit, _YXB_A, _YXB_A);
            cmd.SetComputeTextureParam(m_Shader, m_KInit, _WA,   _WA);
            cmd.DispatchCompute(m_Shader, m_KInit, gx, gy, 1);

            // We'll ping‑pong using local variables (no reassignment to readonly fields)
            int xx0Src=_XX0A, xx1Src=_XX1A, xx2Src=_XX2A, xx3Src=_XX3A;
            int yxrSrc=_YXR_A, yxgSrc=_YXG_A, yxbSrc=_YXB_A, wSrc=_WA;
            int xx0Dst=_XX0B, xx1Dst=_XX1B, xx2Dst=_XX2B, xx3Dst=_XX3B;
            int yxrDst=_YXR_B, yxgDst=_YXG_B, yxbDst=_YXB_B, wDst=_WB;

            for (int it = 0; it < settings.Iterations; ++it)
            {
                int step = settings.BaseStep << it;
                cmd.SetComputeIntParam(m_Shader, _BaseStep, step);

                cmd.SetComputeTextureParam(m_Shader, m_KIter, _DepthTexture, depthRT);
                cmd.SetComputeTextureParam(m_Shader, m_KIter, _NormalTexture, normalRT);
                cmd.SetComputeTextureParam(m_Shader, m_KIter, _AlbedoTexture, hasAlbedo ? albedoRT : fallbackAlbedo);

                // source averages
                cmd.SetComputeTextureParam(m_Shader, m_KIter, xx0Src, xx0Src);
                cmd.SetComputeTextureParam(m_Shader, m_KIter, xx1Src, xx1Src);
                cmd.SetComputeTextureParam(m_Shader, m_KIter, xx2Src, xx2Src);
                cmd.SetComputeTextureParam(m_Shader, m_KIter, xx3Src, xx3Src);
                cmd.SetComputeTextureParam(m_Shader, m_KIter, yxrSrc, yxrSrc);
                cmd.SetComputeTextureParam(m_Shader, m_KIter, yxgSrc, yxgSrc);
                cmd.SetComputeTextureParam(m_Shader, m_KIter, yxbSrc, yxbSrc);
                cmd.SetComputeTextureParam(m_Shader, m_KIter, wSrc,   wSrc);

                // destination averages
                cmd.SetComputeTextureParam(m_Shader, m_KIter, xx0Dst, xx0Dst);
                cmd.SetComputeTextureParam(m_Shader, m_KIter, xx1Dst, xx1Dst);
                cmd.SetComputeTextureParam(m_Shader, m_KIter, xx2Dst, xx2Dst);
                cmd.SetComputeTextureParam(m_Shader, m_KIter, xx3Dst, xx3Dst);
                cmd.SetComputeTextureParam(m_Shader, m_KIter, yxrDst, yxrDst);
                cmd.SetComputeTextureParam(m_Shader, m_KIter, yxgDst, yxgDst);
                cmd.SetComputeTextureParam(m_Shader, m_KIter, yxbDst, yxbDst);
                cmd.SetComputeTextureParam(m_Shader, m_KIter, wDst,   wDst);

                cmd.DispatchCompute(m_Shader, m_KIter, gx, gy, 1);

                // swap locals
                (xx0Src, xx0Dst) = (xx0Dst, xx0Src);
                (xx1Src, xx1Dst) = (xx1Dst, xx1Src);
                (xx2Src, xx2Dst) = (xx2Dst, xx2Src);
                (xx3Src, xx3Dst) = (xx3Dst, xx3Src);
                (yxrSrc, yxrDst) = (yxrDst, yxrSrc);
                (yxgSrc, yxgDst) = (yxgDst, yxgSrc);
                (yxbSrc, yxbDst) = (yxbDst, yxbSrc);
                (wSrc,   wDst)   = (wDst,   wSrc);
            }

            // Solve per pixel from the *current* src set (last ping)
            cmd.SetComputeTextureParam(m_Shader, m_KSolve, _NormalTexture, normalRT);
            cmd.SetComputeTextureParam(m_Shader, m_KSolve, xx0Src, xx0Src);
            cmd.SetComputeTextureParam(m_Shader, m_KSolve, xx1Src, xx1Src);
            cmd.SetComputeTextureParam(m_Shader, m_KSolve, xx2Src, xx2Src);
            cmd.SetComputeTextureParam(m_Shader, m_KSolve, xx3Src, xx3Src);
            cmd.SetComputeTextureParam(m_Shader, m_KSolve, yxrSrc, yxrSrc);
            cmd.SetComputeTextureParam(m_Shader, m_KSolve, yxgSrc, yxgSrc);
            cmd.SetComputeTextureParam(m_Shader, m_KSolve, yxbSrc, yxbSrc);
            cmd.SetComputeTextureParam(m_Shader, m_KSolve, _OutGI, GetRT(destination));
            cmd.DispatchCompute(m_Shader, m_KSolve, gx, gy, 1);

            foreach (int id in ids) cmd.ReleaseTemporaryRT(id);
            return true;
        }

        // --- convenience wrappers matching your call site ---
        internal bool Dispatch(
            CommandBuffer cmd,
            ref RenderingData renderingData,
            Settings settings,
            RTHandle source,
            RTHandle destination,
            RenderTargetIdentifier depthRT,
            RenderTargetIdentifier normalRT,
            RenderTargetIdentifier albedoRT,
            RenderTargetIdentifier fallbackAlbedo,
            bool hasAlbedo,
            Vector4 zParams)
        {
            return Execute(cmd, ref renderingData, settings, source, destination,
                           depthRT, normalRT, albedoRT, fallbackAlbedo, hasAlbedo, zParams);
        }

#if UNITY_6000_0_OR_NEWER
        internal bool Dispatch(
            CommandBuffer cmd,
            Settings settings,
            TextureHandle source,
            TextureHandle destination,
            TextureHandle depthHandle,
            TextureHandle normalHandle,
            TextureHandle albedoHandle,
            TextureHandle fallbackAlbedoHandle,
            bool hasAlbedo,
            int width,
            int height,
            Vector4 zParams)
        {
            // Optional: if you need RenderGraph path, wire similarly to non-RG variant
            // For now, just map to the existing Execute through texture handles if desired.
            // This class currently implements only the RTHandle path explicitly.
            _ = zParams;
            return false; // stub; implement if you use RG path
        }
#endif
    }
}