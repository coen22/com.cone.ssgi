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
        private static readonly string[] k_WalrStatNamesA =
        {
            "_XX0A", "_XX1A", "_XX2A", "_XX3A", "_YXR_A", "_YXG_A", "_YXB_A", "_WA"
        };

        private static readonly string[] k_WalrStatNamesB =
        {
            "_XX0B", "_XX1B", "_XX2B", "_XX3B", "_YXR_B", "_YXG_B", "_YXB_B", "_WB"
        };

        private static readonly int _XX0A = Shader.PropertyToID(k_WalrStatNamesA[0]);
        private static readonly int _XX1A = Shader.PropertyToID(k_WalrStatNamesA[1]);
        private static readonly int _XX2A = Shader.PropertyToID(k_WalrStatNamesA[2]);
        private static readonly int _XX3A = Shader.PropertyToID(k_WalrStatNamesA[3]);
        private static readonly int _YXR_A = Shader.PropertyToID(k_WalrStatNamesA[4]);
        private static readonly int _YXG_A = Shader.PropertyToID(k_WalrStatNamesA[5]);
        private static readonly int _YXB_A = Shader.PropertyToID(k_WalrStatNamesA[6]);
        private static readonly int _WA    = Shader.PropertyToID(k_WalrStatNamesA[7]);

        private static readonly int _XX0B = Shader.PropertyToID(k_WalrStatNamesB[0]);
        private static readonly int _XX1B = Shader.PropertyToID(k_WalrStatNamesB[1]);
        private static readonly int _XX2B = Shader.PropertyToID(k_WalrStatNamesB[2]);
        private static readonly int _XX3B = Shader.PropertyToID(k_WalrStatNamesB[3]);
        private static readonly int _YXR_B = Shader.PropertyToID(k_WalrStatNamesB[4]);
        private static readonly int _YXG_B = Shader.PropertyToID(k_WalrStatNamesB[5]);
        private static readonly int _YXB_B = Shader.PropertyToID(k_WalrStatNamesB[6]);
        private static readonly int _WB    = Shader.PropertyToID(k_WalrStatNamesB[7]);

        private static readonly int[] k_WalrStatIdsA =
        {
            _XX0A, _XX1A, _XX2A, _XX3A, _YXR_A, _YXG_A, _YXB_A, _WA
        };

        private static readonly int[] k_WalrStatIdsB =
        {
            _XX0B, _XX1B, _XX2B, _XX3B, _YXR_B, _YXG_B, _YXB_B, _WB
        };

        private static readonly int[] k_WalrSolveIndices = { 0, 1, 2, 3, 4, 5, 6 };

        private readonly RTHandle[] m_WalrStatsA = new RTHandle[8];
        private readonly RTHandle[] m_WalrStatsB = new RTHandle[8];

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

        private void EnsureStatBuffers(int width, int height)
        {
            if (width <= 0 || height <= 0)
                return;

            RenderTextureDescriptor descriptor = new RenderTextureDescriptor(
                width,
                height,
                GraphicsFormat.R32G32B32A32_SFloat,
                0
            )
            {
                enableRandomWrite = true,
                msaaSamples = 1,
                sRGB = false,
                depthBufferBits = 0
            };

            for (int i = 0; i < m_WalrStatsA.Length; ++i)
            {
                RenderingUtils.ReAllocateIfNeeded(
                    ref m_WalrStatsA[i],
                    descriptor,
                    FilterMode.Point,
                    TextureWrapMode.Clamp,
                    name: k_WalrStatNamesA[i]
                );
                RenderingUtils.ReAllocateIfNeeded(
                    ref m_WalrStatsB[i],
                    descriptor,
                    FilterMode.Point,
                    TextureWrapMode.Clamp,
                    name: k_WalrStatNamesB[i]
                );
            }
        }

        internal void ReleaseResources()
        {
            ReleaseStatBuffers(m_WalrStatsA);
            ReleaseStatBuffers(m_WalrStatsB);
        }

        private static void ReleaseStatBuffers(RTHandle[] handles)
        {
            if (handles == null)
                return;

            for (int i = 0; i < handles.Length; ++i)
            {
                handles[i]?.Release();
                handles[i] = null;
            }
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
            bool hasAlbedo)
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

            EnsureStatBuffers(w, h);

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
            Vector4 zParams = Shader.GetGlobalVector(Shader.PropertyToID("_ZBufferParams"));
            cmd.SetComputeVectorParam(m_Shader, _WalrZBufferParams, zParams);

            // Kernel dispatch sizes
            int gx = Mathf.CeilToInt(w / 8.0f);
            int gy = Mathf.CeilToInt(h / 8.0f);

            // Init: seed per‑pixel stats from the center sample (x = [1, N])
            cmd.SetComputeTextureParam(m_Shader, m_KInit, _NoisyGI,  GetRT(source));
            cmd.SetComputeTextureParam(m_Shader, m_KInit, _DepthTexture, depthRT);
            cmd.SetComputeTextureParam(m_Shader, m_KInit, _NormalTexture, normalRT);
            cmd.SetComputeTextureParam(m_Shader, m_KInit, _AlbedoTexture, hasAlbedo ? albedoRT : fallbackAlbedo);
            for (int i = 0; i < k_WalrStatIdsA.Length; ++i)
            {
                cmd.SetComputeTextureParam(
                    m_Shader,
                    m_KInit,
                    k_WalrStatIdsA[i],
                    GetRT(m_WalrStatsA[i])
                );
            }
            cmd.DispatchCompute(m_Shader, m_KInit, gx, gy, 1);

            // We'll ping‑pong using local arrays (no reassignment to readonly fields)
            int[] srcIds = k_WalrStatIdsA;
            int[] dstIds = k_WalrStatIdsB;
            RTHandle[] srcHandles = m_WalrStatsA;
            RTHandle[] dstHandles = m_WalrStatsB;

            for (int it = 0; it < settings.Iterations; ++it)
            {
                int step = settings.BaseStep << it;
                cmd.SetComputeIntParam(m_Shader, _BaseStep, step);

                cmd.SetComputeTextureParam(m_Shader, m_KIter, _DepthTexture, depthRT);
                cmd.SetComputeTextureParam(m_Shader, m_KIter, _NormalTexture, normalRT);
                cmd.SetComputeTextureParam(m_Shader, m_KIter, _AlbedoTexture, hasAlbedo ? albedoRT : fallbackAlbedo);

                // source and destination averages
                for (int i = 0; i < srcIds.Length; ++i)
                {
                    cmd.SetComputeTextureParam(
                        m_Shader,
                        m_KIter,
                        srcIds[i],
                        GetRT(srcHandles[i])
                    );
                    cmd.SetComputeTextureParam(
                        m_Shader,
                        m_KIter,
                        dstIds[i],
                        GetRT(dstHandles[i])
                    );
                }

                cmd.DispatchCompute(m_Shader, m_KIter, gx, gy, 1);

                // swap locals
                (srcIds, dstIds) = (dstIds, srcIds);
                (srcHandles, dstHandles) = (dstHandles, srcHandles);
            }

            // Solve per pixel from the *current* src set (last ping)
            cmd.SetComputeTextureParam(m_Shader, m_KSolve, _NormalTexture, normalRT);
            foreach (int index in k_WalrSolveIndices)
            {
                cmd.SetComputeTextureParam(
                    m_Shader,
                    m_KSolve,
                    srcIds[index],
                    GetRT(srcHandles[index])
                );
            }
            cmd.SetComputeTextureParam(m_Shader, m_KSolve, _OutGI, GetRT(destination));
            cmd.DispatchCompute(m_Shader, m_KSolve, gx, gy, 1);

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
            bool hasAlbedo)
        {
            return Execute(cmd, ref renderingData, settings, source, destination,
                           depthRT, normalRT, albedoRT, fallbackAlbedo, hasAlbedo);
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
            int height)
        {
            // Optional: if you need RenderGraph path, wire similarly to non-RG variant
            // For now, just map to the existing Execute through texture handles if desired.
            // This class currently implements only the RTHandle path explicitly.
            return false; // stub; implement if you use RG path
        }
#endif
    }
}