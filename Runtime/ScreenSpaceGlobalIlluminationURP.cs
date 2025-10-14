using System;
using System.Collections.Generic;
using System.Reflection;
using Cone.SSGI.Denoisers;
using Cone.SSGI.Sampling;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Rendering.Universal;
using static Cone.SSGI.ScreenSpaceGlobalIlluminationShaderConstants;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

namespace Cone.SSGI
{
    [DisallowMultipleRendererFeature("Screen Space Global Illumination")]
    [Tooltip(
        "The Screen Space Global Illumination uses the depth and color buffer of the screen to calculate diffuse light bounces."
    )]
    [HelpURL("https://github.com/jiaozi158/UnitySSGIURP/blob/main")]
    public class ScreenSpaceGlobalIlluminationURP : ScriptableRendererFeature
    {
        private Material m_SSGIMaterial;

        private ISSGIDenoiser m_Denoiser;

        private readonly ISSGISamplingStrategy[] m_SamplingStrategies =
        {
            new HammersleyCranleyPattersonSamplingStrategy(),
            new R2CranleyPattersonSamplingStrategy(),
            new OwenScrambledSobolSamplingStrategy(),
            new CorrelatedMultiJitteredSamplingStrategy(),
            new ProgressiveMultiJitteredSamplingStrategy(),
            new ProgressiveMultiJitteredBlueNoiseSamplingStrategy(),
            new SobolBurleySamplingStrategy(),
            new OrthogonalArraySamplingStrategy(),
            new BlueNoiseDiffusionSamplingStrategy()
        };

        internal ISSGIDenoiser ActiveDenoiser
        {
            get => m_Denoiser;
            set => m_Denoiser = value;
        }

        [Header("Setup")]
        [Tooltip("The shader of screen space global illumination.")]
        [SerializeField]
        private Shader m_Shader;

        [Tooltip(
            "Specifies if URP computes screen space global illumination in Rendering Debugger view. \nThis is disabled by default to avoid affecting the individual lighting previews."
        )]
        [SerializeField]
        private bool m_RenderingDebugger = false;

        [Header("Performance")]
        [Tooltip(
            "Specifies if URP computes screen space global illumination in both real-time and baked reflection probes. \nScreen space global illumination in real-time reflection probes may reduce performace."
        )]
        [SerializeField]
        private bool m_ReflectionProbes = true;

        [Tooltip(
            "Enables high-quality upscaling for screen space global illumination. \nThis may impact performance."
        )]
        [SerializeField]
        private bool m_HighQualityUpscaling = false;

        [Header("Lighting")]
        [Tooltip(
            "Specifies if screen space global illumination overrides ambient lighting. \nThis ensures the accuracy of indirect lighting from SSGI."
        )]
        [SerializeField]
        private bool m_OverrideAmbientLighting = true;

        [Header("Advanced")]
        [Tooltip(
            "Renders back-face lighting when using automatic thickness mode. \nThis improves accuracy in some cases, but may severely impact performance."
        )]
        [SerializeField]
        private bool m_BackfaceLighting = false;

        /// <summary>
        /// Get the material of screen space global illumination shader.
        /// </summary>
        /// <value>
        /// The material of screen space global illumination shader.
        /// </value>
        public Material SSGIMaterial
        {
            get { return m_SSGIMaterial; }
        }

        /// <summary>
        /// Gets or sets the screen space global illumination shader.
        /// </summary>
        /// <value>
        /// The screen space global illumination shader.
        /// </value>
        public Shader SSGIShader
        {
            get { return m_Shader; }
            set { m_Shader = value == Shader.Find(m_SSGIShaderName) ? value : m_Shader; }
        }

        /// <summary>
        /// Gets or sets a value indicating whether to compute screen space global illumination in Rendering Debugger view.
        /// </summary>
        /// <remarks>
        /// This is disabled by default to avoid affecting the individual lighting previews.
        /// </remarks>
        public bool RenderingDebugger
        {
            get { return m_RenderingDebugger; }
            set { m_RenderingDebugger = value; }
        }

