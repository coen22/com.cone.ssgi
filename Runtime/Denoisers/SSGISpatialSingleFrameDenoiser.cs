using System;
using Cone.SSGI;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

namespace Cone.SSGI.Denoisers
{
    internal sealed class SSGISpatialSingleFrameDenoiser
        : ISSGIDenoiser<SSGISpatialSingleFrameDenoiser.Settings>
    {
        private static readonly int _TexSize = Shader.PropertyToID("_TexSize");
        private static readonly int _Radius = Shader.PropertyToID("_Radius");
        private static readonly int _SigmaColor = Shader.PropertyToID("_SigmaColor");
        private static readonly int _SigmaNormal = Shader.PropertyToID("_SigmaNormal");
        private static readonly int _SigmaDepth = Shader.PropertyToID("_SigmaDepth");
        private static readonly int _AlbedoWeight = Shader.PropertyToID("_AlbedoWeight");
        private static readonly int _LumaWeight = Shader.PropertyToID("_LumaWeight");
        private static readonly int _MinWeight = Shader.PropertyToID("_MinWeight");
        private static readonly int _NormalsAreWorld = Shader.PropertyToID("_NormalsAreWorld");
        private static readonly int _SpatialZBufferParams = Shader.PropertyToID(
            "_SpatialZBufferParams"
        );
        private static readonly int _NoisySSGI = Shader.PropertyToID("_NoisySSGI");
        private static readonly int _DepthTexture = Shader.PropertyToID("_DepthTexture");
        private static readonly int _NormalTexture = Shader.PropertyToID("_NormalTexture");
        private static readonly int _AlbedoTexture = Shader.PropertyToID("_AlbedoTexture");
        private static readonly int _OutDenoised = Shader.PropertyToID("_OutDenoised");

        private const string k_ShaderResource = "SSGI_SpatialDenoiser";
        private const string k_KernelName = "Denoise";
        private const int kGroupSizeX = 8;
        private const int kGroupSizeY = 8;

        private ComputeShader m_Shader;
        private int m_Kernel = -1;
        private bool m_WarnedMissingShader;
        private bool m_WarnedMissingKernel;

        public struct Settings
        {
            public float Radius;
            public float SigmaColor;
            public float SigmaNormal;
            public float SigmaDepth;
            public float AlbedoWeight;
            public float LumaWeight;
            public float MinWeight;
        }

        public SSGISpatialSingleFrameDenoiser()
        {
            profilingSampler = new ProfilingSampler("SSGI Spatial Single Frame");
        }

        public override ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm Algorithm =>
            ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.SingleFrame;

        public override string DisplayName => "Single Frame";

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
                    "Screen Space Global Illumination URP: Missing compute shader 'SSGI_SpatialDenoiser'. Single Frame denoiser will fall back to simple copy."
                );
                m_WarnedMissingShader = true;
            }
            else if (!m_WarnedMissingKernel && m_Shader != null && m_Kernel < 0)
            {
                Debug.LogWarning(
                    "Screen Space Global Illumination URP: Compute shader 'SSGI_SpatialDenoiser' does not expose kernel 'Denoise'. Falling back to copy."
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
                Radius = Mathf.Max(1.0f, volume.singleFrameRadius.value),
                SigmaColor = Mathf.Max(0.0001f, volume.singleFrameSigmaColor.value),
                SigmaNormal = Mathf.Max(0.0001f, volume.singleFrameSigmaNormal.value),
                SigmaDepth = Mathf.Max(0.0001f, volume.singleFrameSigmaDepth.value),
                AlbedoWeight = Mathf.Clamp01(volume.singleFrameAlbedoWeight.value),
                LumaWeight = Mathf.Clamp01(volume.singleFrameLumaWeight.value),
                MinWeight = Mathf.Max(1e-6f, volume.singleFrameMinWeight.value),
            };
        }

        internal override void ConfigurePass(
            ScreenSpaceGlobalIlluminationURP feature,
            ScreenSpaceGlobalIlluminationPass pass,
            ScreenSpaceGlobalIlluminationVolume volume
        )
        {
            pass.singleFrameDenoiser = this;
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
            Vector4 zParams
        )
        {
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

            cmd.SetComputeVectorParam(m_Shader, _TexSize, new Vector4(width, height, 0.0f, 0.0f));
            cmd.SetComputeFloatParam(m_Shader, _Radius, Mathf.Max(1.0f, settings.Radius));
            cmd.SetComputeFloatParam(
                m_Shader,
                _SigmaColor,
                Mathf.Max(0.0001f, settings.SigmaColor)
            );
            cmd.SetComputeFloatParam(
                m_Shader,
                _SigmaNormal,
                Mathf.Max(0.0001f, settings.SigmaNormal)
            );
            cmd.SetComputeFloatParam(
                m_Shader,
                _SigmaDepth,
                Mathf.Max(0.0001f, settings.SigmaDepth)
            );
            cmd.SetComputeFloatParam(m_Shader, _AlbedoWeight, Mathf.Clamp01(settings.AlbedoWeight));
            cmd.SetComputeFloatParam(m_Shader, _LumaWeight, Mathf.Clamp01(settings.LumaWeight));
            cmd.SetComputeFloatParam(m_Shader, _MinWeight, Mathf.Max(1e-6f, settings.MinWeight));
            cmd.SetComputeIntParam(m_Shader, _NormalsAreWorld, 0);
            cmd.SetComputeVectorParam(m_Shader, _SpatialZBufferParams, zParams);

            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _NoisySSGI, source);
            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _DepthTexture, depthRT);
            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _NormalTexture, normalRT);
            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _AlbedoTexture, albedoRT);
            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _OutDenoised, destination);

            int dispatchX = Mathf.CeilToInt(width / (float)kGroupSizeX);
            int dispatchY = Mathf.CeilToInt(height / (float)kGroupSizeY);
            cmd.DispatchCompute(m_Shader, m_Kernel, dispatchX, dispatchY, 1);
            return true;
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
            Vector4 zParams
        ) =>
            Dispatch(
                cmd,
                ref renderingData,
                settings,
                source,
                destination,
                depthRT,
                normalRT,
                albedoRT,
                zParams
            );

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
            Vector4 zParams
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

            TextureHandle albedoTexture =
                hasAlbedo && albedoHandle.IsValid() ? albedoHandle : fallbackAlbedoHandle;

            cmd.SetComputeVectorParam(m_Shader, _TexSize, new Vector4(width, height, 0.0f, 0.0f));
            cmd.SetComputeFloatParam(m_Shader, _Radius, Mathf.Max(1.0f, settings.Radius));
            cmd.SetComputeFloatParam(
                m_Shader,
                _SigmaColor,
                Mathf.Max(0.0001f, settings.SigmaColor)
            );
            cmd.SetComputeFloatParam(
                m_Shader,
                _SigmaNormal,
                Mathf.Max(0.0001f, settings.SigmaNormal)
            );
            cmd.SetComputeFloatParam(
                m_Shader,
                _SigmaDepth,
                Mathf.Max(0.0001f, settings.SigmaDepth)
            );
            cmd.SetComputeFloatParam(
                m_Shader,
                _AlbedoWeight,
                hasAlbedo ? Mathf.Clamp01(settings.AlbedoWeight) : 0.0f
            );
            cmd.SetComputeFloatParam(m_Shader, _LumaWeight, Mathf.Clamp01(settings.LumaWeight));
            cmd.SetComputeFloatParam(m_Shader, _MinWeight, Mathf.Max(1e-6f, settings.MinWeight));
            cmd.SetComputeIntParam(m_Shader, _NormalsAreWorld, 0);
            cmd.SetComputeVectorParam(m_Shader, _SpatialZBufferParams, zParams);

            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _NoisySSGI, source);
            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _DepthTexture, depthHandle);
            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _NormalTexture, normalHandle);
            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _AlbedoTexture, albedoTexture);
            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _OutDenoised, destination);

            int dispatchX = Mathf.CeilToInt(width / (float)kGroupSizeX);
            int dispatchY = Mathf.CeilToInt(height / (float)kGroupSizeY);
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
            int height,
            Vector4 zParams
        ) =>
            Dispatch(
                cmd,
                settings,
                source,
                destination,
                depthHandle,
                normalHandle,
                albedoHandle,
                fallbackAlbedoHandle,
                hasAlbedo,
                width,
                height,
                zParams
            );
#endif
    }
}
