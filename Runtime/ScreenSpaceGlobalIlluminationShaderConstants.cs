using UnityEngine;

namespace Cone.SSGI
{
    internal static class ScreenSpaceGlobalIlluminationShaderConstants
    {
        internal static readonly int _MaxSteps = Shader.PropertyToID("_MaxSteps");
        internal static readonly int _MaxSmallSteps = Shader.PropertyToID("_MaxSmallSteps");
        internal static readonly int _MaxMediumSteps = Shader.PropertyToID("_MaxMediumSteps");
        internal static readonly int _Thickness = Shader.PropertyToID("_Thickness");
        internal static readonly int _Thickness_Increment = Shader.PropertyToID(
            "_Thickness_Increment"
        );
        internal static readonly int _StepSize = Shader.PropertyToID("_StepSize");
        internal static readonly int _SmallStepSize = Shader.PropertyToID("_SmallStepSize");
        internal static readonly int _MediumStepSize = Shader.PropertyToID("_MediumStepSize");
        internal static readonly int _RayCount = Shader.PropertyToID("_RayCount");
        internal static readonly int _NormalBias = Shader.PropertyToID("_NormalBias");
        internal static readonly int _TemporalIntensity = Shader.PropertyToID("_TemporalIntensity");
        internal static readonly int _UseMotionVectorsID = Shader.PropertyToID("_UseMotionVectors");
        internal static readonly int _MaxBrightness = Shader.PropertyToID("_MaxBrightness");
        internal static readonly int _IsProbeCamera = Shader.PropertyToID("_IsProbeCamera");
        internal static readonly int _BackDepthEnabled = Shader.PropertyToID("_BackDepthEnabled");
        internal static readonly int _PrevInvViewProjMatrix = Shader.PropertyToID(
            "_PrevInvViewProjMatrix"
        );
        internal static readonly int _PrevCameraPositionWS = Shader.PropertyToID(
            "_PrevCameraPositionWS"
        );
        internal static readonly int _PixelSpreadAngleTangent = Shader.PropertyToID(
            "_PixelSpreadAngleTangent"
        );
        internal static readonly int _HistoryTextureValid = Shader.PropertyToID(
            "_HistoryTextureValid"
        );
        internal static readonly int _IndirectDiffuseLightingMultiplier = Shader.PropertyToID(
            "_IndirectDiffuseLightingMultiplier"
        );
        internal static readonly int _ZBufferParams = Shader.PropertyToID("_ZBufferParams");
        internal static readonly int _IndirectDiffuseRenderingLayers = Shader.PropertyToID(
            "_IndirectDiffuseRenderingLayers"
        );
        internal static readonly int _AggressiveDenoise = Shader.PropertyToID("_AggressiveDenoise");
        internal static readonly int _ReBlurBlurRotator = Shader.PropertyToID("_ReBlurBlurRotator");
        internal static readonly int _ReBlurDenoiserRadius = Shader.PropertyToID(
            "_ReBlurDenoiserRadius"
        );

        internal const string _CameraDepthTexture = "_CameraDepthTexture";
        internal const string _IndirectDiffuseTexture = "_IndirectDiffuseTexture";
        internal const string _IntermediateIndirectDiffuseTexture =
            "_IntermediateIndirectDiffuseTexture";
        internal const string _IntermediateCameraColorTexture = "_IntermediateCameraColorTexture";
        internal const string _SSGIHistoryDepthTexture = "_SSGIHistoryDepthTexture";
        internal const string _HistoryIndirectDiffuseTexture = "_HistoryIndirectDiffuseTexture";
        internal const string _ReSTIRReservoirTexture = "_ReSTIRReservoirTexture";
        internal const string _ReSTIRReservoirMomentsTexture = "_ReSTIRReservoirMomentsTexture";
        internal const string _SSGISampleTexture = "_SSGISampleTexture";
        internal const string _SSGIHistorySampleTexture = "_SSGIHistorySampleTexture";
        internal const string _SSGIHistoryCameraColorTexture = "_SSGIHistoryCameraColorTexture";
        internal const string _APVLightingTexture = "_APVLightingTexture";