        /// <summary>
        /// Gets or sets a value indicating whether to compute screen space global illumination in both real-time and baked reflection probes.
        /// </summary>
        /// <remarks>
        /// Screen space global illumination in real-time reflection probes may reduce performace.
        /// </remarks>
        public bool ReflectionProbes
        {
            get { return m_ReflectionProbes; }
            set { m_ReflectionProbes = value; }
        }

        /// <summary>
        /// Gets or sets a value indicating whether to enable high-quality upscaling for screen space global illumination.
        /// </summary>
        public bool HighQualityUpscaling
        {
            get { return m_HighQualityUpscaling; }
            set { m_HighQualityUpscaling = value; }
        }

        /// <summary>
        /// Gets or sets a value indicating whether screen space global illumination overrides ambient lighting.
        /// </summary>
        /// <remarks>
        /// Enable this to ensure the accuracy of indirect lighting from SSGI.
        /// </remarks>
        public bool OverrideAmbientLighting
        {
            get { return m_OverrideAmbientLighting; }
            set { m_OverrideAmbientLighting = value; }
        }

        /// <summary>
        /// Renders back-face lighting when using automatic thickness mode.
        /// </summary>
        /// <remarks>
        /// This improves accuracy in some cases, but may severely impact performance.
        /// </remarks>
        public bool BackfaceLighting
        {
            get { return m_BackfaceLighting; }
            set { m_BackfaceLighting = value; }
        }

        private const string m_SSGIShaderName = "Hidden/Lighting/ScreenSpaceGlobalIllumination";
        // Include forward-only fallbacks so forward materials (including unlit) still populate the albedo GBuffer.
        private readonly string[] m_GBufferPassNames =
            new string[]
            {
                "UniversalGBuffer",
                "UniversalForward",
                "UniversalForwardOnly",
                "SRPDefaultUnlit",
            };
        private PreRenderScreenSpaceGlobalIlluminationPass m_PreRenderSSGIPass;
        private ScreenSpaceGlobalIlluminationPass m_SSGIPass;
        private BackfaceDataPass m_BackfaceDataPass;
        private ForwardGBufferPass m_ForwardGBufferPass;

        // Used in Forward GBuffer render pass
        internal static readonly FieldInfo gBufferFieldInfo = typeof(UniversalRenderer).GetField(
            "m_GBufferPass",
            BindingFlags.NonPublic | BindingFlags.Instance
        );

        private const string motionVectorColorHandleName = "m_Color";
        private const string motionVectorDepthHandleName = "m_Depth";

        internal static readonly FieldInfo motionVectorPassFieldInfo =
            typeof(UniversalRenderer).GetField(
                "m_MotionVectorPass",
                BindingFlags.NonPublic | BindingFlags.Instance
            );

        internal static readonly FieldInfo cameraMotionVectorHandleFieldInfo =
            typeof(UniversalRenderer).GetField(
                "m_CameraMotionVectorHandle",
                BindingFlags.NonPublic | BindingFlags.Instance
            );

        internal static readonly FieldInfo motionVectorColorHandleFieldInfo;
        internal static readonly FieldInfo motionVectorDepthHandleFieldInfo;

        static ScreenSpaceGlobalIlluminationURP()
        {
            var motionVectorPassType = motionVectorPassFieldInfo?.FieldType;
            if (motionVectorPassType != null)
            {
                motionVectorColorHandleFieldInfo = motionVectorPassType.GetField(
                    motionVectorColorHandleName,
                    BindingFlags.NonPublic | BindingFlags.Instance
                );
                motionVectorDepthHandleFieldInfo = motionVectorPassType.GetField(
                    motionVectorDepthHandleName,
                    BindingFlags.NonPublic | BindingFlags.Instance
                );
            }
        }

        // [Resolve Later] The "_CameraNormalsTexture" still exists after disabling DepthNormals Prepass, which may cause issue during rendering.
        // So instead of checking the RTHandle, we need to check if DepthNormals Prepass is enqueued.
        //private readonly static FieldInfo normalsTextureFieldInfo = typeof(UniversalRenderer).GetField("m_NormalsTexture", BindingFlags.NonPublic | BindingFlags.Instance);

