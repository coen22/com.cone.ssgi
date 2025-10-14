using System;
using Cone.SSGI;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

namespace Cone.SSGI.Denoisers
{
    internal sealed class SSGIAdaptiveLutDenoiser : ISSGIDenoiser<SSGIAdaptiveLutDenoiser.Settings>
    {
        private static readonly int _TexelSize = Shader.PropertyToID("_TexelSize");
        private static readonly int _FilterRadii = Shader.PropertyToID("_FilterRadii");
        private static readonly int _EdgeThresholds = Shader.PropertyToID("_EdgeThresholds");
        private static readonly int _EdgeComplexityParams = Shader.PropertyToID("_EdgeComplexityParams");
        private static readonly int _GuideParams = Shader.PropertyToID("_GuideParams");
        private static readonly int _MiscParams = Shader.PropertyToID("_MiscParams");
        private static readonly int _SpatialZBufferParams = Shader.PropertyToID("_SpatialZBufferParams");
        private static readonly int _WeightLUT = Shader.PropertyToID("_WeightLUT");
        private static readonly int _NoisySSGI = Shader.PropertyToID("_NoisySSGI");
        private static readonly int _DepthTexture = Shader.PropertyToID("_DepthTexture");
        private static readonly int _NormalTexture = Shader.PropertyToID("_NormalTexture");
        private static readonly int _SSGI_Denoised = Shader.PropertyToID("_SSGI_Denoised");

        private static readonly int _CurrNoisy = Shader.PropertyToID("_CurrNoisy");
        private static readonly int _HistFastPrev = Shader.PropertyToID("_HistFastPrev");
        private static readonly int _HistMainPrev = Shader.PropertyToID("_HistMainPrev");
        private static readonly int _MomentsPrev = Shader.PropertyToID("_MomentsPrev");
        private static readonly int _HistoryDepth = Shader.PropertyToID("_HistoryDepth");
        private static readonly int _MotionTexture = Shader.PropertyToID("_MotionTexture");
        private static readonly int _HistFast = Shader.PropertyToID("_HistFast");
        private static readonly int _HistMain = Shader.PropertyToID("_HistMain");
        private static readonly int _Moments = Shader.PropertyToID("_Moments");
        private static readonly int _TemporalOutput = Shader.PropertyToID("_TemporalOutput");
        private static readonly int _TemporalParams0 = Shader.PropertyToID("_TemporalParams0");
        private static readonly int _TemporalParams1 = Shader.PropertyToID("_TemporalParams1");
        private static readonly int _TemporalZBufferParams = Shader.PropertyToID("_TemporalZBufferParams");

        private const string k_SpatialShaderResource = "SSGI_EdgeAdaptiveLUT";
        private const string k_TemporalShaderResource = "SSGI_TemporalReproject";
        private const string k_SpatialKernelName = "Spatial5x5";
        private const string k_TemporalKernelName = "TemporalReproject";

        private ComputeShader m_SpatialShader;
        private int m_SpatialKernel = -1;

        private ComputeShader m_TemporalShader;
        private int m_TemporalKernel = -1;

        private bool m_WarnedMissingSpatialShader;
        private bool m_WarnedMissingSpatialKernel;
        private bool m_WarnedMissingTemporalKernel;

        private Texture2D m_WeightLut;

        public struct Settings
        {
            public Vector4 Radii;
            public Vector4 EdgeThresholds;
            public float DepthScale;
            public float NormalScale;
            public float GuideNormalPower;
            public float GuideDepthScale;
            public float MaxDistance;
            public float MinWeight;
            public bool UseNormalGate;
            public bool UseDepthGate;

            public bool UseTemporal;
            public int MaxAccumFrames;
            public int MaxFastFrames;
            public float DepthTolerance;
            public float AntiFireflyStrength;
            public float ClampBias;
            public float VarianceEpsilon;
        }

        internal struct ResourceSet
        {
            public int Width;
            public int Height;
            public RenderTargetIdentifier Source;
            public RenderTargetIdentifier Destination;
            public RenderTargetIdentifier Depth;
            public RenderTargetIdentifier Normal;
            public RenderTargetIdentifier Motion;
            public RenderTargetIdentifier HistoryDepth;
            public RenderTargetIdentifier FastHistory;
            public RenderTargetIdentifier MainHistory;
            public RenderTargetIdentifier Moments;
            public RenderTargetIdentifier TemporalOutput;
            public bool HasTemporal;
        }

