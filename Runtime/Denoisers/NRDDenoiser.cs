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
    /// <summary>
    /// Minimal NRD-inspired denoiser scaffold. The temporal/spatial logic is intentionally
    /// /// lightweight for now and can be expanded toward full NRD parity.
    /// </summary>
    internal sealed class NRDDenoiser : ISSGIDenoiser<NRDDenoiser.Settings>
    {
        private static readonly int _TexelSize = Shader.PropertyToID("_TexelSize");
        private static readonly int _TemporalParams0 = Shader.PropertyToID("_TemporalParams0");
        private static readonly int _TemporalParams1 = Shader.PropertyToID("_TemporalParams1");
        private static readonly int _TemporalZBufferParams = Shader.PropertyToID("_TemporalZBufferParams");
        private static readonly int _CurrNoisy = Shader.PropertyToID("_CurrNoisy");
        private static readonly int _HistFastPrev = Shader.PropertyToID("_HistFastPrev");
        private static readonly int _HistMainPrev = Shader.PropertyToID("_HistMainPrev");
        private static readonly int _MomentsPrev = Shader.PropertyToID("_MomentsPrev");
        private static readonly int _HistoryDepth = Shader.PropertyToID("_HistoryDepth");
        private static readonly int _DepthTexture = Shader.PropertyToID("_DepthTexture");
        private static readonly int _NormalTexture = Shader.PropertyToID("_NormalTexture");
        private static readonly int _MotionTexture = Shader.PropertyToID("_MotionTexture");
        private static readonly int _HistFast = Shader.PropertyToID("_HistFast");
        private static readonly int _HistMain = Shader.PropertyToID("_HistMain");
        private static readonly int _Moments = Shader.PropertyToID("_Moments");
        private static readonly int _TemporalOutput = Shader.PropertyToID("_TemporalOutput");

        private const string k_TemporalShaderResource = "SSGI_TemporalReproject";
        private const string k_TemporalKernelName = "TemporalReproject";

        private ComputeShader m_TemporalShader;
        private int m_TemporalKernel = -1;
        private bool m_WarnedMissingTemporalShader;
        private bool m_WarnedMissingTemporalKernel;

        public enum Signal
        {
            Diffuse,
            Specular,
            Shadow,
        }

        public struct Settings
        {
            public float MotionThreshold;
            public int MaxAccumulatedFrames;
            public int FastHistoryLength;
            public float DisocclusionThreshold;
            public float SigmaMultiplier;
            public int SpatialIterations;
            public float SpatialRadius;
        }

        internal struct ResourceSet
        {
            public Signal SignalType;
            public int Width;
            public int Height;

            public RenderTargetIdentifier Source;
            public RenderTargetIdentifier Destination;

            public RenderTargetIdentifier Depth;
            public RenderTargetIdentifier Normal;
            public RenderTargetIdentifier Roughness;
            public RenderTargetIdentifier Motion;
            public RenderTargetIdentifier HistoryDepth;

            public RenderTargetIdentifier HistoryColor;
            public RenderTargetIdentifier HistoryMoments;
            public RenderTargetIdentifier HistoryFast;

            public Vector4 ZParams;
            public bool EnableTemporal;
        }

        public NRDDenoiser()
        {
            profilingSampler = new ProfilingSampler("SSGI NRD");
        }

        public override ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm Algorithm =>
            ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.NRD;

        public override string DisplayName => "NRD";

        public override Type SettingsType => typeof(Settings);

        internal override void Configure(ScreenSpaceGlobalIlluminationURP feature)
        {
            ComputeShader temporal = m_TemporalShader ?? Resources.Load<ComputeShader>(k_TemporalShaderResource);
            if (!ReferenceEquals(temporal, m_TemporalShader))
            {
                m_TemporalShader = temporal;
                m_TemporalKernel =
                    (temporal != null && temporal.HasKernel(k_TemporalKernelName))
                        ? temporal.FindKernel(k_TemporalKernelName)
                        : -1;
                m_WarnedMissingTemporalShader = false;
                m_WarnedMissingTemporalKernel = false;
            }

#if UNITY_EDITOR || DEBUG
            if (!m_WarnedMissingTemporalShader && m_TemporalShader == null)
            {
                Debug.LogWarning(
                    "Screen Space Global Illumination URP: Missing compute shader 'SSGI_TemporalReproject'. NRD temporal stage will fall back to copy."
                );
                m_WarnedMissingTemporalShader = true;
            }
            else if (!m_WarnedMissingTemporalKernel && m_TemporalShader != null && m_TemporalKernel < 0)
            {
                Debug.LogWarning(
                    "Screen Space Global Illumination URP: Compute shader 'SSGI_TemporalReproject' does not expose kernel 'TemporalReproject'. NRD temporal stage will fall back to copy."
                );
                m_WarnedMissingTemporalKernel = true;
            }
#endif
        }

        internal bool SupportsTemporal =>
            SystemInfo.supportsComputeShaders && m_TemporalShader != null && m_TemporalKernel >= 0;

        public override bool IsSupported => true;

        public override Settings CreateSettings(ScreenSpaceGlobalIlluminationVolume volume)
        {
            if (volume == null)
                return default;

            return new Settings
            {
                MotionThreshold = Mathf.Max(0.0f, volume.nrdMotionThreshold.value),
                MaxAccumulatedFrames = Mathf.Max(1, volume.nrdMaxAccumulatedFrames.value),
                FastHistoryLength = Mathf.Max(1, volume.nrdFastHistoryLength.value),
                DisocclusionThreshold = Mathf.Max(0.0f, volume.nrdDisocclusionThreshold.value),
                SigmaMultiplier = Mathf.Max(0.0f, volume.nrdSigmaMultiplier.value),
                SpatialIterations = Mathf.Clamp(volume.nrdSpatialIterations.value, 1, 4),
                SpatialRadius = Mathf.Max(0.5f, volume.nrdSpatialRadius.value),
            };
        }

        internal override void ConfigurePass(
            ScreenSpaceGlobalIlluminationURP feature,
            ScreenSpaceGlobalIlluminationPass pass,
            ScreenSpaceGlobalIlluminationVolume volume
        )
        {
            pass.nrdDenoiser = this;
            var spatial = feature.AcquireDenoiser<SSGISpatialSingleFrameDenoiser>();
            spatial?.ConfigurePass(feature, pass, volume);
        }

        internal bool Dispatch(CommandBuffer cmd, in Settings settings, in ResourceSet resources)
        {
            if (cmd == null)
                return false;

            if (resources.Width <= 0 || resources.Height <= 0)
                return false;

            if (resources.Source == resources.Destination)
                return true;

            bool runTemporal =
                resources.EnableTemporal
                && SupportsTemporal
                && IsValid(resources.Source)
                && IsValid(resources.Destination)
                && IsValid(resources.HistoryColor)
                && IsValid(resources.HistoryFast)
                && IsValid(resources.HistoryMoments)
                && IsValid(resources.Depth)
                && IsValid(resources.HistoryDepth)
                && IsValid(resources.Motion)
                && IsValid(resources.Normal);

            if (!runTemporal)
            {
                cmd.CopyTexture(resources.Source, resources.Destination);
                return false;
            }

            int width = Mathf.Max(1, resources.Width);
            int height = Mathf.Max(1, resources.Height);

            cmd.SetComputeVectorParam(
                m_TemporalShader,
                _TexelSize,
                new Vector4(1.0f / width, 1.0f / height, width, height)
            );

            float maxAccum = Mathf.Max(1, settings.MaxAccumulatedFrames);
            float maxFast = Mathf.Max(1, settings.FastHistoryLength);
            float disocclusion = Mathf.Max(0.0f, settings.DisocclusionThreshold);
            float sigmaMultiplier = Mathf.Max(0.0f, settings.SigmaMultiplier);

            cmd.SetComputeVectorParam(
                m_TemporalShader,
                _TemporalParams0,
                new Vector4(maxAccum, maxFast, disocclusion, sigmaMultiplier)
            );

            const float varianceEps = 1e-4f;
            const float clampBias = 0.01f;

            cmd.SetComputeVectorParam(
                m_TemporalShader,
                _TemporalParams1,
                new Vector4(varianceEps, settings.MotionThreshold, 0.0f, clampBias)
            );

            cmd.SetComputeVectorParam(m_TemporalShader, _TemporalZBufferParams, resources.ZParams);

            cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _CurrNoisy, resources.Source);
            cmd.SetComputeTextureParam(
                m_TemporalShader,
                m_TemporalKernel,
                _HistFastPrev,
                resources.HistoryFast
            );
            cmd.SetComputeTextureParam(
                m_TemporalShader,
                m_TemporalKernel,
                _HistMainPrev,
                resources.HistoryColor
            );
            cmd.SetComputeTextureParam(
                m_TemporalShader,
                m_TemporalKernel,
                _MomentsPrev,
                resources.HistoryMoments
            );
            cmd.SetComputeTextureParam(
                m_TemporalShader,
                m_TemporalKernel,
                _HistoryDepth,
                resources.HistoryDepth
            );
            cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _DepthTexture, resources.Depth);
            cmd.SetComputeTextureParam(
                m_TemporalShader,
                m_TemporalKernel,
                _NormalTexture,
                resources.Normal
            );
            cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _MotionTexture, resources.Motion);

            cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _HistFast, resources.HistoryFast);
            cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _HistMain, resources.HistoryColor);
            cmd.SetComputeTextureParam(
                m_TemporalShader,
                m_TemporalKernel,
                _Moments,
                resources.HistoryMoments
            );
            cmd.SetComputeTextureParam(
                m_TemporalShader,
                m_TemporalKernel,
                _TemporalOutput,
                resources.Destination
            );

            int dispatchX = Mathf.CeilToInt(width / 8.0f);
            int dispatchY = Mathf.CeilToInt(height / 8.0f);
            cmd.DispatchCompute(m_TemporalShader, m_TemporalKernel, dispatchX, dispatchY, 1);
            return true;
        }

        private static bool IsValid(RenderTargetIdentifier id) =>
            id != new RenderTargetIdentifier(BuiltinRenderTextureType.None);

