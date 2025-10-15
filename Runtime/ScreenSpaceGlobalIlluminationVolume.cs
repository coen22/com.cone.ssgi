using System;
using Cone.SSGI.Denoisers;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Cone.SSGI
{
    // Remove later:

    //////////////////////////////////////////////////////
    // This defines a custom VolumeComponent to be used with the Core VolumeFramework
    // (see core API docs https://docs.unity3d.com/Packages/com.unity.render-pipelines.core@latest/index.html?subfolder=/api/UnityEngine.Rendering.VolumeComponent.html)
    // (see URP docs https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@latest/index.html?subfolder=/manual/Volumes.html)
    //
    // After implementing this class you can:
    // * Tweak the default values for this VolumeComponent in the URP GlobalSettings
    // * Add overrides for this VolumeComponent to any local or global scene volume profiles
    //   (see URP docs https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@latest/index.html?subfolder=/manual/Volume-Profile.html)
    //   (see URP docs https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@latest/index.html?subfolder=/manual/VolumeOverrides.html)
    // * Access the blended values of this VolumeComponent from your ScriptableRenderPasses or scripts using the VolumeManager API
    //   (see core API docs https://docs.unity3d.com/Packages/com.unity.render-pipelines.core@latest/index.html?subfolder=/api/UnityEngine.Rendering.VolumeManager.html)
    // * Override the values for this volume per-camera by placing a VolumeProfile in a dedicated layer and setting the camera's "Volume Mask" to that layer
    //
    // Things to keep in mind:
    // * Be careful when renaming, changing types or removing public fields to not break existing instances of this class (note that this class inherits from ScriptableObject so the same serialization rules apply)
    // * The 'IPostProcessComponent' interface adds the 'IsActive()' method, which is currently not strictly necessary and is for your own convenience
    // * It is recommended to only expose fields that are expected to change. Fields which are constant such as shaders, materials or LUT textures
    //   should likely be in AssetBundles or referenced by serialized fields of your custom ScriptableRendererFeatures on used renderers so they would not get stripped during builds
    //////////////////////////////////////////////////////

#if UNITY_2023_1_OR_NEWER
    [
        VolumeComponentMenu("Lighting/Screen Space Global Illumination (URP)"),
        SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))
    ]
#else
    [VolumeComponentMenuForRenderPipeline(
        "Lighting/Screen Space Global Illumination (URP)",
        typeof(UniversalRenderPipeline)
    )]
#endif
#if UNITY_2023_3_OR_NEWER
    [VolumeRequiresRendererFeatures(typeof(ScreenSpaceGlobalIlluminationURP))]