        public SSGIAdaptiveLutDenoiser()
        {
            profilingSampler = new ProfilingSampler("SSGI Adaptive LUT");
        }

        public override ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm Algorithm =>
            ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.EdgeAdaptiveLut;

        public override string DisplayName => "Edge Adaptive LUT";

        public override Type SettingsType => typeof(Settings);

        internal override void Configure(ScreenSpaceGlobalIlluminationURP _)
        {
            ComputeShader spatial = m_SpatialShader ?? Resources.Load<ComputeShader>(k_SpatialShaderResource);
            if (!ReferenceEquals(spatial, m_SpatialShader))
            {
                m_SpatialShader = spatial;
                m_SpatialKernel = (spatial != null && spatial.HasKernel(k_SpatialKernelName)) ? spatial.FindKernel(k_SpatialKernelName) : -1;
                m_WarnedMissingSpatialShader = false;
                m_WarnedMissingSpatialKernel = false;
            }

            ComputeShader temporal = m_TemporalShader ?? Resources.Load<ComputeShader>(k_TemporalShaderResource);
            if (!ReferenceEquals(temporal, m_TemporalShader))
            {
                m_TemporalShader = temporal;
                m_TemporalKernel = (temporal != null && temporal.HasKernel(k_TemporalKernelName)) ? temporal.FindKernel(k_TemporalKernelName) : -1;
                m_WarnedMissingTemporalKernel = false;
            }

#if UNITY_EDITOR || DEBUG
            if (!m_WarnedMissingSpatialShader && m_SpatialShader == null)
            {
                Debug.LogWarning("Screen Space Global Illumination URP: Missing compute shader 'SSGI_EdgeAdaptiveLUT'. Edge Adaptive LUT denoiser will fall back to copy.");
                m_WarnedMissingSpatialShader = true;
            }
            else if (!m_WarnedMissingSpatialKernel && m_SpatialShader != null && m_SpatialKernel < 0)
            {
                Debug.LogWarning("Screen Space Global Illumination URP: Compute shader 'SSGI_EdgeAdaptiveLUT' does not expose kernel 'Spatial5x5'. Falling back to copy.");
                m_WarnedMissingSpatialKernel = true;
            }

            if (!m_WarnedMissingTemporalKernel && m_TemporalShader != null && m_TemporalKernel < 0)
            {
                Debug.LogWarning("Screen Space Global Illumination URP: Compute shader 'SSGI_TemporalReproject' does not expose kernel 'TemporalReproject'. Temporal accumulation will be disabled.");
                m_WarnedMissingTemporalKernel = true;
            }
#endif
        }

        public override bool IsSupported =>
            SystemInfo.supportsComputeShaders && m_SpatialShader != null && m_SpatialKernel >= 0;

        internal bool SupportsTemporal => m_TemporalKernel >= 0 && m_TemporalShader != null;

        public override Settings CreateSettings(ScreenSpaceGlobalIlluminationVolume volume)
        {
            if (volume == null)
                return default;

            Vector4 radii = volume.adaptiveRadii.value;
            radii.x = Mathf.Max(0.0f, radii.x);
            radii.y = Mathf.Max(radii.x, radii.y);
            radii.z = Mathf.Max(radii.y, radii.z);
            radii.w = Mathf.Max(radii.z, radii.w);

            Vector4 thresholds = volume.adaptiveEdgeThresholds.value;
            thresholds.x = Mathf.Max(0.0f, thresholds.x);
            thresholds.y = Mathf.Max(thresholds.x, thresholds.y);
            thresholds.z = Mathf.Max(thresholds.y, thresholds.z);
            thresholds.w = thresholds.z;

            return new Settings
            {
                Radii = radii,
                EdgeThresholds = thresholds,
                DepthScale = Mathf.Max(0.0f, volume.adaptiveDepthScale.value),
                NormalScale = Mathf.Max(0.0f, volume.adaptiveNormalScale.value),
                GuideNormalPower = Mathf.Max(0.0f, volume.adaptiveGuideNormalPower.value),
                GuideDepthScale = Mathf.Max(0.0f, volume.adaptiveGuideDepthScale.value),
                MaxDistance = Mathf.Max(0.1f, volume.adaptiveMaxDistance.value),
                MinWeight = Mathf.Max(0.0f, volume.adaptiveMinWeight.value),
                UseNormalGate = volume.adaptiveUseNormalGate.value,
                UseDepthGate = volume.adaptiveUseDepthGate.value,

                UseTemporal = volume.adaptiveUseTemporal.value,
                MaxAccumFrames = Mathf.Max(1, volume.adaptiveTemporalMaxFrames.value),
                MaxFastFrames = Mathf.Max(1, volume.adaptiveTemporalMaxFastFrames.value),
                DepthTolerance = Mathf.Max(0.0f, volume.adaptiveTemporalDepthTolerance.value),
                AntiFireflyStrength = Mathf.Max(0.0f, volume.adaptiveTemporalAntiFirefly.value),
                ClampBias = Mathf.Max(0.0f, volume.adaptiveTemporalClampBias.value),
                VarianceEpsilon = Mathf.Max(1e-6f, volume.adaptiveTemporalVarianceEpsilon.value),
            };
        }