        internal static readonly int cameraDepthTexture = Shader.PropertyToID(_CameraDepthTexture);
        internal static readonly int indirectDiffuseTexture = Shader.PropertyToID(
            _IndirectDiffuseTexture
        );
        internal static readonly int ssgiHistoryDepthTexture = Shader.PropertyToID(
            _SSGIHistoryDepthTexture
        );
        internal static readonly int historyIndirectDiffuseTexture = Shader.PropertyToID(
            _HistoryIndirectDiffuseTexture
        );
        internal static readonly int restirReservoirTexture = Shader.PropertyToID(
            _ReSTIRReservoirTexture
        );
        internal static readonly int restirReservoirMomentsTexture = Shader.PropertyToID(
            _ReSTIRReservoirMomentsTexture
        );
        internal static readonly int ssgiSampleTexture = Shader.PropertyToID(_SSGISampleTexture);
        internal static readonly int ssgiHistorySampleTexture = Shader.PropertyToID(
            _SSGIHistorySampleTexture
        );
        internal static readonly int ssgiHistoryCameraColorTexture = Shader.PropertyToID(
            _SSGIHistoryCameraColorTexture
        );
        internal static readonly int apvLightingTexture = Shader.PropertyToID(_APVLightingTexture);

        internal const string _GBuffer0 = "_GBuffer0";
        internal const string _GBuffer1 = "_GBuffer1";
        internal const string _GBuffer2 = "_GBuffer2";
        internal const string _GBufferDepth = "_GBufferDepthTexture";

        internal static readonly int gBuffer0 = Shader.PropertyToID(_GBuffer0);
        internal static readonly int gBuffer1 = Shader.PropertyToID(_GBuffer1);
        internal static readonly int gBuffer2 = Shader.PropertyToID(_GBuffer2);

        internal static readonly int specCube0 = Shader.PropertyToID("_SpecCube0");
        internal static readonly int specCube0_HDR = Shader.PropertyToID("_SpecCube0_HDR");
        internal static readonly int specCube0_BoxMin = Shader.PropertyToID("_SpecCube0_BoxMin");
        internal static readonly int specCube0_BoxMax = Shader.PropertyToID("_SpecCube0_BoxMax");
        internal static readonly int specCube0_ProbePosition = Shader.PropertyToID(
            "_SpecCube0_ProbePosition"
        );
        internal static readonly int probeWeight = Shader.PropertyToID("_ProbeWeight");
        internal static readonly int probeSet = Shader.PropertyToID("_ProbeSet");

        internal static readonly int downSample = Shader.PropertyToID("_DownSample");
        internal static readonly int frameIndex = Shader.PropertyToID("_FrameIndex");

        internal static readonly int shAr = Shader.PropertyToID("ssgi_SHAr");
        internal static readonly int shAg = Shader.PropertyToID("ssgi_SHAg");
        internal static readonly int shAb = Shader.PropertyToID("ssgi_SHAb");
        internal static readonly int shBr = Shader.PropertyToID("ssgi_SHBr");
        internal static readonly int shBg = Shader.PropertyToID("ssgi_SHBg");
        internal static readonly int shBb = Shader.PropertyToID("ssgi_SHBb");
        internal static readonly int shC = Shader.PropertyToID("ssgi_SHC");

        internal const string _FP_REFL_PROBE_ATLAS = "_FP_REFL_PROBE_ATLAS";
        internal const string _RAYMARCHING_FALLBACK_SKY = "_RAYMARCHING_FALLBACK_SKY";
        internal const string _RAYMARCHING_FALLBACK_REFLECTION_PROBES =
            "_RAYMARCHING_FALLBACK_REFLECTION_PROBES";
        internal const string _BACKFACE_TEXTURES = "_BACKFACE_TEXTURES";
        internal const string _FORWARD_PLUS = "_FORWARD_PLUS";
#if UNITY_6000_1_OR_NEWER
        internal const string _CLUSTER_LIGHT_LOOP = "_CLUSTER_LIGHT_LOOP";
        internal const string _REFLECTION_PROBE_ATLAS = "_REFLECTION_PROBE_ATLAS";
#endif
        internal const string _WRITE_RENDERING_LAYERS = "_WRITE_RENDERING_LAYERS";
        internal const string _USE_RENDERING_LAYERS = "_USE_RENDERING_LAYERS";
        internal const string _DEPTH_NORMALS_UPSCALE = "_DEPTH_NORMALS_UPSCALE";
        internal const string PROBE_VOLUMES_L1 = "PROBE_VOLUMES_L1";
        internal const string PROBE_VOLUMES_L2 = "PROBE_VOLUMES_L2";
        internal const string _APV_LIGHTING_BUFFER = "_APV_LIGHTING_BUFFER";

        internal const string SSGI_RENDER_GBUFFER = "SSGI_RENDER_GBUFFER";
        internal const string SSGI_RENDER_BACKFACE_DEPTH = "SSGI_RENDER_BACKFACE_DEPTH";
        internal const string SSGI_RENDER_BACKFACE_COLOR = "SSGI_RENDER_BACKFACE_COLOR";

        internal const float k_BlurMaxRadius = 0.04f;

        internal static readonly Vector4 m_ScaleBias = new Vector4(1.0f, 1.0f, 0.0f, 0.0f);
    }
}
