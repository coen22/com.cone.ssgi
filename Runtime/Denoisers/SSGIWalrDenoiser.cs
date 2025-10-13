using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

namespace UnityEngine.Rendering.Universal
{
    internal sealed class SSGIWalrDenoiser : ISSGIDenoiser<SSGIWalrDenoiser.Settings>
    {
        private static readonly int _TexSize = Shader.PropertyToID("_TexSize");
        private static readonly int _Iterations = Shader.PropertyToID("_Iterations");
        private static readonly int _BaseStep = Shader.PropertyToID("_BaseStep");
        private static readonly int _SigmaDepth = Shader.PropertyToID("_SigmaDepth");
        private static readonly int _SigmaNormal = Shader.PropertyToID("_SigmaNormal");
        private static readonly int _SigmaAlbedo = Shader.PropertyToID("_SigmaAlbedo");
        private static readonly int _AlbedoWeight = Shader.PropertyToID("_AlbedoWeight");
        private static readonly int _MinW = Shader.PropertyToID("_MinW");
        private static readonly int _ZBufferParams = Shader.PropertyToID("_ZBufferParams");
        private static readonly int _WalrZBufferParams = Shader.PropertyToID("_WalrZBufferParams");
        private static readonly int _NoisyGI = Shader.PropertyToID("_NoisyGI");
        private static readonly int _DepthTexture = Shader.PropertyToID("_DepthTexture");
        private static readonly int _NormalTexture = Shader.PropertyToID("_NormalTexture");
        private static readonly int _AlbedoTexture = Shader.PropertyToID("_AlbedoTexture");
        private static readonly int _OutGI = Shader.PropertyToID("_OutGI");

        private const string k_ShaderResource = "SSGI_WALR_DiffuseGI";
        private const string k_KernelName = "WALR";

        private ComputeShader m_Shader;
        private int m_Kernel = -1;
        private bool m_WarnedMissingShader;
        private bool m_WarnedMissingKernel;

        public struct Settings
        {
            public int Iterations;
            public int BaseStep;
            public float SigmaDepth;
            public float SigmaNormal;
            public float SigmaAlbedo;
            public float AlbedoWeight;
            public float MinWeight;
        }

        public SSGIWalrDenoiser()
        {
            profilingSampler = new ProfilingSampler("SSGI WALR");
        }

        public override ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm Algorithm =>
            ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.WeightedAtrousLinearRegression;

        public override string DisplayName => "Weighted À-Trous Linear Regression";

        public override Type SettingsType => typeof(Settings);

        internal override void Configure(ScreenSpaceGlobalIlluminationURP _)
        {
            ComputeShader shader = m_Shader ?? Resources.Load<ComputeShader>(k_ShaderResource);
            if (!ReferenceEquals(shader, m_Shader))
            {
                m_Shader = shader;
                m_Kernel =
                    (shader != null && shader.HasKernel(k_KernelName))
                        ? shader.FindKernel(k_KernelName)
                        : -1;
                m_WarnedMissingShader = false;
                m_WarnedMissingKernel = false;
            }

#if UNITY_EDITOR || DEBUG
            if (!m_WarnedMissingShader && m_Shader == null)
            {
                Debug.LogWarning(
                    "Screen Space Global Illumination URP: Missing compute shader 'SSGI_WALR_DiffuseGI'. Weighted À-Trous LR denoiser will fall back to copy."
                );
                m_WarnedMissingShader = true;
            }
            else if (!m_WarnedMissingKernel && m_Shader != null && m_Kernel < 0)
            {
                Debug.LogWarning(
                    "Screen Space Global Illumination URP: Compute shader 'SSGI_WALR_DiffuseGI' does not expose kernel 'WALR'. Falling back to copy."
                );
                m_WarnedMissingKernel = true;
            }
#endif
        }

        public override bool IsSupported =>
            SystemInfo.supportsComputeShaders && m_Shader != null && m_Kernel >= 0;

        public override Settings CreateSettings(ScreenSpaceGlobalIlluminationVolume volume)
        {
            if (volume == null)
                return default;

            return new Settings
            {
                Iterations = Mathf.Clamp(volume.walrIterations.value, 1, 6),
                BaseStep = Mathf.Max(1, volume.walrBaseStep.value),
                SigmaDepth = Mathf.Max(0.0f, volume.walrSigmaDepth.value),
                SigmaNormal = Mathf.Max(0.0f, volume.walrSigmaNormal.value),
                SigmaAlbedo = Mathf.Max(0.0f, volume.walrSigmaAlbedo.value),
                AlbedoWeight = Mathf.Clamp01(volume.walrAlbedoWeight.value),
                MinWeight = Mathf.Max(1e-6f, volume.walrMinWeight.value),
            };
        }