        // Avoid printing messages every frame
        private bool isShaderMismatchLogPrinted = false;
        private bool isDebuggerLogPrinted = false;
        private bool isBackfaceLightingLogPrinted = false;

        public override void Create()
        {
            if (m_Shader != Shader.Find(m_SSGIShaderName))
            {
#if UNITY_EDITOR || DEBUG
                Debug.LogErrorFormat(
                    "Screen Space Global Illumination URP: Material is not using {0} shader.",
                    m_SSGIShaderName
                );
                isShaderMismatchLogPrinted = true;
#endif
                return;
            }
            else
            {
                isShaderMismatchLogPrinted = false;
            }

            m_SSGIMaterial = CoreUtils.CreateEngineMaterial(m_Shader);

            if (m_PreRenderSSGIPass == null)
            {
                m_PreRenderSSGIPass = new PreRenderScreenSpaceGlobalIlluminationPass();
#if UNITY_6000_0_OR_NEWER
                m_PreRenderSSGIPass.renderPassEvent = RenderPassEvent.BeforeRenderingPrePasses;
#else
                m_PreRenderSSGIPass.renderPassEvent =
                    RenderPassEvent.BeforeRenderingTransparents - 1;
#endif
            }

            if (m_SSGIPass == null)
            {
                m_SSGIPass = new ScreenSpaceGlobalIlluminationPass(m_SSGIMaterial);
#if UNITY_6000_0_OR_NEWER
                bool enableRenderGraph = !GraphicsSettings
                    .GetRenderPipelineSettings<RenderGraphSettings>()
                    .enableRenderCompatibilityMode;
                m_SSGIPass.renderPassEvent = enableRenderGraph
                    ? RenderPassEvent.AfterRenderingSkybox
                    : RenderPassEvent.BeforeRenderingTransparents;
#else
                m_SSGIPass.renderPassEvent = RenderPassEvent.BeforeRenderingTransparents; // We cannot move to after skybox because of the motion vectors issue
#endif
            }
            m_SSGIPass.m_SSGIMaterial = m_SSGIMaterial;

            if (m_BackfaceDataPass == null)
            {
                m_BackfaceDataPass = new BackfaceDataPass();
                m_BackfaceDataPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques - 1;
            }

            if (m_ForwardGBufferPass == null)
            {
                m_ForwardGBufferPass = new ForwardGBufferPass(m_GBufferPassNames);
                // Set this to "After Opaques" so that we can enable GBuffers Depth Priming on non-GL platforms.
                m_ForwardGBufferPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (m_PreRenderSSGIPass != null)
                m_PreRenderSSGIPass.Dispose();

            if (m_SSGIPass != null)
                m_SSGIPass.Dispose();

            if (m_BackfaceDataPass != null)
            {
                // Turn off accurate thickness since the render pass is disabled.
                if (m_SSGIMaterial != null)
                {
                    m_SSGIMaterial.SetFloat(_BackDepthEnabled, 0.0f);
                }
                m_BackfaceDataPass.Dispose();
            }

            if (m_ForwardGBufferPass != null)
                m_ForwardGBufferPass.Dispose();

            if (m_SSGIMaterial != null)
                CoreUtils.Destroy(m_SSGIMaterial);

            m_Denoiser = null;

            DenoisersExtensions.ClearSharedDenoiserCache();
        }

        public override void AddRenderPasses(
            ScriptableRenderer renderer,
            ref RenderingData renderingData
        )
        {
            // Do not add render passes if any error occurs.
            if (isShaderMismatchLogPrinted)
                return;

            if (renderingData.cameraData.camera.cameraType == CameraType.Preview)
                return;

            var stack = VolumeManager.instance.stack;
            ScreenSpaceGlobalIlluminationVolume ssgiVolume =
                stack.GetComponent<ScreenSpaceGlobalIlluminationVolume>();
            bool isActive = ssgiVolume != null && ssgiVolume.IsActive();
            bool isDebugger = DebugManager.instance.isAnyDebugUIActive;
            bool shouldDisable =
                !m_ReflectionProbes
                && renderingData.cameraData.camera.cameraType == CameraType.Reflection;
            shouldDisable |=
                ssgiVolume.indirectDiffuseLightingMultiplier.value == 0.0f
                && !m_OverrideAmbientLighting;
            shouldDisable |= renderingData.cameraData.renderType == CameraRenderType.Overlay;

            if (!isActive || shouldDisable)
                return;

#if UNITY_EDITOR || DEBUG
            if (isDebugger && !m_RenderingDebugger)
            {
                if (!isDebuggerLogPrinted)
                {
                    Debug.Log(
                        "Screen Space Global Illumination URP: Disable effect to avoid affecting rendering debugging."
                    );
                    isDebuggerLogPrinted = true;
                }
            }
            else
                isDebuggerLogPrinted = false;
#endif

            ISSGISamplingStrategy samplingStrategy = Array.Find(
                m_SamplingStrategies,
                strategy => strategy.Id == ssgiVolume.samplingSequence.value
            ) ?? m_SamplingStrategies[0];

            foreach (var strategy in m_SamplingStrategies)
            {
                if (ReferenceEquals(strategy, samplingStrategy))
                {
                    m_SSGIMaterial.EnableKeyword(strategy.Keyword);
                    strategy.ConfigureMaterial(m_SSGIMaterial, ssgiVolume);
                }
                else
                    m_SSGIMaterial.DisableKeyword(strategy.Keyword);
            }

            // Per 8 steps: 1 small steps, 2 medium steps, 5 large steps
            bool lowStepCount = ssgiVolume.maxRaySteps.value <= 16;
            int groupsCount = ssgiVolume.maxRaySteps.value / 8;
            int smallSteps = lowStepCount ? 0 : Mathf.Max(groupsCount, 4);
            int mediumSteps = lowStepCount ? groupsCount + 2 : smallSteps + groupsCount * 2;

            // For high resolution: Use a lower accumulation factor to help reduce latency.
            // For low resolution: Use a higher accumulation factor to improve denoising.
            float resolutionScale = ssgiVolume.fullResolutionSS.value
                ? 1.0f
                : ssgiVolume.resolutionScaleSS.value;
            float temporalIntensity = Mathf.Lerp(
                ssgiVolume.denoiseIntensitySS.value + 0.02f,
                ssgiVolume.denoiseIntensitySS.value - 0.04f,
                resolutionScale
            );

            // TODO: Expose more settings
            m_SSGIMaterial.SetFloat(_MaxSteps, ssgiVolume.maxRaySteps.value);
            m_SSGIMaterial.SetFloat(_MaxSmallSteps, smallSteps);
            m_SSGIMaterial.SetFloat(_MaxMediumSteps, mediumSteps);
            m_SSGIMaterial.SetFloat(_StepSize, lowStepCount ? 0.5f : 0.4f);
            m_SSGIMaterial.SetFloat(_SmallStepSize, smallSteps < 4 ? 0.05f : 0.015f);
            m_SSGIMaterial.SetFloat(_MediumStepSize, lowStepCount ? 0.1f : 0.05f);
            m_SSGIMaterial.SetFloat(_Thickness, ssgiVolume.depthBufferThickness.value);
            m_SSGIMaterial.SetFloat(
                _Thickness_Increment,
                ssgiVolume.depthBufferThickness.value * 0.25f
            );
            m_SSGIMaterial.SetFloat(_RayCount, ssgiVolume.sampleCount.value);
            m_SSGIMaterial.SetFloat(_NormalBias, ssgiVolume.normalBias.value);
            m_SSGIMaterial.SetFloat(_TemporalIntensity, temporalIntensity);
            m_SSGIMaterial.SetFloat(
                _ReBlurDenoiserRadius,
                ssgiVolume.denoiserRadiusSS.value * 2.0f * k_BlurMaxRadius
            ); // Optimized for roughness = 1.0
            m_SSGIMaterial.SetFloat(
                _IndirectDiffuseLightingMultiplier,
                ssgiVolume.indirectDiffuseLightingMultiplier.value
            );
            m_SSGIMaterial.SetFloat(_MaxBrightness, 7.0f);
            m_SSGIMaterial.SetFloat(
                _AggressiveDenoise,
                ssgiVolume.denoiserAlgorithmSS.value
                == ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.Aggressive
                    ? 1.0f
                    : 0.0f
            );

#if UNITY_2023_3_OR_NEWER
            bool enableRenderingLayers =
                Shader.IsKeywordEnabled(_WRITE_RENDERING_LAYERS)
                && ssgiVolume.indirectDiffuseRenderingLayers.value.value != 0xFFFF;
            if (enableRenderingLayers)
            {
                m_SSGIMaterial.EnableKeyword(_USE_RENDERING_LAYERS);
                m_SSGIMaterial.SetInteger(
                    _IndirectDiffuseRenderingLayers,
                    (int)ssgiVolume.indirectDiffuseRenderingLayers.value.value
                );
            }
            else
                m_SSGIMaterial.DisableKeyword(_USE_RENDERING_LAYERS);
#else
            bool enableRenderingLayers = false;
            m_SSGIMaterial.DisableKeyword(_USE_RENDERING_LAYERS);
#endif

            m_SSGIPass.ssgiVolume = ssgiVolume;
            m_SSGIPass.enableRenderingLayers = enableRenderingLayers;
            m_SSGIPass.overrideAmbientLighting = m_OverrideAmbientLighting;
            m_SSGIPass.forwardGBufferPass = m_ForwardGBufferPass;

            m_SSGIPass.ConfigureDenoisers(this, ssgiVolume);

            bool skyFallback = ssgiVolume.IsFallbackSky();
            if (skyFallback)
            {
                m_SSGIMaterial.EnableKeyword(_RAYMARCHING_FALLBACK_SKY);
            }
            else
            {
                m_SSGIMaterial.DisableKeyword(_RAYMARCHING_FALLBACK_SKY);
            }

#if UNITY_2023_1_OR_NEWER
            bool outputAPVLighting =
                m_OverrideAmbientLighting
                && skyFallback
                && (
                    Shader.IsKeywordEnabled(PROBE_VOLUMES_L1)
                    || Shader.IsKeywordEnabled(PROBE_VOLUMES_L2)
                );
            if (outputAPVLighting)
            {
                m_SSGIMaterial.EnableKeyword(_APV_LIGHTING_BUFFER);
            }
            else
            {
                m_SSGIMaterial.DisableKeyword(_APV_LIGHTING_BUFFER);
            }
            m_SSGIPass.outputAPVLighting = outputAPVLighting;
#else
            // APV is not supported on URP 14
            m_SSGIPass.outputAPVLighting = false;
            m_SSGIMaterial.DisableKeyword(_APV_LIGHTING_BUFFER);
#endif
            bool reflectionProbesFallback = ssgiVolume.IsFallbackReflectionProbes();
            if (reflectionProbesFallback)
            {
                m_SSGIMaterial.EnableKeyword(_RAYMARCHING_FALLBACK_REFLECTION_PROBES);
            }
            else
            {
                m_SSGIMaterial.DisableKeyword(_RAYMARCHING_FALLBACK_REFLECTION_PROBES);
            }

#if UNITY_6000_1_OR_NEWER
            bool hasProbeAtlas =
                Shader.IsKeywordEnabled(_CLUSTER_LIGHT_LOOP)
                && Shader.IsKeywordEnabled(_REFLECTION_PROBE_ATLAS);
#else
            bool hasProbeAtlas = Shader.IsKeywordEnabled(_FORWARD_PLUS);
#endif
            if (hasProbeAtlas && reflectionProbesFallback)
            {
                m_SSGIMaterial.EnableKeyword(_FP_REFL_PROBE_ATLAS);
            } // TODO: change to URP's keyword
            else
            {
                m_SSGIMaterial.DisableKeyword(_FP_REFL_PROBE_ATLAS);
            }
            m_SSGIPass.hasProbeAtlas = hasProbeAtlas;

            bool isReflectionProbe =
                renderingData.cameraData.camera.cameraType == CameraType.Reflection;
            m_SSGIMaterial.SetFloat(_IsProbeCamera, isReflectionProbe ? 1.0f : 0.0f);

            if (m_HighQualityUpscaling)
                m_SSGIMaterial.EnableKeyword(_DEPTH_NORMALS_UPSCALE);
            else
                m_SSGIMaterial.DisableKeyword(_DEPTH_NORMALS_UPSCALE);

#if UNITY_EDITOR
            // [Editor Only] Motion vectors in scene view don't get updated each frame when not entering play mode.
            // So we manually set them in a pass before rendering motion vectors
            if (renderingData.cameraData.camera.cameraType == CameraType.SceneView)
            {
                m_PreRenderSSGIPass.m_SSGIMaterial = m_SSGIMaterial;
                renderer.EnqueuePass(m_PreRenderSSGIPass);
            }

#endif

            if (
                renderingData.cameraData.camera.cameraType != CameraType.Preview
                && (!isDebugger || m_RenderingDebugger)
            )
                renderer.EnqueuePass(m_SSGIPass);

            // For Unity 6.1+:
            // TODO: the following code will cause issues when using "Deferred+" and disabling Render Graph (URP will fall back to "Forward+")
            // Solution: when using "Deferred+" & "RG Compatibility Mode", we should enqueue the Forward GBuffer pass

            // If GBuffer exists, URP is in Deferred path. (Actual rendering mode can be different from settings, such as URP forces Forward on OpenGL)
            bool isUsingDeferred = gBufferFieldInfo.GetValue(renderer) != null;
            // OpenGL won't use deferred path.
            isUsingDeferred &=
                (SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLES3)
                & (SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLCore); // GLES 2 is deprecated.
            m_SSGIPass.usingDeferred = isUsingDeferred;

            bool renderBackfaceData =
                ssgiVolume.thicknessMode.value
                != ScreenSpaceGlobalIlluminationVolume.ThicknessMode.Constant;
            if (renderBackfaceData)
            {
                // Backface lighting is only supported on Forward(+) rendering path.
                bool supportBackfaceLighting = m_BackfaceLighting && !isUsingDeferred;
                m_BackfaceDataPass.backfaceLighting = supportBackfaceLighting;

                renderer.EnqueuePass(m_BackfaceDataPass);

                m_SSGIMaterial.EnableKeyword(_BACKFACE_TEXTURES);
                Shader.EnableKeyword(SSGI_RENDER_BACKFACE_DEPTH);
                if (supportBackfaceLighting)
                {
                    m_SSGIMaterial.SetFloat(_BackDepthEnabled, 2.0f); // Depth + Color
                    Shader.EnableKeyword(SSGI_RENDER_BACKFACE_COLOR);
                }
                else
                {
                    m_SSGIMaterial.SetFloat(_BackDepthEnabled, 1.0f); // Depth
                    Shader.DisableKeyword(SSGI_RENDER_BACKFACE_COLOR);
                }
            }
            else
            {
                m_SSGIMaterial.DisableKeyword(_BACKFACE_TEXTURES);
                Shader.DisableKeyword(SSGI_RENDER_BACKFACE_DEPTH);
                Shader.DisableKeyword(SSGI_RENDER_BACKFACE_COLOR);
                m_SSGIMaterial.SetFloat(_BackDepthEnabled, 0.0f);
            }

#if UNITY_EDITOR || DEBUG
            if (m_BackfaceLighting && isUsingDeferred)
            {
                if (!isBackfaceLightingLogPrinted)
                {
                    Debug.LogError(
                        "Screen Space Global Illumination URP: Backface Lighting is only supported on Forward(+) rendering path."
                    );
                    isBackfaceLightingLogPrinted = true;
                }
            }
            else
                isBackfaceLightingLogPrinted = false;
#endif

            // Render Forward GBuffer pass if the current device supports MRT.
            // Assuming the current device supports at least 4 MRTs since we require Unity shader model 3.5
            if (!isUsingDeferred)
            {
                renderer.EnqueuePass(m_ForwardGBufferPass);
                Shader.EnableKeyword(SSGI_RENDER_GBUFFER);
            }
            else
            {
                Shader.DisableKeyword(SSGI_RENDER_GBUFFER);
            }
        }
    }
}
