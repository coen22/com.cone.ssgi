using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEngine.Rendering.Universal
{
    internal sealed class SSGIWalrDenoiser
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
        private static readonly int _NoisyGI = Shader.PropertyToID("_NoisyGI");
        private static readonly int _DepthTexture = Shader.PropertyToID("_DepthTexture");
        private static readonly int _NormalTexture = Shader.PropertyToID("_NormalTexture");
        private static readonly int _AlbedoTexture = Shader.PropertyToID("_AlbedoTexture");
        private static readonly int _OutGI = Shader.PropertyToID("_OutGI");

        private ComputeShader m_Shader;
        private int m_Kernel = -1;

        internal struct Settings
        {
            public int Iterations;
            public int BaseStep;
            public float SigmaDepth;
            public float SigmaNormal;
            public float SigmaAlbedo;
            public float AlbedoWeight;
            public float MinWeight;
        }

        internal void UpdateShader(ComputeShader shader)
        {
            m_Shader = shader;
            m_Kernel = (shader != null && shader.HasKernel("WALR")) ? shader.FindKernel("WALR") : -1;
        }

        internal bool IsSupported => SystemInfo.supportsComputeShaders && m_Shader != null && m_Kernel >= 0;

        private static RenderTargetIdentifier GetHandleIdentifier(RTHandle handle)
        {
            if (handle == null)
                return new RenderTargetIdentifier();

            return handle.rt != null ? new RenderTargetIdentifier(handle.rt) : handle.nameID;
        }

        internal bool Execute(CommandBuffer cmd,
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
            _ = renderingData;

            if (!IsSupported || source == null || destination == null || source.rt == null || destination.rt == null)
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

            return Dispatch(cmd,
                            new Vector2Int(width, height),
                            settings,
                            src,
                            dst,
                            depthRT,
                            normalRT,
                            albedo);
        }

        internal bool Execute(CommandBuffer cmd,
                              Vector2Int size,
                              Settings settings,
                              RenderTargetIdentifier source,
                              RenderTargetIdentifier destination,
                              RenderTargetIdentifier depthRT,
                              RenderTargetIdentifier normalRT,
                              RenderTargetIdentifier albedoRT)
        {
            if (!IsSupported)
                return false;

            return Dispatch(cmd, size, settings, source, destination, depthRT, normalRT, albedoRT);
        }

        internal bool Execute(CommandBuffer cmd,
                              Vector2Int size,
                              Settings settings,
                              Texture source,
                              Texture destination,
                              Texture depth,
                              Texture normal,
                              Texture albedo)
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
            RenderTargetIdentifier albedoId = albedo != null ? new RenderTargetIdentifier(albedo) : new RenderTargetIdentifier(Texture2D.blackTexture);

            return Dispatch(cmd, size, settings, src, dst, depthId, normalId, albedoId);
        }

        private bool Dispatch(CommandBuffer cmd,
                              Vector2Int size,
                              Settings settings,
                              RenderTargetIdentifier source,
                              RenderTargetIdentifier destination,
                              RenderTargetIdentifier depthRT,
                              RenderTargetIdentifier normalRT,
                              RenderTargetIdentifier albedoRT)
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
            cmd.SetComputeVectorParam(m_Shader, _ZBufferParams, zParams);

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
    }
}