        internal override void ConfigurePass(
            ScreenSpaceGlobalIlluminationURP feature,
            ScreenSpaceGlobalIlluminationURP.ScreenSpaceGlobalIlluminationPass pass,
            ScreenSpaceGlobalIlluminationVolume volume
        )
        {
            pass.walrDenoiser = this;
        }

        private static RenderTargetIdentifier GetHandleIdentifier(RTHandle handle)
        {
            if (handle == null)
                return new RenderTargetIdentifier();

            return handle.rt != null ? new RenderTargetIdentifier(handle.rt) : handle.nameID;
        }

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
            bool hasAlbedo
        )
        {
            _ = renderingData;

            if (
                !IsSupported
                || source == null
                || destination == null
                || source.rt == null
                || destination.rt == null
            )
            {
                cmd.CopyTexture(source, destination);
                return false;
            }

            int width = destination.rt.width;
            int height = destination.rt.height;
            if (width == 0 || height == 0)
            {
                cmd.CopyTexture(source, destination);
                return false;
            }

            RenderTargetIdentifier src = GetHandleIdentifier(source);
            RenderTargetIdentifier dst = GetHandleIdentifier(destination);
            RenderTargetIdentifier albedo = hasAlbedo ? albedoRT : fallbackAlbedo;

            return ExecuteKernel(
                cmd,
                new Vector2Int(width, height),
                settings,
                src,
                dst,
                depthRT,
                normalRT,
                albedo
            );
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
            bool hasAlbedo
        ) => Dispatch(cmd, ref renderingData, settings, source, destination, depthRT, normalRT, albedoRT, fallbackAlbedo, hasAlbedo);

        internal bool Dispatch(
            CommandBuffer cmd,
            Vector2Int size,
            Settings settings,
            RenderTargetIdentifier source,
            RenderTargetIdentifier destination,
            RenderTargetIdentifier depthRT,
            RenderTargetIdentifier normalRT,
            RenderTargetIdentifier albedoRT
        )
        {
            if (!IsSupported)
                return false;

            return ExecuteKernel(cmd, size, settings, source, destination, depthRT, normalRT, albedoRT);
        }

        internal bool Execute(
            CommandBuffer cmd,
            Vector2Int size,
            Settings settings,
            RenderTargetIdentifier source,
            RenderTargetIdentifier destination,
            RenderTargetIdentifier depthRT,
            RenderTargetIdentifier normalRT,
            RenderTargetIdentifier albedoRT
        ) => Dispatch(cmd, size, settings, source, destination, depthRT, normalRT, albedoRT);

        internal bool Dispatch(
            CommandBuffer cmd,
            Vector2Int size,
            Settings settings,
            Texture source,
            Texture destination,
            Texture depth,
            Texture normal,
            Texture albedo
        )
        {
            if (!IsSupported)
                return false;

            if (source == null || destination == null || depth == null || normal == null)
            {
                if (source != null && destination != null)
                    cmd.CopyTexture(source, destination);
                return false;
            }

            RenderTargetIdentifier src = new RenderTargetIdentifier(source);
            RenderTargetIdentifier dst = new RenderTargetIdentifier(destination);
            RenderTargetIdentifier depthId = new RenderTargetIdentifier(depth);
            RenderTargetIdentifier normalId = new RenderTargetIdentifier(normal);
            RenderTargetIdentifier albedoId =
                albedo != null
                    ? new RenderTargetIdentifier(albedo)
                    : new RenderTargetIdentifier(Texture2D.blackTexture);

            return ExecuteKernel(cmd, size, settings, src, dst, depthId, normalId, albedoId);
        }

        internal bool Execute(
            CommandBuffer cmd,
            Vector2Int size,
            Settings settings,
            Texture source,
            Texture destination,
            Texture depth,
            Texture normal,
            Texture albedo
        ) => Dispatch(cmd, size, settings, source, destination, depth, normal, albedo);