        internal bool Dispatch(
            CommandBuffer cmd,
            Vector4 zParams,
            Settings settings,
            in ResourceSet resources
        )
        {
            if (!IsSupported || resources.Width <= 0 || resources.Height <= 0)
                return false;

            EnsureWeightLut();
            if (m_WeightLut == null)
                return false;

            bool runTemporal = settings.UseTemporal && resources.HasTemporal && m_TemporalKernel >= 0 && m_TemporalShader != null;
            RenderTargetIdentifier spatialSource = resources.Source;

            if (runTemporal)
            {
                cmd.SetComputeVectorParam(m_TemporalShader, _TexelSize,
                    new Vector4(1.0f / Mathf.Max(1, resources.Width), 1.0f / Mathf.Max(1, resources.Height), resources.Width, resources.Height));
                cmd.SetComputeVectorParam(m_TemporalShader, _TemporalParams0,
                    new Vector4(settings.MaxAccumFrames, settings.MaxFastFrames, settings.DepthTolerance, settings.AntiFireflyStrength));
                cmd.SetComputeVectorParam(m_TemporalShader, _TemporalParams1,
                    new Vector4(settings.VarianceEpsilon, 0.0f, 0.0f, settings.ClampBias));
                cmd.SetComputeVectorParam(m_TemporalShader, _TemporalZBufferParams, zParams);

                cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _CurrNoisy, resources.Source);
                cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _HistFastPrev, resources.FastHistory);
                cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _HistMainPrev, resources.MainHistory);
                cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _MomentsPrev, resources.Moments);
                cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _HistoryDepth, resources.HistoryDepth);
                cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _DepthTexture, resources.Depth);
                cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _NormalTexture, resources.Normal);
                cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _MotionTexture, resources.Motion);

                cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _HistFast, resources.FastHistory);
                cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _HistMain, resources.MainHistory);
                cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _Moments, resources.Moments);
                cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _TemporalOutput, resources.TemporalOutput);

                int dispatchX = Mathf.CeilToInt(resources.Width / 8.0f);
                int dispatchY = Mathf.CeilToInt(resources.Height / 8.0f);
                cmd.DispatchCompute(m_TemporalShader, m_TemporalKernel, dispatchX, dispatchY, 1);

                spatialSource = resources.TemporalOutput;
            }

            cmd.SetComputeVectorParam(m_SpatialShader, _TexelSize,
                new Vector4(1.0f / Mathf.Max(1, resources.Width), 1.0f / Mathf.Max(1, resources.Height), resources.Width, resources.Height));
            cmd.SetComputeVectorParam(m_SpatialShader, _FilterRadii, settings.Radii);
            cmd.SetComputeVectorParam(m_SpatialShader, _EdgeThresholds, settings.EdgeThresholds);
            cmd.SetComputeVectorParam(m_SpatialShader, _EdgeComplexityParams, new Vector4(settings.DepthScale, settings.NormalScale, 0.0f, 0.0f));
            cmd.SetComputeVectorParam(m_SpatialShader, _GuideParams,
                new Vector4(settings.GuideNormalPower, settings.GuideDepthScale, settings.UseNormalGate ? 1.0f : 0.0f, settings.UseDepthGate ? 1.0f : 0.0f));
            cmd.SetComputeVectorParam(m_SpatialShader, _MiscParams, new Vector4(settings.MaxDistance, settings.MinWeight, 0.0f, 0.0f));
            cmd.SetComputeVectorParam(m_SpatialShader, _SpatialZBufferParams, zParams);

            cmd.SetComputeTextureParam(m_SpatialShader, m_SpatialKernel, _WeightLUT, m_WeightLut);
            cmd.SetComputeTextureParam(m_SpatialShader, m_SpatialKernel, _NoisySSGI, spatialSource);
            cmd.SetComputeTextureParam(m_SpatialShader, m_SpatialKernel, _DepthTexture, resources.Depth);
            cmd.SetComputeTextureParam(m_SpatialShader, m_SpatialKernel, _NormalTexture, resources.Normal);
            cmd.SetComputeTextureParam(m_SpatialShader, m_SpatialKernel, _SSGI_Denoised, resources.Destination);

            int spatialDispatchX = Mathf.CeilToInt(resources.Width / 8.0f);
            int spatialDispatchY = Mathf.CeilToInt(resources.Height / 8.0f);
            cmd.DispatchCompute(m_SpatialShader, m_SpatialKernel, spatialDispatchX, spatialDispatchY, 1);
            return true;
        }

        internal bool Execute(
            CommandBuffer cmd,
            Vector4 zParams,
            Settings settings,
            in ResourceSet resources
        ) => Dispatch(cmd, zParams, settings, in resources);

        private void EnsureWeightLut()
        {
            if (m_WeightLut != null)
                return;

            const int width = 256;
            const int height = 32;

            var lut = new Texture2D(width, height, TextureFormat.RFloat, mipChain: false, linear: true)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "SSGI_AdaptiveLUT",
            };

            NativeArray<float> data = new NativeArray<float>(width * height, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            for (int y = 0; y < height; ++y)
            {
                float dist = (float)y / Mathf.Max(1, height - 1);
                for (int x = 0; x < width; ++x)
                {
                    float edge = (float)x / Mathf.Max(1, width - 1);
                    float attenuation = Mathf.Exp(-dist * Mathf.Lerp(1.0f, 4.0f, edge));
                    data[y * width + x] = attenuation;
                }
            }

            lut.SetPixelData(data, 0);
            lut.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            data.Dispose();

            m_WeightLut = lut;
        }

