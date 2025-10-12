using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

namespace UnityEngine.Rendering.Universal
{
    internal sealed class SSGIEdgeAwareAtrousDenoiser
        : ISSGIDenoiser<SSGIEdgeAwareAtrousDenoiser.Settings>
    {
        private static readonly int _TexSize = Shader.PropertyToID("_TexSize");
        private static readonly int _SigmaColor = Shader.PropertyToID("_SigmaColor");
        private static readonly int _SigmaNormal = Shader.PropertyToID("_SigmaNormal");
        private static readonly int _SigmaDepth = Shader.PropertyToID("_SigmaDepth");
        private static readonly int _AlbedoWeight = Shader.PropertyToID("_AlbedoWeight");
        private static readonly int _MinWeight = Shader.PropertyToID("_MinWeight");
        private static readonly int _EdgeDepthReject = Shader.PropertyToID("_EdgeDepthReject");
        private static readonly int _AtrousStep = Shader.PropertyToID("_AtrousStep");
        private static readonly int _ZBufferParams = Shader.PropertyToID("_ZBufferParams");
        private static readonly int _AtrousZBufferParams = Shader.PropertyToID("_AtrousZBufferParams");
        private static readonly int _Src = Shader.PropertyToID("_Src");
        private static readonly int _DepthTexture = Shader.PropertyToID("_DepthTexture");
        private static readonly int _NormalTexture = Shader.PropertyToID("_NormalTexture");
        private static readonly int _AlbedoTexture = Shader.PropertyToID("_AlbedoTexture");
        private static readonly int _Dst = Shader.PropertyToID("_Dst");

        private ComputeShader m_Shader;
        private int m_Kernel = -1;

        public struct Settings
        {
            public int Iterations;
            public int BaseStep;
            public float SigmaColor;
            public float SigmaNormal;
            public float SigmaDepth;
            public float AlbedoWeight;
            public float MinWeight;
            public float EdgeDepthReject;
        }

        public ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm Algorithm =>
            ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.EdgeAwareAtrous;

        public string DisplayName => "Edge Aware A-Trous";

        public Type SettingsType => typeof(Settings);

        public void UpdateShader(ComputeShader shader)
        {
            m_Shader = shader;
            m_Kernel =
                (shader != null && shader.HasKernel("DenoiseAtrous"))
                    ? shader.FindKernel("DenoiseAtrous")
                    : -1;
        }

        public bool IsSupported =>
            SystemInfo.supportsComputeShaders && m_Shader != null && m_Kernel >= 0;

        public Settings CreateSettings(ScreenSpaceGlobalIlluminationVolume volume)
        {
            if (volume == null)
                return default;

            return new Settings
            {
                Iterations = Mathf.Clamp(volume.atrousIterations.value, 1, 6),
                BaseStep = Mathf.Max(1, volume.atrousBaseStep.value),
                SigmaColor = Mathf.Max(0.0001f, volume.atrousSigmaColor.value),
                SigmaNormal = Mathf.Max(0.0001f, volume.atrousSigmaNormal.value),
                SigmaDepth = Mathf.Max(0.0001f, volume.atrousSigmaDepth.value),
                AlbedoWeight = Mathf.Clamp01(volume.atrousAlbedoWeight.value),
                MinWeight = Mathf.Max(1e-6f, volume.atrousMinWeight.value),
                EdgeDepthReject = Mathf.Max(0.0f, volume.atrousEdgeDepthReject.value),
            };
        }

        private static RenderTargetIdentifier GetHandleIdentifier(RTHandle handle)
        {
            if (handle == null)
                return new RenderTargetIdentifier();

            return handle.rt != null ? new RenderTargetIdentifier(handle.rt) : handle.nameID;
        }

        internal bool Execute(
            CommandBuffer cmd,
            ref RenderingData renderingData,
            Settings settings,
            RTHandle source,
            RTHandle target,
            RTHandle ping,
            RTHandle pong,
            RenderTargetIdentifier depthRT,
            RenderTargetIdentifier normalRT,
            RenderTargetIdentifier albedoRT,
            RenderTargetIdentifier fallbackAlbedo,
            bool hasAlbedo
        )
        {
            if (
                !IsSupported
                || source == null
                || target == null
                || source.rt == null
                || target.rt == null
            )
            {
                cmd.CopyTexture(source, target);
                return false;
            }

            int width = target.rt.width;
            int height = target.rt.height;
            if (width == 0 || height == 0)
            {
                cmd.CopyTexture(source, target);
                return false;
            }

            int iterationCount = Mathf.Max(1, settings.Iterations);
            bool needsIntermediateTargets = iterationCount > 1;

            if (
                needsIntermediateTargets
                && (ping == null || pong == null || ping.rt == null || pong.rt == null)
            )
            {
                cmd.CopyTexture(source, target);
                return false;
            }

            Vector4 texSize = new Vector4(width, height, 0.0f, 0.0f);
            Vector4 zParams = Shader.GetGlobalVector(_ZBufferParams);
            RenderTargetIdentifier currentSource = GetHandleIdentifier(source);
            RenderTargetIdentifier finalTarget = GetHandleIdentifier(target);

            RenderTargetIdentifier pingIdentifier = needsIntermediateTargets
                ? GetHandleIdentifier(ping)
                : default;
            RenderTargetIdentifier pongIdentifier = needsIntermediateTargets
                ? GetHandleIdentifier(pong)
                : default;

            bool shouldUseAlbedo = hasAlbedo && settings.AlbedoWeight > 0.0f;
            if (shouldUseAlbedo)
                cmd.EnableShaderKeyword("USE_ALBEDO_GUIDE");
            else
                cmd.DisableShaderKeyword("USE_ALBEDO_GUIDE");

            cmd.SetComputeVectorParam(m_Shader, _TexSize, texSize);
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
            cmd.SetComputeFloatParam(m_Shader, _MinWeight, Mathf.Max(1e-6f, settings.MinWeight));
            cmd.SetComputeFloatParam(
                m_Shader,
                _EdgeDepthReject,
                Mathf.Max(0.0f, settings.EdgeDepthReject)
            );
            cmd.SetComputeVectorParam(m_Shader, _AtrousZBufferParams, zParams);

            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _DepthTexture, depthRT);
            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _NormalTexture, normalRT);
            cmd.SetComputeTextureParam(
                m_Shader,
                m_Kernel,
                _AlbedoTexture,
                hasAlbedo ? albedoRT : fallbackAlbedo
            );

