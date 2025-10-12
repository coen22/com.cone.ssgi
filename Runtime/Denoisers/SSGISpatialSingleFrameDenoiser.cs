using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEngine.Rendering.Universal
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
        private static readonly int _ZBufferParams = Shader.PropertyToID("_ZBufferParams");
        private static readonly int _NoisySSGI = Shader.PropertyToID("_NoisySSGI");
        private static readonly int _DepthTexture = Shader.PropertyToID("_DepthTexture");
        private static readonly int _NormalTexture = Shader.PropertyToID("_NormalTexture");
        private static readonly int _AlbedoTexture = Shader.PropertyToID("_AlbedoTexture");
        private static readonly int _OutDenoised = Shader.PropertyToID("_OutDenoised");

        private ComputeShader m_Shader;
        private int m_Kernel = -1;

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

        public ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm Algorithm =>
            ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.SingleFrame;

        public string DisplayName => "Single Frame";

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
                Radius = Mathf.Max(1.0f, volume.singleFrameRadius.value),
                SigmaColor = Mathf.Max(0.0001f, volume.singleFrameSigmaColor.value),
                SigmaNormal = Mathf.Max(0.0001f, volume.singleFrameSigmaNormal.value),
                SigmaDepth = Mathf.Max(0.0001f, volume.singleFrameSigmaDepth.value),
                AlbedoWeight = Mathf.Clamp01(volume.singleFrameAlbedoWeight.value),
                LumaWeight = Mathf.Clamp01(volume.singleFrameLumaWeight.value),
                MinWeight = Mathf.Max(1e-6f, volume.singleFrameMinWeight.value),
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

            Vector4 zParams = Shader.GetGlobalVector(_ZBufferParams);
            cmd.SetComputeVectorParam(m_Shader, _ZBufferParams, zParams);

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
