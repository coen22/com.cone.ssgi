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
        private static readonly int _AtrousZBufferParams = Shader.PropertyToID(
            "_AtrousZBufferParams"
        );
        private static readonly int _Src = Shader.PropertyToID("_Src");
        private static readonly int _DepthTexture = Shader.PropertyToID("_DepthTexture");
        private static readonly int _NormalTexture = Shader.PropertyToID("_NormalTexture");
        private static readonly int _AlbedoTexture = Shader.PropertyToID("_AlbedoTexture");
        private static readonly int _Dst = Shader.PropertyToID("_Dst");

        private const string k_ShaderResource = "SSGI_EdgeAwareAtrous";
        private const string k_KernelName = "DenoiseAtrous";

        private ComputeShader m_Shader;
        private int m_Kernel = -1;
        private bool m_WarnedMissingShader;
        private bool m_WarnedMissingKernel;
        private LocalKeyword m_UseAlbedoGuideKeyword;

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

        public SSGIEdgeAwareAtrousDenoiser()
        {
            profilingSampler = new ProfilingSampler("SSGI EdgeAware Atrous");
        }

        public override ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm Algorithm =>
            ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.EdgeAwareAtrous;

        public override string DisplayName => "Edge Aware A-Trous";

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
                m_UseAlbedoGuideKeyword =
                    shader != null ? new LocalKeyword(shader, "USE_ALBEDO_GUIDE") : default;
            }

#if UNITY_EDITOR || DEBUG
            if (!m_WarnedMissingShader && m_Shader == null)
            {
                Debug.LogWarning(
                    "Screen Space Global Illumination URP: Missing compute shader 'SSGI_EdgeAwareAtrous'. Edge Aware A-Trous will fall back to copy."
                );
                m_WarnedMissingShader = true;
            }
            else if (!m_WarnedMissingKernel && m_Shader != null && m_Kernel < 0)
            {
                Debug.LogWarning(
                    "Screen Space Global Illumination URP: Compute shader 'SSGI_EdgeAwareAtrous' is missing kernel 'DenoiseAtrous'. Falling back to copy."
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

        internal override void ConfigurePass(
            ScreenSpaceGlobalIlluminationURP feature,
            ScreenSpaceGlobalIlluminationPass pass,
            ScreenSpaceGlobalIlluminationVolume volume
        )
        {
            pass.edgeAwareAtrousDenoiser = this;

            if (volume != null && volume.fastAtrousSchedule.value)
            {
                var fast = feature.AcquireDenoiser<SSGIEdgeAwareAtrousDenoiserFast>();
                fast?.ConfigurePass(feature, pass, volume);
            }
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
            RTHandle target,
            RTHandle ping,
            RTHandle pong,
            RenderTargetIdentifier depthRT,
            RenderTargetIdentifier normalRT,
            RenderTargetIdentifier albedoRT,
            RenderTargetIdentifier fallbackAlbedo,
            bool hasAlbedo,
            Vector4 zParams
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
            RenderTargetIdentifier currentSource = GetHandleIdentifier(source);
            RenderTargetIdentifier finalTarget = GetHandleIdentifier(target);

            RenderTargetIdentifier pingIdentifier = needsIntermediateTargets
                ? GetHandleIdentifier(ping)
                : default;
            RenderTargetIdentifier pongIdentifier = needsIntermediateTargets
                ? GetHandleIdentifier(pong)
                : default;

            bool shouldUseAlbedo = hasAlbedo && settings.AlbedoWeight > 0.0f;
            cmd.SetKeyword(m_Shader, m_UseAlbedoGuideKeyword, shouldUseAlbedo);

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
                cmd.SetKeyword(m_Shader, m_UseAlbedoGuideKeyword, false);

            return true;
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
            bool hasAlbedo,
            Vector4 zParams
        ) =>
            Dispatch(
                cmd,
                ref renderingData,
                settings,
                source,
                target,
                ping,
                pong,
                depthRT,
                normalRT,
                albedoRT,
                fallbackAlbedo,
                hasAlbedo,
                zParams
            );

#if UNITY_6000_0_OR_NEWER
        internal bool Dispatch(
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
            int height,
            Vector4 zParams
        )
        {
            if (!IsSupported || !source.IsValid() || !target.IsValid() || width <= 0 || height <= 0)
            {
                if (source.IsValid() && target.IsValid())
                    cmd.CopyTexture(source, target);
                return false;
            }

            int iterationCount = Mathf.Max(1, settings.Iterations);
            bool needsIntermediateTargets = iterationCount > 1;

            if (needsIntermediateTargets && (!ping.IsValid() || !pong.IsValid()))
            {
                cmd.CopyTexture(source, target);
                return false;
            }

            Vector4 texSize = new Vector4(width, height, 0.0f, 0.0f);
            TextureHandle currentSource = source;
            TextureHandle finalTarget = target;
            TextureHandle pingHandle = ping;
            TextureHandle pongHandle = pong;

            bool shouldUseAlbedo = hasAlbedo && settings.AlbedoWeight > 0.0f;
            cmd.SetKeyword(m_Shader, m_UseAlbedoGuideKeyword, shouldUseAlbedo);

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
                cmd.SetKeyword(m_Shader, m_UseAlbedoGuideKeyword, false);

            return true;
        }

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
            int height,
            Vector4 zParams
        ) =>
            Dispatch(
                cmd,
                settings,
                source,
                target,
                ping,
                pong,
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
