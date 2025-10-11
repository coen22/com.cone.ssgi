using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEngine.Rendering.Universal
{
    internal sealed class SSGIEdgeAwareAtrousDenoiserFast
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
        private static readonly int _Phase = Shader.PropertyToID("_Phase");
        private static readonly int _DepthTexture = Shader.PropertyToID("_DepthTexture");
        private static readonly int _NormalTexture = Shader.PropertyToID("_NormalTexture");
        private static readonly int _AlbedoTexture = Shader.PropertyToID("_AlbedoTexture");
        private static readonly int _Src = Shader.PropertyToID("_Src");
        private static readonly int _Dst = Shader.PropertyToID("_Dst");
        private static readonly int _ZBufferParams = Shader.PropertyToID("_ZBufferParams");

        private ComputeShader m_Shader;
        private int m_Kernel = -1;

        internal struct Settings
        {
            public int Iterations;
            public float SigmaColor;
            public float SigmaNormal;
            public float SigmaDepth;
            public float AlbedoWeight;
            public float MinWeight;
            public float EdgeDepthReject;
        }

        internal void UpdateShader(ComputeShader shader)
        {
            m_Shader = shader;
            m_Kernel = (shader != null && shader.HasKernel("DenoiseAtrous")) ? shader.FindKernel("DenoiseAtrous") : -1;
        }

        internal bool IsSupported => SystemInfo.supportsComputeShaders && m_Shader != null && m_Kernel >= 0;

        internal bool Execute(CommandBuffer cmd,
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
                              bool hasAlbedo)
        {
            if (!IsSupported || source == null || target == null || source.rt == null || target.rt == null)
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

            if (needsPingPong && (ping == null || pong == null || ping.rt == null || pong.rt == null))
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
            cmd.SetComputeFloatParam(m_Shader, _SigmaColor, Mathf.Max(0.0001f, settings.SigmaColor));
            cmd.SetComputeFloatParam(m_Shader, _SigmaNormal, Mathf.Max(0.0001f, settings.SigmaNormal));
            cmd.SetComputeFloatParam(m_Shader, _SigmaDepth, Mathf.Max(0.0001f, settings.SigmaDepth));
            cmd.SetComputeFloatParam(m_Shader, _AlbedoWeight, Mathf.Clamp01(settings.AlbedoWeight));
            cmd.SetComputeFloatParam(m_Shader, _MinWeight, Mathf.Max(1e-6f, settings.MinWeight));
            cmd.SetComputeFloatParam(m_Shader, _EdgeDepthReject, Mathf.Max(0.0f, settings.EdgeDepthReject));
            cmd.SetComputeVectorParam(m_Shader, _ZBufferParams, zParams);

            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _DepthTexture, depthRT);
            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _NormalTexture, normalRT);
            cmd.SetComputeTextureParam(m_Shader, m_Kernel, _AlbedoTexture, hasAlbedo ? albedoRT : fallbackAlbedo);

            int groupSize = 16;
            int phaseCount = 4;

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

                cmd.SetComputeVectorParam(m_Shader, _CompactSize, new Vector4(compactWidth, compactHeight, 0.0f, 0.0f));
                cmd.SetComputeIntParam(m_Shader, _IterationIndex, iteration);
                cmd.SetComputeTextureParam(m_Shader, m_Kernel, _Src, iterationSource);

                for (int phase = 0; phase < phaseCount; ++phase)
                {
                    cmd.SetComputeIntParam(m_Shader, _Phase, phase);
                    cmd.SetComputeTextureParam(m_Shader, m_Kernel, _Dst, iterationDestination);

                    cmd.DispatchCompute(m_Shader, m_Kernel, dispatchX, dispatchY, 1);
                }

                currentSource = iterationDestination;
            }

            if (shouldUseAlbedo)
                cmd.DisableShaderKeyword("USE_ALBEDO_GUIDE");

            return true;
        }

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

            int maxCoord = RemoveBit(size - 1, iteration);
            return Mathf.Max(1, maxCoord + 1);
        }
    }
}