#if UNITY_6000_0_OR_NEWER
        internal struct RenderGraphResourceSet
        {
            public int Width;
            public int Height;
            public TextureHandle Source;
            public TextureHandle Destination;
            public TextureHandle Depth;
            public TextureHandle Normal;
            public TextureHandle Motion;
            public TextureHandle HistoryDepth;
            public TextureHandle FastHistory;
            public TextureHandle MainHistory;
            public TextureHandle Moments;
            public TextureHandle TemporalOutput;
            public bool HasTemporal;
        }

        internal bool Dispatch(
            CommandBuffer cmd,
            Vector4 zParams,
            Settings settings,
            in RenderGraphResourceSet resources
        )
        {
            if (!IsSupported || resources.Width <= 0 || resources.Height <= 0 || !resources.Source.IsValid() || !resources.Destination.IsValid() || !resources.Depth.IsValid() || !resources.Normal.IsValid())
                return false;

            EnsureWeightLut();
            if (m_WeightLut == null)
                return false;

            bool runTemporal = settings.UseTemporal && resources.HasTemporal && m_TemporalKernel >= 0 && m_TemporalShader != null &&
                               resources.HistoryDepth.IsValid() && resources.FastHistory.IsValid() && resources.MainHistory.IsValid() &&
                               resources.Moments.IsValid() && resources.Motion.IsValid() && resources.TemporalOutput.IsValid();

            TextureHandle spatialSource = resources.Source;

            if (runTemporal)
            {
                cmd.SetComputeVectorParam(m_TemporalShader, _TexelSize,
                    new Vector4(1.0f / Mathf.Max(1, resources.Width), 1.0f / Mathf.Max(1, resources.Height), resources.Width, resources.Height));
                cmd.SetComputeVectorParam(m_TemporalShader, _TemporalParams0,
                    new Vector4(settings.MaxAccumFrames, settings.MaxFastFrames, settings.DepthTolerance, settings.AntiFireflyStrength));
                cmd.SetComputeVectorParam(m_TemporalShader, _TemporalParams1,
                    new Vector4(settings.VarianceEpsilon, 0.0f, 0.0f, settings.ClampBias));
                cmd.SetComputeVectorParam(m_TemporalShader, _TemporalZBufferParams, zParams);

                cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _CurrNoisy, resources.Source);
                cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _HistFastPrev, resources.FastHistory);
                cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _HistMainPrev, resources.MainHistory);
                cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _MomentsPrev, resources.Moments);
                cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _HistoryDepth, resources.HistoryDepth);
                cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _DepthTexture, resources.Depth);
                cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _NormalTexture, resources.Normal);
                cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _MotionTexture, resources.Motion);

                cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _HistFast, resources.FastHistory);
                cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _HistMain, resources.MainHistory);
                cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _Moments, resources.Moments);
                cmd.SetComputeTextureParam(m_TemporalShader, m_TemporalKernel, _TemporalOutput, resources.TemporalOutput);

                int dispatchX = Mathf.CeilToInt(resources.Width / 8.0f);
                int dispatchY = Mathf.CeilToInt(resources.Height / 8.0f);
                cmd.DispatchCompute(m_TemporalShader, m_TemporalKernel, dispatchX, dispatchY, 1);

                spatialSource = resources.TemporalOutput;
            }

            cmd.SetComputeVectorParam(m_SpatialShader, _TexelSize,
                new Vector4(1.0f / Mathf.Max(1, resources.Width), 1.0f / Mathf.Max(1, resources.Height), resources.Width, resources.Height));
            cmd.SetComputeVectorParam(m_SpatialShader, _FilterRadii, settings.Radii);
            cmd.SetComputeVectorParam(m_SpatialShader, _EdgeThresholds, settings.EdgeThresholds);
            cmd.SetComputeVectorParam(m_SpatialShader, _EdgeComplexityParams, new Vector4(settings.DepthScale, settings.NormalScale, 0.0f, 0.0f));
            cmd.SetComputeVectorParam(m_SpatialShader, _GuideParams,
                new Vector4(settings.GuideNormalPower, settings.GuideDepthScale, settings.UseNormalGate ? 1.0f : 0.0f, settings.UseDepthGate ? 1.0f : 0.0f));
            cmd.SetComputeVectorParam(m_SpatialShader, _MiscParams, new Vector4(settings.MaxDistance, settings.MinWeight, 0.0f, 0.0f));
            cmd.SetComputeVectorParam(m_SpatialShader, _SpatialZBufferParams, zParams);

            cmd.SetComputeTextureParam(m_SpatialShader, m_SpatialKernel, _WeightLUT, m_WeightLut);
            cmd.SetComputeTextureParam(m_SpatialShader, m_SpatialKernel, _NoisySSGI, spatialSource);
            cmd.SetComputeTextureParam(m_SpatialShader, m_SpatialKernel, _DepthTexture, resources.Depth);
            cmd.SetComputeTextureParam(m_SpatialShader, m_SpatialKernel, _NormalTexture, resources.Normal);
            cmd.SetComputeTextureParam(m_SpatialShader, m_SpatialKernel, _SSGI_Denoised, resources.Destination);

            int spatialDispatchX = Mathf.CeilToInt(resources.Width / 8.0f);
            int spatialDispatchY = Mathf.CeilToInt(resources.Height / 8.0f);
            cmd.DispatchCompute(m_SpatialShader, m_SpatialKernel, spatialDispatchX, spatialDispatchY, 1);
            return true;
        }

        internal override void ConfigurePass(ScreenSpaceGlobalIlluminationURP feature, ScreenSpaceGlobalIlluminationPass pass, ScreenSpaceGlobalIlluminationVolume volume)
        {
            pass.adaptiveLutDenoiser = this;
        }

        internal bool Execute(
            CommandBuffer cmd,
            Vector4 zParams,
            Settings settings,
            in RenderGraphResourceSet resources
        ) => Dispatch(cmd, zParams, settings, in resources);
#endif
    }
}