using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

namespace UnityEngine.Rendering.Universal
{
    internal sealed class SSGIEdgeAwareAtrousDenoiserFast
        : ISSGIDenoiser<SSGIEdgeAwareAtrousDenoiserFast.Settings>
    {
        private static readonly int _TexSize = Shader.PropertyToID("_TexSize");
        private static readonly int _SigmaColor = Shader.PropertyToID("_SigmaColor");
        private static readonly int _SigmaNormal = Shader.PropertyToID("_SigmaNormal");
        private static readonly int _SigmaDepth = Shader.PropertyToID("_SigmaDepth");
        private static readonly int _AlbedoWeight = Shader.PropertyToID("_AlbedoWeight");
        private static readonly int _MinWeight = Shader.PropertyToID("_MinWeight");
        private static readonly int _EdgeDepthReject = Shader.PropertyToID("_EdgeDepthReject");
        private static readonly int _CompactSize = Shader.PropertyToID("_CompactSize");
        private static readonly int _IterationIndex = Shader.PropertyToID("_IterationIndex");
        private static readonly int _DepthTexture = Shader.PropertyToID("_DepthTexture");
        private static readonly int _NormalTexture = Shader.PropertyToID("_NormalTexture");
        private static readonly int _AlbedoTexture = Shader.PropertyToID("_AlbedoTexture");
        private static readonly int _Src = Shader.PropertyToID("_Src");
        private static readonly int _Dst = Shader.PropertyToID("_Dst");
        private static readonly int _ZBufferParams = Shader.PropertyToID("_ZBufferParams");
        private static readonly int _AtrousZBufferParams = Shader.PropertyToID("_AtrousZBufferParams");

        private const string k_ShaderResource = "SSGI_EdgeAwareAtrous_Fast";
        private const string k_KernelName = "DenoiseAtrous";

        private ComputeShader m_Shader;
        private int m_Kernel = -1;
        private bool m_WarnedMissingShader;
        private bool m_WarnedMissingKernel;

        public struct Settings
        {
            public int Iterations;
            public float SigmaColor;
            public float SigmaNormal;
            public float SigmaDepth;
            public float AlbedoWeight;
            public float MinWeight;
            public float EdgeDepthReject;
        }

        public SSGIEdgeAwareAtrousDenoiserFast()
        {
            profilingSampler = new ProfilingSampler("SSGI EdgeAware Atrous Fast");
        }

        public override ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm Algorithm =>
            ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.EdgeAwareAtrous;

        public override string DisplayName => "Edge Aware A-Trous (Fast)";

        public override Type SettingsType => typeof(Settings);

        public override bool IsDefaultVariant => false;

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
                    "Screen Space Global Illumination URP: Missing compute shader 'SSGI_EdgeAwareAtrous_Fast'. Edge Aware A-Trous will fall back to copy."
                );
                m_WarnedMissingShader = true;
            }
            else if (!m_WarnedMissingKernel && m_Shader != null && m_Kernel < 0)
            {
                Debug.LogWarning(
                    "Screen Space Global Illumination URP: Compute shader 'SSGI_EdgeAwareAtrous_Fast' does not expose kernel 'DenoiseAtrous'. Falling back to copy."
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
            ScreenSpaceGlobalIlluminationURP.ScreenSpaceGlobalIlluminationPass pass,
            ScreenSpaceGlobalIlluminationVolume volume
        )
        {
            pass.edgeAwareAtrousDenoiserFast = this;
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

            int iterations = Mathf.Clamp(settings.Iterations, 1, 6);
            bool needsPingPong = iterations > 1;

            if (
                needsPingPong
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
            RenderTargetIdentifier pingId = needsPingPong ? GetHandleIdentifier(ping) : default;
            RenderTargetIdentifier pongId = needsPingPong ? GetHandleIdentifier(pong) : default;

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

            int groupSize = 16;
            const int phaseCount = 4;

            for (int iteration = 0; iteration < iterations; ++iteration)
            {
                bool lastIteration = iteration == iterations - 1;

                RenderTargetIdentifier iterationDestination;
                if (lastIteration)
                    iterationDestination = finalTarget;
                else
                    iterationDestination = (iteration & 1) == 0 ? pingId : pongId;

                RenderTargetIdentifier iterationSource = currentSource;

                int compactWidth = ComputeCompactDimension(width, iteration);
                int compactHeight = ComputeCompactDimension(height, iteration);
                if (compactWidth <= 0 || compactHeight <= 0)
                {
                    cmd.CopyTexture(source, target);
                    return false;
                }

                int dispatchX = Mathf.Max(1, Mathf.CeilToInt(compactWidth / (float)groupSize));
                int dispatchY = Mathf.Max(1, Mathf.CeilToInt(compactHeight / (float)groupSize));

                cmd.SetComputeVectorParam(
                    m_Shader,
                    _CompactSize,
                    new Vector4(compactWidth, compactHeight, 0.0f, 0.0f)
                );
                cmd.SetComputeIntParam(m_Shader, _IterationIndex, iteration);
                cmd.SetComputeTextureParam(m_Shader, m_Kernel, _Src, iterationSource);

                cmd.SetComputeTextureParam(m_Shader, m_Kernel, _Dst, iterationDestination);
                cmd.DispatchCompute(m_Shader, m_Kernel, dispatchX, dispatchY, phaseCount);

                currentSource = iterationDestination;
            }

            if (shouldUseAlbedo)
                cmd.DisableShaderKeyword("USE_ALBEDO_GUIDE");

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
            bool hasAlbedo
        ) => Dispatch(cmd, ref renderingData, settings, source, target, ping, pong, depthRT, normalRT, albedoRT, fallbackAlbedo, hasAlbedo);

        private static RenderTargetIdentifier GetHandleIdentifier(RTHandle handle)
        {
            if (handle == null)
                return new RenderTargetIdentifier();

            return handle.rt != null ? new RenderTargetIdentifier(handle.rt) : handle.nameID;
        }

        private static int RemoveBit(int value, int bit)
        {
            int lowMask = (1 << bit) - 1;
            int low = value & lowMask;
            int high = value >> (bit + 1);
            return (high << bit) | low;
        }

        private static int ComputeCompactDimension(int size, int iteration)
        {
            if (size <= 0)
                return 0;

            int maxCoord = RemoveBit(Mathf.Max(size, 1) - 1, Mathf.Clamp(iteration, 0, 15));
            return Mathf.Max(1, maxCoord + 1);
        }

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

            int iterations = Mathf.Clamp(settings.Iterations, 1, 6);
            bool needsPingPong = iterations > 1;

            if (
                needsPingPong
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

            int groupSize = 16;
            const int phaseCount = 4;

            for (int iteration = 0; iteration < iterations; ++iteration)
            {
                bool lastIteration = iteration == iterations - 1;

                TextureHandle iterationDestination = lastIteration
                    ? finalTarget
                    : ((iteration & 1) == 0 ? pingHandle : pongHandle);

                TextureHandle iterationSource = currentSource;

                int compactWidth = ComputeCompactDimension(width, iteration);
                int compactHeight = ComputeCompactDimension(height, iteration);
                if (compactWidth <= 0 || compactHeight <= 0)
                {
                    cmd.CopyTexture(source, target);
                    if (shouldUseAlbedo)
                        cmd.DisableShaderKeyword("USE_ALBEDO_GUIDE");
                    return false;
                }

                int dispatchX = Mathf.Max(1, Mathf.CeilToInt(compactWidth / (float)groupSize));
                int dispatchY = Mathf.Max(1, Mathf.CeilToInt(compactHeight / (float)groupSize));

                cmd.SetComputeVectorParam(
                    m_Shader,
                    _CompactSize,
                    new Vector4(compactWidth, compactHeight, width, height)
                );
                cmd.SetComputeIntParam(m_Shader, _IterationIndex, iteration % phaseCount);

                cmd.SetComputeTextureParam(m_Shader, m_Kernel, _Src, iterationSource);
                cmd.SetComputeTextureParam(m_Shader, m_Kernel, _Dst, iterationDestination);

                cmd.DispatchCompute(m_Shader, m_Kernel, dispatchX, dispatchY, 1);

                currentSource = iterationDestination;
            }

            if (shouldUseAlbedo)
                cmd.DisableShaderKeyword("USE_ALBEDO_GUIDE");

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
            int height
        ) => Dispatch(cmd, settings, source, target, ping, pong, depthHandle, normalHandle, albedoHandle, fallbackAlbedoHandle, hasAlbedo, width, height);
#endif
    }
}