#if UNITY_6000_0_OR_NEWER
        internal struct RenderGraphResourceSet
        {
            public Signal SignalType;
            public int Width;
            public int Height;

            public TextureHandle Source;
            public TextureHandle Destination;

            public TextureHandle Depth;
            public TextureHandle Normal;
            public TextureHandle Roughness;
            public TextureHandle Motion;
            public TextureHandle HistoryDepth;

            public TextureHandle HistoryColor;
            public TextureHandle HistoryMoments;
            public TextureHandle HistoryFast;

            public Vector4 ZParams;
            public bool EnableTemporal;
        }

        internal bool Dispatch(
            CommandBuffer cmd,
            in Settings settings,
            in RenderGraphResourceSet resources
        )
        {
            if (cmd == null)
                return false;

            if (resources.Width <= 0 || resources.Height <= 0)
                return false;

            if (!resources.Source.IsValid() || !resources.Destination.IsValid())
                return false;

            bool runTemporal =
                resources.EnableTemporal
                && SupportsTemporal
                && resources.HistoryColor.IsValid()
                && resources.HistoryFast.IsValid()
                && resources.HistoryMoments.IsValid()
                && resources.Depth.IsValid()
                && resources.HistoryDepth.IsValid()
                && resources.Motion.IsValid()
                && resources.Normal.IsValid();

            if (!runTemporal)
            {
                Blitter.BlitCameraTexture(cmd, resources.Source, resources.Destination);
                return false;
            }

            int width = Mathf.Max(1, resources.Width);
            int height = Mathf.Max(1, resources.Height);

            cmd.SetComputeVectorParam(
                m_TemporalShader,
                _TexelSize,
                new Vector4(1.0f / width, 1.0f / height, width, height)
            );

            float maxAccum = Mathf.Max(1, settings.MaxAccumulatedFrames);
            float maxFast = Mathf.Max(1, settings.FastHistoryLength);
            float disocclusion = Mathf.Max(0.0f, settings.DisocclusionThreshold);
            float sigmaMultiplier = Mathf.Max(0.0f, settings.SigmaMultiplier);

            cmd.SetComputeVectorParam(
                m_TemporalShader,
                _TemporalParams0,
                new Vector4(maxAccum, maxFast, disocclusion, sigmaMultiplier)
            );

            const float varianceEps = 1e-4f;
            const float clampBias = 0.01f;

            cmd.SetComputeVectorParam(
                m_TemporalShader,
                _TemporalParams1,
                new Vector4(varianceEps, settings.MotionThreshold, 0.0f, clampBias)
            );

            cmd.SetComputeVectorParam(m_TemporalShader, _TemporalZBufferParams, resources.ZParams);

            cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _CurrNoisy, resources.Source);
            cmd.SetComputeTextureParam(
                m_TemporalShader,
                m_TemporalKernel,
                _HistFastPrev,
                resources.HistoryFast
            );
            cmd.SetComputeTextureParam(
                m_TemporalShader,
                m_TemporalKernel,
                _HistMainPrev,
                resources.HistoryColor
            );
            cmd.SetComputeTextureParam(
                m_TemporalShader,
                m_TemporalKernel,
                _MomentsPrev,
                resources.HistoryMoments
            );
            cmd.SetComputeTextureParam(
                m_TemporalShader,
                m_TemporalKernel,
                _HistoryDepth,
                resources.HistoryDepth
            );
            cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _DepthTexture, resources.Depth);
            cmd.SetComputeTextureParam(
                m_TemporalShader,
                m_TemporalKernel,
                _NormalTexture,
                resources.Normal
            );
            cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _MotionTexture, resources.Motion);

            cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _HistFast, resources.HistoryFast);
            cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _HistMain, resources.HistoryColor);
            cmd.SetComputeTextureParam(
                m_TemporalShader,
                m_TemporalKernel,
                _Moments,
                resources.HistoryMoments
            );
            cmd.SetComputeTextureParam(
                m_TemporalShader,
                m_TemporalKernel,
                _TemporalOutput,
                resources.Destination
            );

            int dispatchX = Mathf.CeilToInt(width / 8.0f);
            int dispatchY = Mathf.CeilToInt(height / 8.0f);
            cmd.DispatchCompute(m_TemporalShader, m_TemporalKernel, dispatchX, dispatchY, 1);
            return true;
        }
#endif

        internal bool Execute(CommandBuffer cmd, in Settings settings, in ResourceSet resources) =>
            Dispatch(cmd, in settings, in resources);
    }
}