            int dispatchX = Mathf.CeilToInt(width / 8.0f);
            int dispatchY = Mathf.CeilToInt(height / 8.0f);

            int atrousStep = Mathf.Max(1, settings.BaseStep);
            for (int i = 0; i < iterationCount; ++i)
            {
                bool last = i == iterationCount - 1;
                RenderTargetIdentifier destination = last
                    ? finalTarget
                    : ((i & 1) == 0 ? pingIdentifier : pongIdentifier);

                cmd.SetComputeIntParam(m_Shader, _AtrousStep, Mathf.Max(1, atrousStep));
                cmd.SetComputeTextureParam(m_Shader, m_Kernel, _Src, currentSource);
                cmd.SetComputeTextureParam(m_Shader, m_Kernel, _Dst, destination);

                cmd.DispatchCompute(m_Shader, m_Kernel, dispatchX, dispatchY, 1);

                currentSource = destination;
                atrousStep <<= 1;
            }

            if (shouldUseAlbedo)
                cmd.DisableShaderKeyword("USE_ALBEDO_GUIDE");

            return true;
        }

#if UNITY_6000_0_OR_NEWER
        internal bool Execute(
            CommandBuffer cmd,
            Settings settings,
            TextureHandle source,
            TextureHandle target,
            TextureHandle ping,
            TextureHandle pong,
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
                || !target.IsValid()
                || width <= 0
                || height <= 0
            )
            {
                if (source.IsValid() && target.IsValid())
                    cmd.CopyTexture(source, target);
                return false;
            }

            int iterationCount = Mathf.Max(1, settings.Iterations);
            bool needsIntermediateTargets = iterationCount > 1;

            if (
                needsIntermediateTargets
                && (
                    !ping.IsValid()
                    || !pong.IsValid()
                )
            )
            {
                cmd.CopyTexture(source, target);
                return false;
            }

            Vector4 texSize = new Vector4(width, height, 0.0f, 0.0f);
            Vector4 zParams = Shader.GetGlobalVector(_ZBufferParams);
            TextureHandle currentSource = source;
            TextureHandle finalTarget = target;
            TextureHandle pingHandle = ping;
            TextureHandle pongHandle = pong;

            bool shouldUseAlbedo = hasAlbedo && settings.AlbedoWeight > 0.0f;
            if (shouldUseAlbedo)
                cmd.EnableShaderKeyword("USE_ALBEDO_GUIDE");
            else
                cmd.DisableShaderKeyword("USE_ALBEDO_GUIDE");

            cmd.SetComputeVectorParam(m_Shader, _TexSize, texSize);
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
            cmd.SetComputeFloatParam(m_Shader, _MinWeight, Mathf.Max(1e-6f, settings.MinWeight));
            cmd.SetComputeFloatParam(
                m_Shader,
                _EdgeDepthReject,
                Mathf.Max(0.0f, settings.EdgeDepthReject)
            );
            cmd.SetComputeVectorParam(m_Shader, _AtrousZBufferParams, zParams);

            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _DepthTexture, depthHandle);
            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _NormalTexture, normalHandle);
            cmd.SetComputeTextureParam(
                m_Shader,
                m_Kernel,
                _AlbedoTexture,
                shouldUseAlbedo ? albedoHandle : fallbackAlbedoHandle
            );

            int dispatchX = Mathf.CeilToInt(width / 8.0f);
            int dispatchY = Mathf.CeilToInt(height / 8.0f);

            int atrousStep = Mathf.Max(1, settings.BaseStep);
            for (int i = 0; i < iterationCount; ++i)
            {
                bool last = i == iterationCount - 1;
                TextureHandle destination = last
                    ? finalTarget
                    : ((i & 1) == 0 ? pingHandle : pongHandle);

                cmd.SetComputeIntParam(m_Shader, _AtrousStep, Mathf.Max(1, atrousStep));
                cmd.SetComputeTextureParam(m_Shader, m_Kernel, _Src, currentSource);
                cmd.SetComputeTextureParam(m_Shader, m_Kernel, _Dst, destination);

                cmd.DispatchCompute(m_Shader, m_Kernel, dispatchX, dispatchY, 1);

                currentSource = destination;
                atrousStep <<= 1;
            }

            if (shouldUseAlbedo)
                cmd.DisableShaderKeyword("USE_ALBEDO_GUIDE");

            return true;
        }
#endif
    }
}
