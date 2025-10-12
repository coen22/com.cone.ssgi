using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEngine.Rendering.Universal
{
    internal sealed class SSGIAdaptiveLutDenoiser
        : ISSGIDenoiser<SSGIAdaptiveLutDenoiser.Settings>
    {
        private static readonly int _TexSize = Shader.PropertyToID("_TexSize");
        private static readonly int _DepthThreshold = Shader.PropertyToID("_DepthThreshold");
        private static readonly int _NormalThreshold = Shader.PropertyToID("_NormalThreshold");
        private static readonly int _EdgeSensitivity = Shader.PropertyToID("_EdgeSensitivity");
        private static readonly int _MaxRadius = Shader.PropertyToID("_MaxRadius");
        private static readonly int _NoisySSGI = Shader.PropertyToID("_NoisySSGI");
        private static readonly int _DepthTexture = Shader.PropertyToID("_DepthTexture");
        private static readonly int _NormalTexture = Shader.PropertyToID("_NormalTexture");
        private static readonly int _AlbedoTexture = Shader.PropertyToID("_AlbedoTexture");
        private static readonly int _OutDenoised = Shader.PropertyToID("_OutDenoised");

        private ComputeShader m_Shader;
        private int m_Kernel = -1;

        public struct Settings
        {
            public int MaxRadius;
            public float EdgeSensitivity;
            public float DepthReject;
            public float NormalReject;
        }

        public ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm Algorithm =>
            ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.EdgeAdaptiveLut;

        public string DisplayName => "Edge Adaptive LUT";

        public Type SettingsType => typeof(Settings);

        public void UpdateShader(ComputeShader shader)
        {
            m_Shader = shader;
            m_Kernel =
                (shader != null && shader.HasKernel("Denoise")) ? shader.FindKernel("Denoise") : -1;
        }

        public bool IsSupported =>
            SystemInfo.supportsComputeShaders && m_Shader != null && m_Kernel >= 0;

        public Settings CreateSettings(ScreenSpaceGlobalIlluminationVolume volume)
        {
            if (volume == null)
                return default;

            return new Settings
            {
                MaxRadius = Mathf.Clamp(volume.adaptiveMaxRadius.value, 1, 4),
                EdgeSensitivity = Mathf.Max(0.1f, volume.adaptiveEdgeSensitivity.value),
                DepthReject = Mathf.Max(1e-4f, volume.adaptiveDepthThreshold.value),
                NormalReject = Mathf.Clamp(volume.adaptiveNormalThreshold.value, 0.0f, 1.0f),
            };
        }

        internal bool Execute(
            CommandBuffer cmd,
            ref RenderingData renderingData,
            Settings settings,
            RTHandle source,
            RTHandle destination,
            RenderTargetIdentifier depthRT,
            RenderTargetIdentifier normalRT,
            RenderTargetIdentifier albedoRT
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

            int maxRadius = Mathf.Clamp(settings.MaxRadius, 1, 4);

            cmd.SetComputeVectorParam(m_Shader, _TexSize, new Vector4(width, height, 0.0f, 0.0f));
            cmd.SetComputeFloatParam(
                m_Shader,
                _DepthThreshold,
                Mathf.Max(1e-4f, settings.DepthReject)
            );
            cmd.SetComputeFloatParam(
                m_Shader,
                _NormalThreshold,
                Mathf.Clamp(settings.NormalReject, 0.0f, 1.0f)
            );
            cmd.SetComputeFloatParam(
                m_Shader,
                _EdgeSensitivity,
                Mathf.Max(0.1f, settings.EdgeSensitivity)
            );
            cmd.SetComputeIntParam(m_Shader, _MaxRadius, maxRadius);

            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _NoisySSGI, source);
            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _DepthTexture, depthRT);
            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _NormalTexture, normalRT);
            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _AlbedoTexture, albedoRT);
            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _OutDenoised, destination);

            int dispatchX = Mathf.CeilToInt(width / 8.0f);
            int dispatchY = Mathf.CeilToInt(height / 8.0f);
            cmd.DispatchCompute(m_Shader, m_Kernel, dispatchX, dispatchY, 1);
            return true;
        }
    }
}