#endif
    [HelpURL("https://github.com/jiaozi158/UnitySSGIURP/blob/main/Documentation~/Documentation.md")]
    public sealed class ScreenSpaceGlobalIlluminationVolume : VolumeComponent, IPostProcessComponent
    {
        public ScreenSpaceGlobalIlluminationVolume()
        {
            displayName = "Screen Space Global Illumination";
        }

#if !UNITY_2023_2_OR_NEWER
        // This is unused since 2023.1
        public bool IsTileCompatible() => false;
#endif

        /// <summary>
        /// Enable screen space global illumination.
        /// </summary>
        [Tooltip("Enable screen space global illumination.")]
        public BoolParameter enable = new BoolParameter(false, BoolParameter.DisplayType.EnumPopup);

        /// <summary>
        /// Controls the thickness mode of screen space global illumination.
        /// </summary>
        [Tooltip("The thickness mode of screen space global illumination.")]
        public ThicknessParameter thicknessMode = new(
            value: ThicknessMode.Constant,
            overrideState: false
        );

        /// <summary>
        /// The thickness (or fallback thickness) of the depth buffer value used for the ray marching.
        /// </summary>
        [Tooltip(
            "Controls the thickness (or fallback thickness) of the depth buffer used for ray marching."
        )]
        public ClampedFloatParameter depthBufferThickness = new ClampedFloatParameter(
            0.1f,
            0.0f,
            0.5f
        );

        /// <summary>
        /// Gets or sets the quality of screen space global illumination.
        /// </summary>
        public RayMarchingModeParameter qualityMode
        {
            get { return quality; }
            set
            {
                quality = value;
                ApplyCurrentQualityMode();
            }
        }

        /// <summary>
        /// Gets the current screen space global illumination quality.
        /// </summary>
        [Tooltip("Specifies the quality of screen space global illumination.")]
        public RayMarchingModeParameter quality = new(QualityMode.Low, overrideState: false);

        /// <summary>
        /// Defines if the screen space global illumination should be evaluated at full resolution.
        /// </summary>
        [
            InspectorName("Full Resolution"),
            Tooltip(
                "Controls if the screen space global illumination should be evaluated at full resolution."
            )
        ]
        public BoolParameter fullResolutionSS = new BoolParameter(false);

        /// <summary>
        /// Defines the resolution used to evaluate screen space global illumination.
        /// This should not be changed frequently.
        /// </summary>
        [
            InspectorName("Resolution Scale"),
            Tooltip("Controls the resolution used to evaluate screen space global illumination.")
        ]
        public NoInterpClampedFloatParameter resolutionScaleSS = new NoInterpClampedFloatParameter(
            0.5f,
            0.25f,
            0.75f
        );

        /// <summary>
        /// The number of samples for global illumination.
        /// </summary>
        [Tooltip("Controls the number of samples for global illumination.")]
        public ClampedIntParameter sampleCount = new ClampedIntParameter(2, 1, 32);

        /// <summary>
        /// Enables reservoir-based spatio-temporal resampling for diffuse lighting.
        /// </summary>
        [Tooltip("Enables ReSTIR GI to reuse high quality samples across space and time.")]
        public BoolParameter restirGi = new BoolParameter(false);

        /// <summary>
        /// The low-discrepancy sampling sequence applied to ray directions.
        /// </summary>
        [Tooltip("Controls the low-discrepancy sampling pattern used for ray directions.")]
        public SamplingSequenceParameter samplingSequence = new SamplingSequenceParameter(
            SamplingSequence.OwenScrambledSobolBlueNoise,
            false
        );

        /// <summary>
        /// Controls the bias towards normal-aligned sampling directions.
        /// </summary>
        [
            InspectorName("Normal Bias"),
            Tooltip(
                "Blends between uniform and cosine-weighted sampling. Higher values focus rays around the surface normal."
            )
        ]
        public ClampedFloatParameter normalBias = new ClampedFloatParameter(0.8f, 0.0f, 1.0f);

        /// <summary>
        /// The number of steps that should be used during ray marching.
        /// </summary>
        [Tooltip("Controls the number of steps used for ray marching.")]
        public MinIntParameter maxRaySteps = new MinIntParameter(32, 16);

        /// <summary>
        /// Defines if the screen space global illumination should be denoised.
        /// </summary>
        [
            InspectorName("Denoise"),
            Tooltip("Controls if the screen space global illumination should be denoised.")
        ]
        public BoolParameter denoiseSS = new BoolParameter(true);

        /// <summary>
        /// Defines the denoising mode for screen space global illumination.
        /// </summary>
        [
            InspectorName("Algorithm"),
            Tooltip("Controls the denoising mode for screen space global illumination.")
        ]
        public DenoiserAlgorithmParameter denoiserAlgorithmSS = new DenoiserAlgorithmParameter(
            DenoiserAlgorithm.Aggressive,
            false
        );

        /// <summary>
        /// Defines the intensity of temporal denoising pass.
        /// </summary>
        [
            InspectorName("Intensity"),
            Tooltip("Controls the intensity of temporal denoising pass."),
            SSGIDenoiserParameter(DenoiserAlgorithm.Conservative, "Intensity", order: -10),
            SSGIDenoiserParameter(DenoiserAlgorithm.Aggressive, "Intensity", order: -10),
            SSGIDenoiserParameter(DenoiserAlgorithm.HybridTemporal, "Intensity", order: -10)
        ]
        public ClampedFloatParameter denoiseIntensitySS = new ClampedFloatParameter(
            0.95f,
            0.5f,
            0.95f,
            false
        );

        /// <summary>
        /// Defines the radius of the GI denoiser (First Pass).
        /// </summary>
        [
            InspectorName("Denoiser Radius"),
            Tooltip("Controls the radius of the GI denoiser (First Pass)."),
            SSGIDenoiserParameter(DenoiserAlgorithm.Aggressive, "Radius")
        ]
        public ClampedFloatParameter denoiserRadiusSS = new ClampedFloatParameter(
            0.6f,
            0.001f,
            1.0f,
            false
        );

        /// <summary>
        /// Defines if the second denoising pass should be enabled.
        /// </summary>
        [
            InspectorName("Second Denoiser Pass"),
            Tooltip("Enable second denoising pass."),
            SSGIDenoiserParameter(DenoiserAlgorithm.HybridTemporal, "Second Denoiser Pass")
        ]
        public BoolParameter secondDenoiserPassSS = new BoolParameter(false);

        [
            Header("Single Frame Denoiser"),
            InspectorName("Radius (px)"),
            SSGIDenoiserParameter(DenoiserAlgorithm.SingleFrame, "Radius (px)"),
            SSGIDenoiserParameter(DenoiserAlgorithm.HybridTemporal, "Radius (px)")
        ]
        public ClampedFloatParameter singleFrameRadius = new ClampedFloatParameter(
            2.0f,
            1.0f,
            8.0f
        );

        [
            InspectorName("Sigma Color"),
            Tooltip("Color similarity threshold for the single frame denoiser."),
            SSGIDenoiserParameter(DenoiserAlgorithm.SingleFrame, "Sigma Color"),
            SSGIDenoiserParameter(DenoiserAlgorithm.HybridTemporal, "Sigma Color")
        ]
        public ClampedFloatParameter singleFrameSigmaColor = new ClampedFloatParameter(
            0.15f,
            0.01f,
            1.0f
        );

        [
            InspectorName("Sigma Normal"),
            Tooltip("Normal similarity threshold for the single frame denoiser."),
            SSGIDenoiserParameter(DenoiserAlgorithm.SingleFrame, "Sigma Normal"),
            SSGIDenoiserParameter(DenoiserAlgorithm.HybridTemporal, "Sigma Normal")
        ]
        public ClampedFloatParameter singleFrameSigmaNormal = new ClampedFloatParameter(
            0.25f,
            0.01f,
            1.0f
        );

        [
            InspectorName("Sigma Depth"),
            Tooltip("Depth similarity threshold for the single frame denoiser."),
            SSGIDenoiserParameter(DenoiserAlgorithm.SingleFrame, "Sigma Depth"),
            SSGIDenoiserParameter(DenoiserAlgorithm.HybridTemporal, "Sigma Depth")
        ]
        public ClampedFloatParameter singleFrameSigmaDepth = new ClampedFloatParameter(
            0.015f,
            0.001f,
            0.2f
        );

        [
            InspectorName("Albedo Weight"),
            Tooltip("Blending factor for albedo guidance in the single frame denoiser."),
            SSGIDenoiserParameter(DenoiserAlgorithm.SingleFrame, "Albedo Weight"),
            SSGIDenoiserParameter(DenoiserAlgorithm.HybridTemporal, "Albedo Weight")
        ]
        public ClampedFloatParameter singleFrameAlbedoWeight = new ClampedFloatParameter(
            0.30f,
            0.0f,
            1.0f
        );

        [
            InspectorName("Luma Weight"),
            Tooltip("Contribution of luminance guidance in the single frame denoiser."),
            SSGIDenoiserParameter(DenoiserAlgorithm.SingleFrame, "Luma Weight"),
            SSGIDenoiserParameter(DenoiserAlgorithm.HybridTemporal, "Luma Weight")
        ]
        public ClampedFloatParameter singleFrameLumaWeight = new ClampedFloatParameter(
            1.0f,
            0.0f,
            1.0f
        );

        [
            InspectorName("Minimum Weight"),
            Tooltip("Lower bound for filter weights in the single frame denoiser."),
            SSGIDenoiserParameter(DenoiserAlgorithm.SingleFrame, "Minimum Weight"),
            SSGIDenoiserParameter(DenoiserAlgorithm.HybridTemporal, "Minimum Weight")
        ]
        public ClampedFloatParameter singleFrameMinWeight = new ClampedFloatParameter(
            1e-4f,
            1e-6f,
            1e-2f
        );

        [
            Header("Edge Adaptive LUT"),
            InspectorName("Radii (px)"),
            Tooltip("Filter radii per edge tier (low → high complexity)."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAdaptiveLut, "Radii (px)")
        ]
        public Vector4Parameter adaptiveRadii = new Vector4Parameter(
            new Vector4(1.0f, 1.5f, 2.0f, 3.0f)
        );

        [
            InspectorName("Edge Thresholds"),
            Tooltip("Edge complexity breakpoints that select the filter radius."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAdaptiveLut, "Edge Thresholds")
        ]
        public Vector4Parameter adaptiveEdgeThresholds = new Vector4Parameter(
            new Vector4(0.08f, 0.16f, 0.32f, 0.32f)
        );

        [
            InspectorName("Depth Scale"),
            Tooltip("Contribution of depth gradients when computing edge complexity."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAdaptiveLut, "Depth Scale")
        ]
        public ClampedFloatParameter adaptiveDepthScale = new ClampedFloatParameter(
            1.0f,
            0.0f,
            10.0f
        );

        [
            InspectorName("Normal Scale"),
            Tooltip("Contribution of normal gradients when computing edge complexity."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAdaptiveLut, "Normal Scale")
        ]
        public ClampedFloatParameter adaptiveNormalScale = new ClampedFloatParameter(
            2.0f,
            0.0f,
            10.0f
        );

        [
            InspectorName("Guide Normal Power"),
            Tooltip("Exponent applied to the normal dot product gate."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAdaptiveLut, "Guide Normal Power")
        ]
        public MinFloatParameter adaptiveGuideNormalPower = new MinFloatParameter(8.0f, 0.0f);

        [
            InspectorName("Guide Depth Scale"),
            Tooltip("Scale factor applied to the reciprocal depth gate."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAdaptiveLut, "Guide Depth Scale")
        ]
        public MinFloatParameter adaptiveGuideDepthScale = new MinFloatParameter(80.0f, 0.0f);

        [
            InspectorName("Max Distance"),
            Tooltip("Normalised distance (in pixels) used for the LUT V coordinate."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAdaptiveLut, "Max Distance")
        ]
        public MinFloatParameter adaptiveMaxDistance = new MinFloatParameter(4.0f, 0.1f);

        [
            InspectorName("Minimum Weight"),
            Tooltip("Lower bound for LUT weights to keep taps in the accumulation."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAdaptiveLut, "Minimum Weight")
        ]
        public MinFloatParameter adaptiveMinWeight = new MinFloatParameter(1e-5f, 0.0f);

        [
            InspectorName("Normal Gate"),
            Tooltip("Enables normal-based gating on spatial taps."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAdaptiveLut, "Normal Gate")
        ]
        public BoolParameter adaptiveUseNormalGate = new BoolParameter(true);

        [
            InspectorName("Depth Gate"),
            Tooltip("Enables depth-based gating on spatial taps."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAdaptiveLut, "Depth Gate")
        ]
        public BoolParameter adaptiveUseDepthGate = new BoolParameter(true);

        [
            Header("Hybrid Temporal"),
            InspectorName("Motion Threshold (m)"),
            Tooltip(
                "Maximum camera displacement allowed between frames before switching to single frame filtering."
            ),
            SSGIDenoiserParameter(DenoiserAlgorithm.HybridTemporal, "Motion Threshold (m)")
        ]
        public MinFloatParameter hybridMotionThreshold = new MinFloatParameter(0.05f, 0.0f);

        [
            Header("NRD"),
            InspectorName("Motion Threshold (m)"),
            Tooltip("Camera motion magnitude before NRD reduces reliance on history."),
            SSGIDenoiserParameter(DenoiserAlgorithm.NRD, "Motion Threshold (m)")
        ]
        public MinFloatParameter nrdMotionThreshold = new MinFloatParameter(0.05f, 0.0f);

        [
            InspectorName("Max Frames"),
            Tooltip("Maximum number of frames blended in the NRD temporal accumulator."),
            SSGIDenoiserParameter(DenoiserAlgorithm.NRD, "Max Frames")
        ]
        public ClampedIntParameter nrdMaxAccumulatedFrames = new ClampedIntParameter(32, 1, 128);

        [
            InspectorName("Fast History"),
            Tooltip("Window for the fast history clamp used to prevent smearing."),
            SSGIDenoiserParameter(DenoiserAlgorithm.NRD, "Fast History")
        ]
        public ClampedIntParameter nrdFastHistoryLength = new ClampedIntParameter(6, 1, 16);

        [
            InspectorName("Disocclusion Threshold"),
            Tooltip("Depth delta (view-space) that triggers history rejection."),
            SSGIDenoiserParameter(DenoiserAlgorithm.NRD, "Disocclusion Threshold")
        ]
        public MinFloatParameter nrdDisocclusionThreshold = new MinFloatParameter(0.1f, 0.0f);

        [
            InspectorName("Sigma Multiplier"),
            Tooltip("Variance multiplier for clamp range (μ ± kσ)."),
            SSGIDenoiserParameter(DenoiserAlgorithm.NRD, "Sigma Multiplier")
        ]
        public ClampedFloatParameter nrdSigmaMultiplier = new ClampedFloatParameter(
            2.5f,
            0.5f,
            6.0f
        );

        [
            InspectorName("Spatial Iterations"),
            Tooltip("Number of NRD spatial filter iterations for diffuse/specular signals."),
            SSGIDenoiserParameter(DenoiserAlgorithm.NRD, "Spatial Iterations")
        ]
        public ClampedIntParameter nrdSpatialIterations = new ClampedIntParameter(3, 1, 4);

        [
            InspectorName("Spatial Radius (px)"),
            Tooltip("Base kernel radius used by the NRD spatial stage."),
            SSGIDenoiserParameter(DenoiserAlgorithm.NRD, "Spatial Radius (px)")
        ]
        public MinFloatParameter nrdSpatialRadius = new MinFloatParameter(1.5f, 0.5f);

        [
            Header("Temporal Accumulation"),
            InspectorName("Enable Temporal"),
            Tooltip("Enable temporal reprojection and history clamping for the LUT denoiser."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAdaptiveLut, "Enable Temporal")
        ]
        public BoolParameter adaptiveUseTemporal = new BoolParameter(false);

        [
            InspectorName("Max Frames (Main)"),
            Tooltip("Maximum history length for the main accumulation buffer."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAdaptiveLut, "Max Frames (Main)")
        ]
        public ClampedIntParameter adaptiveTemporalMaxFrames = new ClampedIntParameter(32, 1, 128);

        [
            InspectorName("Max Frames (Fast)"),
            Tooltip("Maximum history length for the fast clamp buffer."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAdaptiveLut, "Max Frames (Fast)")
        ]
        public ClampedIntParameter adaptiveTemporalMaxFastFrames = new ClampedIntParameter(
            6,
            1,
            16
        );

        [
            InspectorName("Depth Tolerance"),
            Tooltip("View-space depth tolerance used to reject reprojected history."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAdaptiveLut, "Depth Tolerance")
        ]
        public MinFloatParameter adaptiveTemporalDepthTolerance = new MinFloatParameter(
            0.05f,
            0.0f
        );

        [
            InspectorName("Anti-Firefly (σ)"),
            Tooltip("Number of standard deviations used to clamp luminance outliers."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAdaptiveLut, "Anti-Firefly (σ)")
        ]
        public ClampedFloatParameter adaptiveTemporalAntiFirefly = new ClampedFloatParameter(
            2.5f,
            0.0f,
            5.0f
        );

        [
            InspectorName("Clamp Bias"),
            Tooltip("Bias added to the spatial clamp extent to avoid stalls."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAdaptiveLut, "Clamp Bias")
        ]
        public MinFloatParameter adaptiveTemporalClampBias = new MinFloatParameter(0.001f, 0.0f);

        [
            InspectorName("Variance Epsilon"),
            Tooltip("Floor applied to variance when computing sigma."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAdaptiveLut, "Variance Epsilon")
        ]
        public MinFloatParameter adaptiveTemporalVarianceEpsilon = new MinFloatParameter(
            1e-4f,
            0.0f
        );

        [
            Header("Edge Aware A-Trous"),
            InspectorName("Fast Schedule"),
            Tooltip("Use the fast GPU-friendly schedule for the edge-aware A-Trous denoiser."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAwareAtrous, "Fast Schedule", order: -20)
        ]
        public BoolParameter fastAtrousSchedule = new BoolParameter(true);

        [
            InspectorName("Iterations"),
            Tooltip("Number of A-trous passes to run."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAwareAtrous, "Iterations")
        ]
        public ClampedIntParameter atrousIterations = new ClampedIntParameter(2, 1, 6);

        [
            InspectorName("Base Step"),
            Tooltip("Initial kernel radius (in pixels) for the A-trous pass."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAwareAtrous, "Base Step")
        ]
        public ClampedIntParameter atrousBaseStep = new ClampedIntParameter(1, 1, 4);

        [
            InspectorName("Sigma Color"),
            Tooltip("Color similarity threshold for the A-trous denoiser."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAwareAtrous, "Sigma Color")
        ]
        public ClampedFloatParameter atrousSigmaColor = new ClampedFloatParameter(
            0.20f,
            0.01f,
            1.0f
        );

        [
            InspectorName("Sigma Normal"),
            Tooltip("Normal similarity threshold for the A-trous denoiser."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAwareAtrous, "Sigma Normal")
        ]
        public ClampedFloatParameter atrousSigmaNormal = new ClampedFloatParameter(
            0.30f,
            0.01f,
            1.0f
        );

        [
            InspectorName("Sigma Depth"),
            Tooltip("Depth similarity threshold for the A-trous denoiser."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAwareAtrous, "Sigma Depth")
        ]
        public ClampedFloatParameter atrousSigmaDepth = new ClampedFloatParameter(
            0.02f,
            0.001f,
            0.2f
        );

        [
            InspectorName("Albedo Weight"),
            Tooltip("Blending factor for albedo guidance in the A-trous denoiser."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAwareAtrous, "Albedo Weight")
        ]
        public ClampedFloatParameter atrousAlbedoWeight = new ClampedFloatParameter(
            0.30f,
            0.0f,
            1.0f
        );

        [
            InspectorName("Minimum Weight"),
            Tooltip("Lower bound for filter weights in the A-trous denoiser."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAwareAtrous, "Minimum Weight")
        ]
        public ClampedFloatParameter atrousMinWeight = new ClampedFloatParameter(
            1e-4f,
            1e-6f,
            1e-2f
        );

        [
            InspectorName("Edge Depth Reject"),
            Tooltip("Depth difference threshold for rejecting samples in the A-trous denoiser."),
            SSGIDenoiserParameter(DenoiserAlgorithm.EdgeAwareAtrous, "Edge Depth Reject")
        ]
        public ClampedFloatParameter atrousEdgeDepthReject = new ClampedFloatParameter(
            0.05f,
            0.0f,
            0.25f
        );

        [
            Header("Weighted À-Trous Linear Regression"),
            InspectorName("Iterations"),
            Tooltip("Number of WALR passes to accumulate before solving the regression."),
            SSGIDenoiserParameter(DenoiserAlgorithm.WeightedAtrousLinearRegression, "Iterations")
        ]
        public ClampedIntParameter walrIterations = new ClampedIntParameter(2, 1, 6);

        [
            InspectorName("Base Step"),
            Tooltip("Initial à-trous step size for the WALR gather."),
            SSGIDenoiserParameter(DenoiserAlgorithm.WeightedAtrousLinearRegression, "Base Step")
        ]
        public ClampedIntParameter walrBaseStep = new ClampedIntParameter(1, 1, 4);

        [
            InspectorName("Sigma Depth"),
            Tooltip("Depth similarity threshold for the WALR regression weights."),
            SSGIDenoiserParameter(DenoiserAlgorithm.WeightedAtrousLinearRegression, "Sigma Depth")
        ]
        public ClampedFloatParameter walrSigmaDepth = new ClampedFloatParameter(
            0.02f,
            0.001f,
            0.2f
        );

        [
            InspectorName("Sigma Normal"),
            Tooltip("Normal similarity threshold for the WALR regression weights."),
            SSGIDenoiserParameter(DenoiserAlgorithm.WeightedAtrousLinearRegression, "Sigma Normal")
        ]
        public ClampedFloatParameter walrSigmaNormal = new ClampedFloatParameter(
            0.30f,
            0.05f,
            1.0f
        );

        [
            InspectorName("Sigma Albedo"),
            Tooltip("Albedo similarity threshold for the WALR regression weights."),
            SSGIDenoiserParameter(DenoiserAlgorithm.WeightedAtrousLinearRegression, "Sigma Albedo")
        ]
        public ClampedFloatParameter walrSigmaAlbedo = new ClampedFloatParameter(
            0.20f,
            0.01f,
            1.0f
        );

        [
            InspectorName("Albedo Weight"),
            Tooltip("Blending factor for albedo guidance in the WALR regression."),
            SSGIDenoiserParameter(DenoiserAlgorithm.WeightedAtrousLinearRegression, "Albedo Weight")
        ]
        public ClampedFloatParameter walrAlbedoWeight = new ClampedFloatParameter(
            0.30f,
            0.0f,
            1.0f
        );

        [
            InspectorName("Minimum Weight"),
            Tooltip("Lower bound for filter weights in the WALR regression."),
            SSGIDenoiserParameter(
                DenoiserAlgorithm.WeightedAtrousLinearRegression,
                "Minimum Weight"
            )
        ]
        public ClampedFloatParameter walrMinWeight = new ClampedFloatParameter(1e-4f, 1e-6f, 1e-2f);

        /// <summary>
        /// Controls the fallback hierarchy for indirect diffuse in case the ray misses.
        /// </summary>
        [Tooltip("Controls the fallback hierarchy for indirect diffuse in case the ray misses.")]
        public RayMarchingFallbackHierarchyParameter rayMiss =
            new RayMarchingFallbackHierarchyParameter(
                RayMarchingFallbackHierarchy.ReflectionProbesAndSky
            );

        /// <summary>
        /// Controls the indirect diffuse lighting from screen space global illumination.
        /// </summary>
        [
            Header("Artistic Overrides"),
            InspectorName("Indirect Diffuse Lighting Multiplier"),
            Tooltip("Controls the indirect diffuse lighting from screen space global illumination.")
        ]
        public MinFloatParameter indirectDiffuseLightingMultiplier = new MinFloatParameter(
            1.0f,
            0.0f
        );

#if UNITY_2023_3_OR_NEWER
        /// <summary>
        /// Controls which rendering layer will be affected by screen space global illumination.
        /// </summary>
        [
            AdditionalProperty,
            InspectorName("Indirect Diffuse Rendering Layers"),
            Tooltip(
                "Controls which rendering layer will be affected by screen space global illumination."
            )
        ]
        public RenderingLayerEnumParameter indirectDiffuseRenderingLayers =
            new RenderingLayerEnumParameter(-1); // RenderingLayerMask.Everything
#endif

        public bool IsActive()
        {
#if UNITY_2023_3_OR_NEWER
            return enable.value && indirectDiffuseRenderingLayers.value.value != 0; // RenderingLayerMask.Nothing
#else
            return enable.value;
#endif
        }

        public enum DenoiserAlgorithm
        {
            [Tooltip("Produces results with more detail, but may retain some noise.")]
            Conservative = 0,

            [Tooltip("Produces cleaner results.")]
            Aggressive = 1,

            [
                InspectorName("Single Frame"),
                Tooltip("Applies a purely spatial denoiser that does not require motion vectors.")
            ]
            SingleFrame = 2,

            [
                InspectorName("Edge Aware A-Trous"),
                Tooltip("Multi-pass edge-aware A-trous spatial denoiser (single frame).")
            ]
            EdgeAwareAtrous = 3,

            [
                InspectorName("Weighted À-Trous LR"),
                Tooltip("Performs weighted à-trous linear regression denoising for diffuse GI.")
            ]
            WeightedAtrousLinearRegression = 4,

            [
                InspectorName("Edge Adaptive LUT"),
                Tooltip(
                    "Adaptive single-frame denoiser that queries a precomputed weight table and shrinks the kernel near edges."
                )
            ]
            EdgeAdaptiveLut = 5,

            [
                InspectorName("Hybrid Temporal"),
                Tooltip(
                    "Switches between temporal accumulation and single frame filtering based on camera motion."
                )
            ]
            HybridTemporal = 6,

            [
                InspectorName("NRD"),
                Tooltip(
                    "NRD-style temporal + spatial denoising for diffuse, specular, and shadow signals."
                )
            ]
            NRD,
        }

        public enum SamplingSequence
        {
            [InspectorName("Hammersley + CP Rotation"), Tooltip("Classic Hammersley sequence with a Cranley–Patterson rotation.")]
            HammersleyCranleyPatterson = 0,

            [InspectorName("R2 (Golden Ratio) + CP Rotation"), Tooltip("R2 sequence decorrelated with a Cranley–Patterson rotation.")]
            R2CranleyPatterson = 1,

            [
                InspectorName("Owen-Scrambled Sobol + Heitz Mapping"),
                Tooltip("Hash-based Owen-scrambled Sobol sequence combined with the Heitz screen-space permutation.")
            ]
            OwenScrambledSobolBlueNoise = 2,

            [
                InspectorName("CMJ (Correlated Multi-Jittered)"),
                Tooltip("Stratified CMJ layout with per-pixel hashing for random access on the unit square.")
            ]
            CorrelatedMultiJittered = 3,

            [
                InspectorName("PMJ (Progressive Multi-Jittered)"),
                Tooltip("Progressive multi-jittered sequence that remains well-stratified as sample counts grow.")
            ]
            ProgressiveMultiJittered = 4,

            [
                InspectorName("PMJ-BN (Progressive MJ + Blue Noise)"),
                Tooltip("PMJ sequence with blue-noise biased jitter for improved low sample visual quality.")
            ]
            ProgressiveMultiJitteredBlueNoise = 5,

            [
                InspectorName("Sobol-Burley (Hashed Owen + Shuffle)"),
                Tooltip("Stateless Owen-scrambled Sobol with Burley permutations for dimension padding.")
            ]
            SobolBurley = 6,

            [
                InspectorName("Orthogonal Array Sampling"),
                Tooltip("Higher-order stratified pattern derived from orthogonal arrays across the unit square.")
            ]
            OrthogonalArray = 7,

            [
                InspectorName("Screen-Space Blue-Noise Diffusion"),
                Tooltip("Applies screen-space blue-noise diffusion to Sobol samples for perceptually smooth error.")
            ]
            ScreenSpaceBlueNoiseDiffusion = 8,
        }

        /// <summary>
        /// A <see cref="VolumeParameter"/> that holds a <see cref="DenoiserAlgorithm"/> value.
        /// </summary>
        [Serializable]
        public sealed class DenoiserAlgorithmParameter : VolumeParameter<DenoiserAlgorithm>
        {
            /// <summary>
            /// Creates a new <see cref="DenoiserAlgorithmParameter"/> instance.
            /// </summary>
            /// <param name="value">The initial value to store in the parameter.</param>
            /// <param name="overrideState">The initial override state for the parameter.</param>
            public DenoiserAlgorithmParameter(DenoiserAlgorithm value, bool overrideState = false)
                : base(value, overrideState) { }
        }

        /// <summary>
        /// A <see cref="VolumeParameter"/> that holds a <see cref="SamplingSequence"/> value.
        /// </summary>
        [Serializable]
        public sealed class SamplingSequenceParameter : VolumeParameter<SamplingSequence>
        {
            /// <summary>
            /// Creates a new <see cref="SamplingSequenceParameter"/> instance.
            /// </summary>
            /// <param name="value">The initial value to store in the parameter.</param>
            /// <param name="overrideState">The initial override state for the parameter.</param>
            public SamplingSequenceParameter(SamplingSequence value, bool overrideState = false)
                : base(value, overrideState) { }
        }

        public enum ThicknessMode
        {
            [InspectorName("Constant"), Tooltip("Apply constant thickness to every scene object.")]
            Constant = 0,

            [
                InspectorName("Automatic"),
                Tooltip("Render the back-faces of scene objects to compute thickness.")
            ]
            ComputeBackface = 1,
        }

        /// <summary>
        /// A <see cref="VolumeParameter"/> that holds a <see cref="ThicknessMode"/> value.
        /// </summary>
        [Serializable]
        public sealed class ThicknessParameter : VolumeParameter<ThicknessMode>
        {
            /// <summary>
            /// Creates a new <see cref="SSGIThicknessParameter"/> instance.
            /// </summary>
            /// <param name="value">The initial value to store in the parameter.</param>
            /// <param name="overrideState">The initial override state for the parameter.</param>
            public ThicknessParameter(ThicknessMode value, bool overrideState = false)
                : base(value, overrideState) { }
        }

        public enum QualityMode
        {
            /// <summary>
            /// When selected, choices are made to reduce execution time of the effect.
            /// </summary>
            [Tooltip("When selected, choices are made to reduce execution time of the effect.")]
            Low = 0,

            /// <summary>
            /// When selected, choices are made to increase the visual quality of the effect.
            /// </summary>
            [Tooltip(
                "When selected, choices are made to increase the visual quality of the effect."
            )]
            Medium = 1,

            /// <summary>
            /// When selected, choices are made to increase the visual quality of the effect.
            /// </summary>
            [Tooltip(
                "When selected, choices are made to increase the visual quality of the effect."
            )]
            High = 2,

            /// <summary>
            /// When selected, choices are made to increase the visual quality of the effect.
            /// </summary>
            [Tooltip(
                "When selected, choices are made to increase the visual quality of the effect."
            )]
            Custom = 3,
        }

        /// <summary>
        /// A <see cref="VolumeParameter"/> that holds a <see cref="QualityMode"/> value.
        /// </summary>
        [Serializable]
        public sealed class RayMarchingModeParameter : VolumeParameter<QualityMode>
        {
            /// <summary>
            /// Creates a new <see cref="RayMarchingModeParameter"/> instance.
            /// </summary>
            /// <param name="value">The initial value to store in the parameter.</param>
            /// <param name="overrideState">The initial override state for the parameter.</param>
            public RayMarchingModeParameter(QualityMode value, bool overrideState = false)
                : base(value, overrideState) { }
        }

        /// <summary>
        /// This defines the order in which the fall backs are used if a screen space global illumination ray misses.
        /// </summary>
        public enum RayMarchingFallbackHierarchy
        {
            /// <summary>
            /// When selected, ray marching will return a black color.
            /// </summary>
            [
                InspectorName("Nothing"),
                Tooltip("When selected, ray marching will return a black color.")
            ]
            None = 0x00,

            /// <summary>
            /// When selected, ray marching will fall back on the sky.
            /// </summary>
            [
                InspectorName("Sky"),
                Tooltip("When selected, ray marching will fall back on the sky.")
            ]
            Sky = 0x01,

            /// <summary>
            /// When selected, ray marching will fall back on reflection probes (if any).
            /// </summary>
            [
                InspectorName("Reflection Probes"),
                Tooltip("When selected, ray marching will fall back on reflection probes (if any).")
            ]
            ReflectionProbes = 0x02,

            /// <summary>
            /// When selected, ray marching will fall back on reflection probes (if any) then on the sky.
            /// </summary>
            [
                InspectorName("Reflection Probes and Sky"),
                Tooltip(
                    "When selected, ray marching will fall back on reflection probes (if any) then on the sky."
                )
            ]
            ReflectionProbesAndSky = 0x03,
        }

        /// <summary>
        /// A <see cref="VolumeParameter"/> that holds
        /// <see cref="RayMarchingFallbackHierarchy"/> value.
        /// </summary>
        [Serializable]
        public sealed class RayMarchingFallbackHierarchyParameter
            : VolumeParameter<RayMarchingFallbackHierarchy>
        {
            /// <summary>
            /// Creates a new <see cref="RayMarchingFallbackHierarchyParameter"/> instance.
            /// </summary>
            /// <param name="value">The initial value to store in the parameter.</param>
            /// <param name="overrideState">The initial override state for the parameter.</param>
            public RayMarchingFallbackHierarchyParameter(
                RayMarchingFallbackHierarchy value,
                bool overrideState = false
            )
                : base(value, overrideState) { }
        }

        /// <summary>
        /// Determines if the current fallback hierarchy includes the sky.
        /// </summary>
        public bool IsFallbackSky()
        {
            return (rayMiss.value & RayMarchingFallbackHierarchy.Sky) != 0;
        }

        /// <summary>
        /// Determines if the current fallback hierarchy includes reflection probes.
        /// </summary>
        public bool IsFallbackReflectionProbes()
        {
            return (rayMiss.value & RayMarchingFallbackHierarchy.ReflectionProbes) != 0;
        }

#if UNITY_2023_3_OR_NEWER
        /// <summary>
        /// A <see cref="VolumeParameter"/> that holds
        /// <see cref="RenderingLayerMask"/> value.
        /// </summary>
        [Serializable]
        public sealed class RenderingLayerEnumParameter : VolumeParameter<RenderingLayerMask>
        {
            /// <summary>
            /// Creates a new <see cref="RenderingLayerEnumParameter"/> instance.
            /// </summary>
            /// <param name="value">The initial value to store in the parameter.</param>
            /// <param name="overrideState">The initial override state for the parameter.</param>
            public RenderingLayerEnumParameter(RenderingLayerMask value, bool overrideState = false)
                : base(value, overrideState) { }
        }
#endif

        private void ApplyCurrentQualityMode()
        {
            // Apply the currently set preset
            switch (quality.value)
            {
                case QualityMode.Low:
                    {
                        sampleCount.value = 1;
                        maxRaySteps.value = 24;
                    }
                    break;
                case QualityMode.Medium:
                    {
                        sampleCount.value = 2;
                        maxRaySteps.value = 32;
                    }
                    break;
                case QualityMode.High:
                    {
                        sampleCount.value = 4;
                        maxRaySteps.value = 64;
                    }
                    break;
                default:
                    break;
            }
        }
    }
}