        private bool ExecuteKernel(
            CommandBuffer cmd,
            Vector2Int size,
            Settings settings,
            RenderTargetIdentifier source,
            RenderTargetIdentifier destination,
            RenderTargetIdentifier depthRT,
            RenderTargetIdentifier normalRT,
            RenderTargetIdentifier albedoRT
        )
        {
            if (size.x <= 0 || size.y <= 0)
            {
                cmd.CopyTexture(source, destination);
                return false;
            }

            Vector4 texSize = new Vector4(size.x, size.y, 0.0f, 0.0f);
            cmd.SetComputeVectorParam(m_Shader, _TexSize, texSize);
            cmd.SetComputeIntParam(m_Shader, _Iterations, Mathf.Max(1, settings.Iterations));
            cmd.SetComputeIntParam(m_Shader, _BaseStep, Mathf.Max(1, settings.BaseStep));
            cmd.SetComputeFloatParam(m_Shader, _SigmaDepth, Mathf.Max(0.0f, settings.SigmaDepth));
            cmd.SetComputeFloatParam(m_Shader, _SigmaNormal, Mathf.Max(0.0f, settings.SigmaNormal));
            cmd.SetComputeFloatParam(m_Shader, _SigmaAlbedo, Mathf.Max(0.0f, settings.SigmaAlbedo));
            cmd.SetComputeFloatParam(m_Shader, _AlbedoWeight, Mathf.Clamp01(settings.AlbedoWeight));
            cmd.SetComputeFloatParam(m_Shader, _MinW, Mathf.Max(1e-6f, settings.MinWeight));

            Vector4 zParams = Shader.GetGlobalVector(_ZBufferParams);
            cmd.SetComputeVectorParam(m_Shader, _WalrZBufferParams, zParams);

            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _NoisyGI, source);
            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _DepthTexture, depthRT);
            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _NormalTexture, normalRT);
            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _AlbedoTexture, albedoRT);
            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _OutGI, destination);

            int dispatchX = Mathf.CeilToInt(size.x / 8.0f);
            int dispatchY = Mathf.CeilToInt(size.y / 8.0f);
            cmd.DispatchCompute(m_Shader, m_Kernel, dispatchX, dispatchY, 1);
            return true;
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
            int height
        )
        {
            if (
                !IsSupported
                || !source.IsValid()
                || !destination.IsValid()
                || !depthHandle.IsValid()
                || !normalHandle.IsValid()
                || width <= 0
                || height <= 0
            )
            {
                if (source.IsValid() && destination.IsValid())
                    cmd.CopyTexture(source, destination);
                return false;
            }

            TextureHandle albedo = hasAlbedo && albedoHandle.IsValid()
                ? albedoHandle
                : fallbackAlbedoHandle;

            if (!albedo.IsValid())
                albedo = fallbackAlbedoHandle;

            Vector4 texSize = new Vector4(width, height, 0.0f, 0.0f);
            cmd.SetComputeVectorParam(m_Shader, _TexSize, texSize);
            cmd.SetComputeIntParam(m_Shader, _Iterations, Mathf.Max(1, settings.Iterations));
            cmd.SetComputeIntParam(m_Shader, _BaseStep, Mathf.Max(1, settings.BaseStep));
            cmd.SetComputeFloatParam(m_Shader, _SigmaDepth, Mathf.Max(0.0f, settings.SigmaDepth));
            cmd.SetComputeFloatParam(m_Shader, _SigmaNormal, Mathf.Max(0.0f, settings.SigmaNormal));
            cmd.SetComputeFloatParam(m_Shader, _SigmaAlbedo, Mathf.Max(0.0f, settings.SigmaAlbedo));
            cmd.SetComputeFloatParam(
                m_Shader,
                _AlbedoWeight,
                Mathf.Clamp01(settings.AlbedoWeight)
            );
            cmd.SetComputeFloatParam(m_Shader, _MinW, Mathf.Max(1e-6f, settings.MinWeight));

            Vector4 zParams = Shader.GetGlobalVector(_ZBufferParams);
            cmd.SetComputeVectorParam(m_Shader, _WalrZBufferParams, zParams);

            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _NoisyGI, source);
            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _DepthTexture, depthHandle);
            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _NormalTexture, normalHandle);
            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _AlbedoTexture, albedo);
            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _OutGI, destination);

            int dispatchX = Mathf.CeilToInt(width / 8.0f);
            int dispatchY = Mathf.CeilToInt(height / 8.0f);
            cmd.DispatchCompute(m_Shader, m_Kernel, dispatchX, dispatchY, 1);
            return true;
        }

        internal bool Execute(
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
            int height
        ) => Dispatch(cmd, settings, source, destination, depthHandle, normalHandle, albedoHandle, fallbackAlbedoHandle, hasAlbedo, width, height);
#endif
    }
}
