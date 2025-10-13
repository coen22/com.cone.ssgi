using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Rendering.Universal;
using Cone.SSGI.Denoisers;
using static Cone.SSGI.ScreenSpaceGlobalIlluminationShaderConstants;
using static Cone.SSGI.ScreenSpaceGlobalIlluminationURP;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

namespace Cone.SSGI
{
    public class ScreenSpaceGlobalIlluminationPass : ScriptableRenderPass
    {
        private const string m_ProfilerTag = "Screen Space Global Illumination";
        private readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler(m_ProfilerTag);

        public ScreenSpaceGlobalIlluminationVolume ssgiVolume;
        public bool enableRenderingLayers;
        public bool overrideAmbientLighting;
        public bool hasProbeAtlas;
        public bool outputAPVLighting;
        public Material m_SSGIMaterial;
        internal SSGISpatialSingleFrameDenoiser singleFrameDenoiser;
        internal SSGIEdgeAwareAtrousDenoiser edgeAwareAtrousDenoiser;
        internal SSGIEdgeAwareAtrousDenoiserFast edgeAwareAtrousDenoiserFast;
        internal SSGIWalrDenoiser walrDenoiser;
        internal SSGIAdaptiveLutDenoiser adaptiveLutDenoiser;
        internal SSGIHybridTemporalDenoiser hybridTemporalDenoiser;
        internal NRDDenoiser nrdDenoiser;
        internal ForwardGBufferPass forwardGBufferPass;
        internal bool usingDeferred;

        internal void ConfigureDenoisers(
            ScreenSpaceGlobalIlluminationURP feature,
            ScreenSpaceGlobalIlluminationVolume volume
        )
        {
            singleFrameDenoiser = null;
            edgeAwareAtrousDenoiser = null;
            edgeAwareAtrousDenoiserFast = null;
            walrDenoiser = null;
            adaptiveLutDenoiser = null;
            hybridTemporalDenoiser = null;
            nrdDenoiser = null;

            if (volume == null || !volume.denoiseSS.value)
                return;

            ISSGIDenoiser active = feature.AcquireDenoiser(volume.denoiserAlgorithmSS.value);
            active?.ConfigurePass(feature, this, volume);

            if (volume.secondDenoiserPassSS.value && singleFrameDenoiser == null)
            {
                var fallback = feature.AcquireDenoiser<SSGISpatialSingleFrameDenoiser>();
                fallback?.ConfigurePass(feature, this, volume);
            }
        }

        private RTHandle m_IntermediateCameraColorHandle;
        private RTHandle m_DiffuseHandle;
        private RTHandle m_IntermediateDiffuseHandle;
        private RTHandle m_AccumulateSampleHandle;
        private RTHandle m_APVLightingHandle;
        private RTHandle m_AtrousPingHandle;
        private RTHandle m_AtrousPongHandle;
        private RTHandle m_AdaptiveTemporalOutputHandle;

        // Render Graph Pass
        // Persistent RTHandles
        private RTHandle m_HistoryDepthHandle;
        private RTHandle m_HistoryCameraColorHandle;
        private RTHandle m_HistoryIndirectDiffuseHandle;
        private RTHandle m_AccumulateHistorySampleHandle;

        private readonly RenderTargetIdentifier[] rTHandles = new RenderTargetIdentifier[2];
        private readonly SSGITemporalDenoiser m_TemporalDenoiser = new SSGITemporalDenoiser();

        private bool isHistoryTextureValid;
        private bool enableDenoise;
        private int frameCount = 0;
        private float resolutionScale = 1.0f;
        private float cameraMotionMagnitude = 0.0f;

        public static readonly float[] k_PreBlurRands = new float[]
        {
            0.840188f,
            0.394383f,
            0.783099f,
            0.79844f,
            0.911647f,
            0.197551f,
            0.335223f,
            0.76823f,
            0.277775f,
            0.55397f,
            0.477397f,
            0.628871f,
            0.364784f,
            0.513401f,
            0.95223f,
            0.916195f,
            0.635712f,
            0.717297f,
            0.141603f,
            0.606969f,
            0.0163006f,
            0.242887f,
            0.137232f,
            0.804177f,
            0.156679f,
            0.400944f,
            0.12979f,
            0.108809f,
            0.998924f,
            0.218257f,
            0.512932f,
            0.839112f,
        };
        public static readonly float[] k_BlurRands = new float[]
        {
            0.61264f,
            0.296032f,
            0.637552f,
            0.524287f,
            0.493583f,
            0.972775f,
            0.292517f,
            0.771358f,
            0.526745f,
            0.769914f,
            0.400229f,
            0.891529f,
            0.283315f,
            0.352458f,
            0.807725f,
            0.919026f,
            0.0697553f,
            0.949327f,
            0.525995f,
            0.0860558f,
            0.192214f,
            0.663227f,
            0.890233f,
            0.348893f,
            0.0641713f,
            0.020023f,
            0.457702f,
            0.0630958f,
            0.23828f,
            0.970634f,
            0.902208f,
            0.85092f,
        };
        public static readonly float[] k_PostBlurRands = new float[]
        {
            0.266666f,
            0.53976f,
            0.375207f,
            0.760249f,
            0.512535f,
            0.667724f,
            0.531606f,
            0.0392803f,
            0.437638f,
            0.931835f,
            0.93081f,
            0.720952f,
            0.284293f,
            0.738534f,
            0.639979f,
            0.354049f,
            0.687861f,
            0.165974f,
            0.440105f,
            0.880075f,
            0.829201f,
            0.330337f,
            0.228968f,
            0.893372f,
            0.35036f,
            0.68667f,
            0.956468f,
            0.58864f,
            0.657304f,
            0.858676f,
            0.43956f,
            0.92397f,
        };

        public ScreenSpaceGlobalIlluminationPass(Material material)
        {
            m_SSGIMaterial = material;
        }

        #region Non Render Graph Pass

        // The index of current camera in the "CameraHistoryData[]"
        private int cameraHistoryIndex;

#if UNITY_6000_0_OR_NEWER
        [Obsolete]
#endif
        public override void Execute(
            ScriptableRenderContext context,
            ref RenderingData renderingData
        )
        {
            RTHandle colorHandle = renderingData.cameraData.renderer.cameraColorTargetHandle;

            // Get the persistent textures for the current camera
            var m_HistoryDepthHandle = cameraHistoryData[cameraHistoryIndex].historyDepthHandle;
            var m_HistoryCameraColorHandle = cameraHistoryData[
                cameraHistoryIndex
            ].historyCameraColorHandle;
            var m_HistoryIndirectDiffuseHandle = cameraHistoryData[
                cameraHistoryIndex
            ].historyIndirectDiffuseHandle;
            var m_AccumulateHistorySampleHandle = cameraHistoryData[
                cameraHistoryIndex
            ].accumulateHistorySampleHandle;

            CommandBuffer cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                bool useBlueNoiseSampling = ssgiVolume.blueNoiseSampling.value;
                m_SSGIMaterial.SetFloat(_SSGIUseBlueNoise, useBlueNoiseSampling ? 1.0f : 0.0f);
                ApplyBlueNoiseToMaterial(m_SSGIMaterial, useBlueNoiseSampling);

                var denoiserMode = ssgiVolume.denoiserAlgorithmSS.value;
                bool useHybridDenoiser = false;
                bool hybridLowMotion = false;
                bool useNRDDenoiser = false;
                bool useSpatialDenoiser =
                    enableDenoise
                    && denoiserMode
                        == ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.SingleFrame
                    && singleFrameDenoiser != null
                    && singleFrameDenoiser.IsSupported;

                bool useAtrousDenoiser =
                    enableDenoise
                    && denoiserMode
                        == ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.EdgeAwareAtrous
                    && edgeAwareAtrousDenoiser != null
                    && edgeAwareAtrousDenoiser.IsSupported;

                bool useWalrDenoiser =
                    enableDenoise
                    && denoiserMode
                        == ScreenSpaceGlobalIlluminationVolume
                            .DenoiserAlgorithm
                            .WeightedAtrousLinearRegression
                    && walrDenoiser != null
                    && walrDenoiser.IsSupported;

                bool useAdaptiveLut =
                    enableDenoise
                    && denoiserMode
                        == ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.EdgeAdaptiveLut
                    && adaptiveLutDenoiser != null
                    && adaptiveLutDenoiser.IsSupported;

                useHybridDenoiser =
                    enableDenoise
                    && denoiserMode
                        == ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.HybridTemporal
                    && hybridTemporalDenoiser != null
                    && hybridTemporalDenoiser.IsSupported
                    && singleFrameDenoiser != null
                    && singleFrameDenoiser.IsSupported;

                if (useHybridDenoiser)
                {
                    float motionThreshold = hybridTemporalDenoiser
                        .CreateSettings(ssgiVolume)
                        .MotionThreshold;
                    hybridLowMotion = motionThreshold <= 0.0f
                        || cameraMotionMagnitude <= motionThreshold;
                }

                useNRDDenoiser =
                    enableDenoise
                    && denoiserMode
                        == ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.NRD
                    && nrdDenoiser != null
                    && nrdDenoiser.IsSupported;

                // Copy Direct Lighting
                if (overrideAmbientLighting)
                {
                    if (outputAPVLighting)
                    {
                        rTHandles[0] = m_IntermediateCameraColorHandle;
                        rTHandles[1] = m_APVLightingHandle;
                        // RT-1: direct lighting
                        // RT-2: indirect lighting (APV)
                        cmd.SetRenderTarget(rTHandles, m_IntermediateCameraColorHandle);
                        Blitter.BlitTexture(cmd, colorHandle, m_ScaleBias, m_SSGIMaterial, pass: 0);
                        m_SSGIMaterial.SetTexture(apvLightingTexture, m_APVLightingHandle);
                    }
                    else
                        Blitter.BlitCameraTexture(
                            cmd,
                            colorHandle,
                            m_IntermediateCameraColorHandle,
                            m_SSGIMaterial,
                            pass: 0
                        );
                }
                else
                    Blitter.BlitCameraTexture(cmd, colorHandle, m_IntermediateCameraColorHandle);

                if (enableDenoise)
                {
                    // Render SSGI
                    Blitter.BlitCameraTexture(
                        cmd,
                        m_IntermediateCameraColorHandle,
                        m_IntermediateDiffuseHandle,
                        RenderBufferLoadAction.Load,
                        RenderBufferStoreAction.Store,
                        m_SSGIMaterial,
                        pass: 1
                    );
                    m_SSGIMaterial.SetTexture(indirectDiffuseTexture, m_DiffuseHandle);

                    bool aggressiveTemporal =
                        denoiserMode
                        == ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.Aggressive;

                    switch (denoiserMode)
                    {
                        case ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.EdgeAwareAtrous:
                        {
                            if (!useAtrousDenoiser)
                                goto default;

                            RunEdgeAwareAtrous(cmd, ref renderingData);

                            cmd.SetRenderTarget(
                                m_AccumulateSampleHandle,
                                RenderBufferLoadAction.DontCare,
                                RenderBufferStoreAction.Store,
                                m_AccumulateSampleHandle,
                                RenderBufferLoadAction.DontCare,
                                RenderBufferStoreAction.DontCare
                            );
                            CoreUtils.ClearRenderTarget(cmd, ClearFlag.Color, Color.black);
                            break;
                        }
                        case ScreenSpaceGlobalIlluminationVolume
                            .DenoiserAlgorithm
                            .WeightedAtrousLinearRegression:
                        {
                            if (!useWalrDenoiser)
                                goto default;

                            RunWalrDenoiser(cmd, ref renderingData);

                            cmd.SetRenderTarget(
                                m_AccumulateSampleHandle,
                                RenderBufferLoadAction.DontCare,
                                RenderBufferStoreAction.Store
                            );
                            CoreUtils.ClearRenderTarget(cmd, ClearFlag.Color, Color.black);
                            break;
                        }
                        case ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.EdgeAdaptiveLut:
                        {
                            if (!useAdaptiveLut)
                                goto default;

                            RunAdaptiveLutDenoiser(cmd, ref renderingData);

                            cmd.SetRenderTarget(
                                m_AccumulateSampleHandle,
                                RenderBufferLoadAction.DontCare,
                                RenderBufferStoreAction.Store
                            );
                            CoreUtils.ClearRenderTarget(cmd, ClearFlag.Color, Color.black);
                            break;
                        }
                        case ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.NRD:
                        {
                            if (!useNRDDenoiser)
                                goto default;

                            RunNRDDenoiser(
                                cmd,
                                ref renderingData,
                                nrdDenoiser.CreateSettings(ssgiVolume)
                            );

                            cmd.SetRenderTarget(
                                m_AccumulateSampleHandle,
                                RenderBufferLoadAction.DontCare,
                                RenderBufferStoreAction.Store
                            );
                            CoreUtils.ClearRenderTarget(cmd, ClearFlag.Color, Color.black);
                            break;
                        }
                        case ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.HybridTemporal:
                        {
                            if (!useHybridDenoiser)
                                goto default;

                            if (hybridLowMotion)
                            {
                                m_TemporalDenoiser.Dispatch(
                                    cmd,
                                    m_SSGIMaterial,
                                    m_ScaleBias,
                                    m_IntermediateDiffuseHandle,
                                    m_DiffuseHandle,
                                    m_AccumulateSampleHandle,
                                    rTHandles,
                                    aggressiveDenoise: false,
                                    ssgiVolume.secondDenoiserPassSS.value
                                );
                            }
                            else
                            {
                                RunSpatialDenoiser(cmd, ref renderingData);

                                cmd.SetRenderTarget(
                                    m_AccumulateSampleHandle,
                                    RenderBufferLoadAction.DontCare,
                                    RenderBufferStoreAction.Store
                                );
                                CoreUtils.ClearRenderTarget(cmd, ClearFlag.Color, Color.black);
                            }

                            break;
                        }
                        case ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.SingleFrame:
                        {
                            if (!useSpatialDenoiser)
                                goto default;

                            RunSpatialDenoiser(cmd, ref renderingData);

                            // Reset accumulation buffers so that switching back to temporal filtering starts cleanly.
                            cmd.SetRenderTarget(
                                m_AccumulateSampleHandle,
                                RenderBufferLoadAction.DontCare,
                                RenderBufferStoreAction.Store
                            );
                            CoreUtils.ClearRenderTarget(cmd, ClearFlag.Color, Color.black);
                            break;
                        }
                        case ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.Conservative:
                        case ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.Aggressive:
                        default:
                        {
                            m_TemporalDenoiser.Dispatch(
                                cmd,
                                m_SSGIMaterial,
                                m_ScaleBias,
                                m_IntermediateDiffuseHandle,
                                m_DiffuseHandle,
                                m_AccumulateSampleHandle,
                                rTHandles,
                                aggressiveTemporal,
                                ssgiVolume.secondDenoiserPassSS.value
                            );
                            break;
                        }
                    }

                    // Update History Color
                    cmd.CopyTexture(m_DiffuseHandle, m_HistoryIndirectDiffuseHandle);

                    // Update History Depth
                    Blitter.BlitCameraTexture(
                        cmd,
                        m_HistoryDepthHandle,
                        m_HistoryDepthHandle,
                        RenderBufferLoadAction.DontCare,
                        RenderBufferStoreAction.Store,
                        m_SSGIMaterial,
                        pass: 5
                    );

                    cmd.CopyTexture(m_AccumulateSampleHandle, m_AccumulateHistorySampleHandle);
                }
                else
                {
                    // SSGI
                    Blitter.BlitCameraTexture(
                        cmd,
                        m_IntermediateCameraColorHandle,
                        m_DiffuseHandle,
                        RenderBufferLoadAction.Load,
                        RenderBufferStoreAction.Store,
                        m_SSGIMaterial,
                        pass: 1
                    );
                    m_SSGIMaterial.SetTexture(indirectDiffuseTexture, m_DiffuseHandle);

                    // Update History Depth
                    Blitter.BlitCameraTexture(
                        cmd,
                        m_HistoryDepthHandle,
                        m_HistoryDepthHandle,
                        RenderBufferLoadAction.DontCare,
                        RenderBufferStoreAction.Store,
                        m_SSGIMaterial,
                        pass: 5
                    );
                }

                // Combine
                Blitter.BlitCameraTexture(
                    cmd,
                    m_IntermediateCameraColorHandle,
                    colorHandle,
                    m_SSGIMaterial,
                    pass: 6
                );

                // Copy History Scene Color
                Blitter.BlitCameraTexture(
                    cmd,
                    colorHandle,
                    m_HistoryCameraColorHandle,
                    RenderBufferLoadAction.DontCare,
                    RenderBufferStoreAction.Store,
                    m_SSGIMaterial,
                    pass: 9
                );
            }
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            CommandBufferPool.Release(cmd);
        }

        private RenderTargetIdentifier GetNormalTextureRT()
        {
            if (
                !usingDeferred
                && forwardGBufferPass != null
                && forwardGBufferPass.m_GBuffer2 != null
            )
            {
                if (forwardGBufferPass.m_GBuffer2.rt != null)
                    return new RenderTargetIdentifier(forwardGBufferPass.m_GBuffer2.rt);

                return forwardGBufferPass.m_GBuffer2.nameID;
            }

            return new RenderTargetIdentifier(BuiltinRenderTextureType.GBuffer2);
        }

        private RenderTargetIdentifier GetAlbedoTextureRT()
        {
            if (
                !usingDeferred
                && forwardGBufferPass != null
                && forwardGBufferPass.m_GBuffer0 != null
            )
            {
                if (forwardGBufferPass.m_GBuffer0.rt != null)
                    return new RenderTargetIdentifier(forwardGBufferPass.m_GBuffer0.rt);

                return forwardGBufferPass.m_GBuffer0.nameID;
            }

            return new RenderTargetIdentifier(BuiltinRenderTextureType.GBuffer0);
        }

        private RenderTargetIdentifier GetMotionVectorRT(ref RenderingData renderingData)
        {
#if UNITY_6000_0_OR_NEWER
            if (
                renderingData.cameraData.renderer is UniversalRenderer universalRenderer
                && cameraMotionVectorHandleFieldInfo != null
            )
            {
                if (
                    cameraMotionVectorHandleFieldInfo.GetValue(universalRenderer)
                        is RTHandle motionHandle
                    && motionHandle != null
                )
                {
                    return ToRTIdentifier(motionHandle);
                }
            }
#endif

            if (motionVectorPassFieldInfo != null)
            {
                var motionPass = motionVectorPassFieldInfo.GetValue(renderingData.cameraData.renderer);
                if (motionPass != null)
                {
                    const string colorFieldName = "m_Color";
                    var colorField = motionPass
                        .GetType()
                        .GetField(colorFieldName, BindingFlags.NonPublic | BindingFlags.Instance);
                    if (colorField?.GetValue(motionPass) is RTHandle colorHandle)
                    {
                        if (colorHandle.rt != null)
                            return new RenderTargetIdentifier(colorHandle.rt);
                        return colorHandle.nameID;
                    }
                }
            }

            return new RenderTargetIdentifier(BuiltinRenderTextureType.MotionVectors);
        }

        private static RenderTargetIdentifier ToRTIdentifier(RTHandle handle)
        {
            if (handle == null)
                return new RenderTargetIdentifier(BuiltinRenderTextureType.None);

            if (handle.rt != null)
                return new RenderTargetIdentifier(handle.rt);

            return handle.nameID;
        }

        private void RunSpatialDenoiser(CommandBuffer cmd, ref RenderingData renderingData)
        {
            if (
                singleFrameDenoiser == null
                || m_IntermediateDiffuseHandle == null
                || m_DiffuseHandle == null
            )
            {
                cmd.CopyTexture(m_IntermediateDiffuseHandle, m_DiffuseHandle);
                return;
            }

            var settings = singleFrameDenoiser.CreateSettings(ssgiVolume);

            var depthHandle = renderingData.cameraData.renderer.cameraDepthTargetHandle;
            RenderTargetIdentifier depthRT =
                depthHandle != null
                    ? (
                        depthHandle.rt != null
                            ? new RenderTargetIdentifier(depthHandle.rt)
                            : depthHandle.nameID
                    )
                    : new RenderTargetIdentifier(BuiltinRenderTextureType.Depth);

            RenderTargetIdentifier normalRT = GetNormalTextureRT();
            bool hasAlbedo =
                usingDeferred
                || (
                    forwardGBufferPass != null
                    && forwardGBufferPass.m_GBuffer0 != null
                    && forwardGBufferPass.m_GBuffer0.rt != null
                );
            RenderTargetIdentifier albedoRT = hasAlbedo
                ? GetAlbedoTextureRT()
                : new RenderTargetIdentifier(Texture2D.blackTexture);

            if (
                !singleFrameDenoiser.Dispatch(
                    cmd,
                    ref renderingData,
                    settings,
                    m_IntermediateDiffuseHandle,
                    m_DiffuseHandle,
                    depthRT,
                    normalRT,
                    albedoRT
                )
            )
                cmd.CopyTexture(m_IntermediateDiffuseHandle, m_DiffuseHandle);
        }

        private void RunAdaptiveLutDenoiser(CommandBuffer cmd, ref RenderingData renderingData)
        {
            if (
                adaptiveLutDenoiser == null
                || m_IntermediateDiffuseHandle == null
                || m_DiffuseHandle == null
            )
            {
                cmd.CopyTexture(m_IntermediateDiffuseHandle, m_DiffuseHandle);
                return;
            }

            if (m_IntermediateDiffuseHandle.rt == null || m_DiffuseHandle.rt == null)
            {
                cmd.CopyTexture(m_IntermediateDiffuseHandle, m_DiffuseHandle);
                return;
            }

            int width = m_DiffuseHandle.rt.width;
            int height = m_DiffuseHandle.rt.height;
            if (width == 0 || height == 0)
            {
                cmd.CopyTexture(m_IntermediateDiffuseHandle, m_DiffuseHandle);
                return;
            }

            var settings = adaptiveLutDenoiser.CreateSettings(ssgiVolume);

            var depthHandle = renderingData.cameraData.renderer.cameraDepthTargetHandle;
            RenderTargetIdentifier depthRT =
                depthHandle != null ? ToRTIdentifier(depthHandle) : new RenderTargetIdentifier(BuiltinRenderTextureType.Depth);
            RenderTargetIdentifier normalRT = GetNormalTextureRT();
            RenderTargetIdentifier motionRT = GetMotionVectorRT(ref renderingData);
            RenderTargetIdentifier historyDepthRT = ToRTIdentifier(m_HistoryDepthHandle);

            ref var fastHistoryHandle = ref cameraHistoryData[cameraHistoryIndex].adaptiveFastHistoryHandle;
            ref var mainHistoryHandle = ref cameraHistoryData[cameraHistoryIndex].adaptiveMainHistoryHandle;
            ref var momentsHandle = ref cameraHistoryData[cameraHistoryIndex].adaptiveMomentsHandle;

            bool temporalSupported = settings.UseTemporal && adaptiveLutDenoiser.SupportsTemporal;

            if (temporalSupported)
            {
                RenderTextureDescriptor temporalDesc = new RenderTextureDescriptor(
                    width,
                    height,
                    GraphicsFormat.R16G16B16A16_SFloat,
                    0
                )
                {
                    depthStencilFormat = GraphicsFormat.None,
                    stencilFormat = GraphicsFormat.None,
                    msaaSamples = 1,
                    bindMS = false,
                    sRGB = false,
                    useMipMap = false,
                    autoGenerateMips = false,
                    enableRandomWrite = true,
                    volumeDepth = 1,
                    mipCount = 1
                };

#if UNITY_6000_0_OR_NEWER
                RenderingUtils.ReAllocateHandleIfNeeded(
                    ref fastHistoryHandle,
                    temporalDesc,
                    FilterMode.Point,
                    TextureWrapMode.Clamp,
                    name: "_SSGIAdaptiveHistFast"
                );
                RenderingUtils.ReAllocateHandleIfNeeded(
                    ref mainHistoryHandle,
                    temporalDesc,
                    FilterMode.Point,
                    TextureWrapMode.Clamp,
                    name: "_SSGIAdaptiveHistMain"
                );
#else
                RenderingUtils.ReAllocateIfNeeded(
                    ref fastHistoryHandle,
                    temporalDesc,
                    FilterMode.Point,
                    TextureWrapMode.Clamp,
                    name: "_SSGIAdaptiveHistFast"
                );
                RenderingUtils.ReAllocateIfNeeded(
                    ref mainHistoryHandle,
                    temporalDesc,
                    FilterMode.Point,
                    TextureWrapMode.Clamp,
                    name: "_SSGIAdaptiveHistMain"
                );
#endif

                RenderTextureDescriptor momentDesc = temporalDesc;
                momentDesc.graphicsFormat = GraphicsFormat.R16G16_SFloat;

#if UNITY_6000_0_OR_NEWER
                RenderingUtils.ReAllocateHandleIfNeeded(
                    ref momentsHandle,
                    momentDesc,
                    FilterMode.Point,
                    TextureWrapMode.Clamp,
                    name: "_SSGIAdaptiveMoments"
                );
                RenderingUtils.ReAllocateHandleIfNeeded(
                    ref m_AdaptiveTemporalOutputHandle,
                    temporalDesc,
                    FilterMode.Point,
                    TextureWrapMode.Clamp,
                    name: "_SSGIAdaptiveTemporal"
                );
#else
                RenderingUtils.ReAllocateIfNeeded(
                    ref momentsHandle,
                    momentDesc,
                    FilterMode.Point,
                    TextureWrapMode.Clamp,
                    name: "_SSGIAdaptiveMoments"
                );
                RenderingUtils.ReAllocateIfNeeded(
                    ref m_AdaptiveTemporalOutputHandle,
                    temporalDesc,
                    FilterMode.Point,
                    TextureWrapMode.Clamp,
                    name: "_SSGIAdaptiveTemporal"
                );
#endif
            }
            else
            {
                settings.UseTemporal = false;
            }

            var resources = new SSGIAdaptiveLutDenoiser.ResourceSet
            {
                Width = width,
                Height = height,
                Source = m_IntermediateDiffuseHandle,
                Destination = m_DiffuseHandle,
                Depth = depthRT,
                Normal = normalRT,
                Motion = motionRT,
                HistoryDepth = historyDepthRT,
                FastHistory = ToRTIdentifier(fastHistoryHandle),
                MainHistory = ToRTIdentifier(mainHistoryHandle),
                Moments = ToRTIdentifier(momentsHandle),
                TemporalOutput = ToRTIdentifier(m_AdaptiveTemporalOutputHandle),
                HasTemporal = temporalSupported
            };

            RenderTargetIdentifier noneRT = new RenderTargetIdentifier(BuiltinRenderTextureType.None);
            if (
                resources.HistoryDepth == noneRT
                || resources.FastHistory == noneRT
                || resources.MainHistory == noneRT
                || resources.Moments == noneRT
                || resources.TemporalOutput == noneRT
            )
            {
                resources.HasTemporal = false;
                settings.UseTemporal = false;
            }

            if (!resources.HasTemporal)
                settings.UseTemporal = false;

            Vector4 zParams = Shader.GetGlobalVector(_ZBufferParams);

            if (!adaptiveLutDenoiser.Dispatch(cmd, zParams, settings, in resources))
            {
                cmd.CopyTexture(m_IntermediateDiffuseHandle, m_DiffuseHandle);
            }
        }

        private void RunNRDDenoiser(
            CommandBuffer cmd,
            ref RenderingData renderingData,
            NRDDenoiser.Settings settings
        )
        {
            if (
                nrdDenoiser == null
                || m_IntermediateDiffuseHandle == null
                || m_DiffuseHandle == null
                || m_IntermediateDiffuseHandle.rt == null
                || m_DiffuseHandle.rt == null
            )
            {
                cmd.CopyTexture(m_IntermediateDiffuseHandle, m_DiffuseHandle);
                return;
            }

            int width = m_DiffuseHandle.rt.width;
            int height = m_DiffuseHandle.rt.height;
            if (width == 0 || height == 0)
            {
                cmd.CopyTexture(m_IntermediateDiffuseHandle, m_DiffuseHandle);
                return;
            }
            // Temporal accumulation using the built-in temporal pass with NRD-specific weighting.
            float temporalIntensity = settings.MaxAccumulatedFrames <= 1
                ? 0.0f
                : Mathf.Clamp01(1.0f - (1.0f / settings.MaxAccumulatedFrames));

            float originalTemporal = m_SSGIMaterial.GetFloat(_TemporalIntensity);
            m_SSGIMaterial.SetFloat(_TemporalIntensity, temporalIntensity);

            m_TemporalDenoiser.Dispatch(
                cmd,
                m_SSGIMaterial,
                m_ScaleBias,
                m_IntermediateDiffuseHandle,
                m_DiffuseHandle,
                m_AccumulateSampleHandle,
                rTHandles,
                aggressiveDenoise: false,
                ssgiVolume.secondDenoiserPassSS.value
            );

            m_SSGIMaterial.SetFloat(_TemporalIntensity, originalTemporal);

            // Spatial filter leveraging the single-frame denoiser but overriding radius/thresholds.
            var spatialSettings = singleFrameDenoiser.CreateSettings(ssgiVolume);
            spatialSettings.Radius = Mathf.Max(1.0f, settings.SpatialRadius);
            spatialSettings.SigmaColor = Mathf.Max(1e-4f, spatialSettings.SigmaColor / Mathf.Max(0.001f, settings.SigmaMultiplier));

            var depthHandle = renderingData.cameraData.renderer.cameraDepthTargetHandle;
            RenderTargetIdentifier depthRT = depthHandle != null
                ? ToRTIdentifier(depthHandle)
                : new RenderTargetIdentifier(BuiltinRenderTextureType.Depth);

            RenderTargetIdentifier normalRT = GetNormalTextureRT();
            bool hasAlbedo =
                usingDeferred
                || (
                    forwardGBufferPass != null
                    && forwardGBufferPass.m_GBuffer0 != null
                    && forwardGBufferPass.m_GBuffer0.rt != null
                );
            RenderTargetIdentifier albedoRT = hasAlbedo
                ? GetAlbedoTextureRT()
                : new RenderTargetIdentifier(Texture2D.blackTexture);

            cmd.CopyTexture(m_DiffuseHandle, m_IntermediateDiffuseHandle);

            if (
                !singleFrameDenoiser.Dispatch(
                    cmd,
                    ref renderingData,
                    spatialSettings,
                    m_IntermediateDiffuseHandle,
                    m_DiffuseHandle,
                    depthRT,
                    normalRT,
                    albedoRT
                )
            )
            {
                cmd.CopyTexture(m_IntermediateDiffuseHandle, m_DiffuseHandle);
            }
        }

        private void RunEdgeAwareAtrous(CommandBuffer cmd, ref RenderingData renderingData)
        {
            if (m_IntermediateDiffuseHandle == null || m_DiffuseHandle == null)
            {
                cmd.CopyTexture(m_IntermediateDiffuseHandle, m_DiffuseHandle);
                return;
            }

            var depthHandle = renderingData.cameraData.renderer.cameraDepthTargetHandle;
            RenderTargetIdentifier depthRT =
                depthHandle != null
                    ? (
                        depthHandle.rt != null
                            ? new RenderTargetIdentifier(depthHandle.rt)
                            : depthHandle.nameID
                    )
                    : new RenderTargetIdentifier(BuiltinRenderTextureType.Depth);

            RenderTargetIdentifier normalRT = GetNormalTextureRT();
            RenderTargetIdentifier albedoRT = GetAlbedoTextureRT();
            bool hasAlbedo =
                usingDeferred
                || (
                    forwardGBufferPass != null
                    && forwardGBufferPass.m_GBuffer0 != null
                    && forwardGBufferPass.m_GBuffer0.rt != null
                );
            RenderTargetIdentifier fallbackAlbedo = new RenderTargetIdentifier(
                Texture2D.blackTexture
            );

            bool useFastSchedule =
                ssgiVolume.fastAtrousSchedule.value
                && edgeAwareAtrousDenoiserFast != null
                && edgeAwareAtrousDenoiserFast.IsSupported;
            bool executed;

            if (useFastSchedule)
            {
                var fastSettings = edgeAwareAtrousDenoiserFast.CreateSettings(ssgiVolume);

                executed = edgeAwareAtrousDenoiserFast.Dispatch(
                    cmd,
                    ref renderingData,
                    fastSettings,
                    m_IntermediateDiffuseHandle,
                    m_DiffuseHandle,
                    m_AtrousPingHandle,
                    m_AtrousPongHandle,
                    depthRT,
                    normalRT,
                    albedoRT,
                    fallbackAlbedo,
                    hasAlbedo
                );
            }
            else
            {
                if (edgeAwareAtrousDenoiser == null || !edgeAwareAtrousDenoiser.IsSupported)
                {
                    cmd.CopyTexture(m_IntermediateDiffuseHandle, m_DiffuseHandle);
                    return;
                }

                var legacySettings = edgeAwareAtrousDenoiser.CreateSettings(ssgiVolume);

                executed = edgeAwareAtrousDenoiser.Dispatch(
                    cmd,
                    ref renderingData,
                    legacySettings,
                    m_IntermediateDiffuseHandle,
                    m_DiffuseHandle,
                    m_AtrousPingHandle,
                    m_AtrousPongHandle,
                    depthRT,
                    normalRT,
                    albedoRT,
                    fallbackAlbedo,
                    hasAlbedo
                );
            }

            if (!executed)
            {
                cmd.CopyTexture(m_IntermediateDiffuseHandle, m_DiffuseHandle);
            }
        }

        private void RunWalrDenoiser(CommandBuffer cmd, ref RenderingData renderingData)
        {
            if (
                walrDenoiser == null
                || m_IntermediateDiffuseHandle == null
                || m_DiffuseHandle == null
            )
            {
                cmd.CopyTexture(m_IntermediateDiffuseHandle, m_DiffuseHandle);
                return;
            }

            var settings = walrDenoiser.CreateSettings(ssgiVolume);

            var depthHandle = renderingData.cameraData.renderer.cameraDepthTargetHandle;
            RenderTargetIdentifier depthRT =
                depthHandle != null
                    ? (
                        depthHandle.rt != null
                            ? new RenderTargetIdentifier(depthHandle.rt)
                            : depthHandle.nameID
                    )
                    : new RenderTargetIdentifier(BuiltinRenderTextureType.Depth);

            RenderTargetIdentifier normalRT = GetNormalTextureRT();
            bool hasAlbedo =
                usingDeferred
                || (
                    forwardGBufferPass != null
                    && forwardGBufferPass.m_GBuffer0 != null
                    && forwardGBufferPass.m_GBuffer0.rt != null
                );
            RenderTargetIdentifier albedoRT = hasAlbedo
                ? GetAlbedoTextureRT()
                : new RenderTargetIdentifier(Texture2D.blackTexture);
            RenderTargetIdentifier fallbackAlbedo = new RenderTargetIdentifier(
                Texture2D.blackTexture
            );

            if (
                !walrDenoiser.Dispatch(
                    cmd,
                    ref renderingData,
                    settings,
                    m_IntermediateDiffuseHandle,
                    m_DiffuseHandle,
                    depthRT,
                    normalRT,
                    albedoRT,
                    fallbackAlbedo,
                    hasAlbedo
                )
            )
            {
                cmd.CopyTexture(m_IntermediateDiffuseHandle, m_DiffuseHandle);
            }
        }

#if UNITY_6000_0_OR_NEWER
        [Obsolete]
#endif
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var visibleReflectionProbes = renderingData.cullResults.visibleReflectionProbes;
            var camera = renderingData.cameraData.camera;
            bool isReflectionCamera = camera.cameraType == CameraType.Reflection;

            int currentCameraHash = camera.GetHashCode();
            cameraHistoryIndex = GetCameraHistoryDataIndex(currentCameraHash);

            if (!hasProbeAtlas)
                UpdateReflectionProbe(visibleReflectionProbes, camera.transform.position);
            else
                m_SSGIMaterial.SetFloat(probeSet, 0.0f);

            m_SSGIMaterial.SetFloat(frameIndex, frameCount);
            m_SSGIMaterial.SetVector(
                _ReBlurBlurRotator,
                EvaluateRotator(k_BlurRands[frameCount % 32])
            );
            frameCount += 33;
            frameCount %= 64000;

            int width = (int)(camera.scaledPixelWidth * renderingData.cameraData.renderScale);
            int height = (int)(camera.scaledPixelHeight * renderingData.cameraData.renderScale);

            bool denoiseStateChanged = ssgiVolume.denoiseSS.value != enableDenoise;
            bool resolutionStateChanged = ssgiVolume.fullResolutionSS.value
                ? resolutionScale != 1.0f
                : ssgiVolume.resolutionScaleSS.value != resolutionScale;
            bool cameraHasChanged = cameraHistoryIndex == -1;

            // Reorder the history data array when camera is new
            UpdateCameraHistoryData(cameraHasChanged);
            // Assign the data to index 0 for the new camera
            cameraHistoryIndex = cameraHasChanged ? 0 : cameraHistoryIndex;

            ref var m_HistoryDepthHandle = ref cameraHistoryData[
                cameraHistoryIndex
            ].historyDepthHandle;
            ref var m_HistoryCameraColorHandle = ref cameraHistoryData[
                cameraHistoryIndex
            ].historyCameraColorHandle;
            ref var m_HistoryIndirectDiffuseHandle = ref cameraHistoryData[
                cameraHistoryIndex
            ].historyIndirectDiffuseHandle;
            ref var m_AccumulateHistorySampleHandle = ref cameraHistoryData[
                cameraHistoryIndex
            ].accumulateHistorySampleHandle;

            ref var prevCamInvVPMatrix = ref cameraHistoryData[
                cameraHistoryIndex
            ].prevCamInvVPMatrix;
            ref var prevCameraPositionWS = ref cameraHistoryData[
                cameraHistoryIndex
            ].prevCameraPositionWS;
            ref var historyCameraHash = ref cameraHistoryData[cameraHistoryIndex].hash;

            Vector3 currentCameraPosition = camera.transform.position;
            cameraMotionMagnitude = cameraHasChanged
                ? float.MaxValue
                : Vector3.Distance(prevCameraPositionWS, currentCameraPosition);

            if (prevCamInvVPMatrix != null)
                m_SSGIMaterial.SetMatrix(_PrevInvViewProjMatrix, prevCamInvVPMatrix);
            else
                m_SSGIMaterial.SetMatrix(
                    _PrevInvViewProjMatrix,
                    camera.previousViewProjectionMatrix.inverse
                );

            if (prevCameraPositionWS != null)
                m_SSGIMaterial.SetVector(_PrevCameraPositionWS, prevCameraPositionWS);
            else
                m_SSGIMaterial.SetVector(_PrevCameraPositionWS, currentCameraPosition);

            prevCamInvVPMatrix = (
                GL.GetGPUProjectionMatrix(camera.projectionMatrix, true)
                * renderingData.cameraData.GetViewMatrix()
            ).inverse;
            prevCameraPositionWS = currentCameraPosition;
            historyCameraHash = currentCameraHash;

            // The spread angle is used to compute the world space pixel footprint during denoising.
            // We use low FOV for orthographic cameras as a temporary solution.
            float fieldOfView = camera.orthographic ? 1.0f : camera.fieldOfView;
            m_SSGIMaterial.SetFloat(
                _PixelSpreadAngleTangent,
                Mathf.Tan(fieldOfView * Mathf.Deg2Rad * 0.5f)
                    * 2.0f
                    / Mathf.Min(
                        Mathf.FloorToInt(camera.scaledPixelWidth * resolutionScale),
                        Mathf.FloorToInt(camera.scaledPixelHeight * resolutionScale)
                    )
            );

            ref float historyCameraScaledWidth = ref cameraHistoryData[
                cameraHistoryIndex
            ].scaledWidth;
            ref float historyCameraScaledHeight = ref cameraHistoryData[
                cameraHistoryIndex
            ].scaledHeight;

            resolutionStateChanged |=
                (historyCameraScaledWidth != width) || (historyCameraScaledHeight != height);
            if (!cameraHasChanged && (denoiseStateChanged || resolutionStateChanged))
                isHistoryTextureValid = false;

            historyCameraScaledWidth = width;
            historyCameraScaledHeight = height;

            resolutionScale = ssgiVolume.fullResolutionSS.value
                ? 1.0f
                : ssgiVolume.resolutionScaleSS.value;
            m_SSGIMaterial.SetFloat(downSample, resolutionScale);

            enableDenoise = ssgiVolume.denoiseSS.value;
            var denoiserMode = ssgiVolume.denoiserAlgorithmSS.value;
            bool useHybridDenoiser = false;
            bool hybridLowMotion = false;
            bool useNRDDenoiser = false;

            bool useSpatialDenoiser =
                enableDenoise
                && denoiserMode
                    == ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.SingleFrame
                && singleFrameDenoiser != null
                && singleFrameDenoiser.IsSupported;

            bool useAtrousDenoiser =
                enableDenoise
                && denoiserMode
                    == ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.EdgeAwareAtrous
                && edgeAwareAtrousDenoiser != null
                && edgeAwareAtrousDenoiser.IsSupported;

            bool useWalrDenoiser =
                enableDenoise
                && denoiserMode
                    == ScreenSpaceGlobalIlluminationVolume
                        .DenoiserAlgorithm
                        .WeightedAtrousLinearRegression
                && walrDenoiser != null
                && walrDenoiser.IsSupported;

            bool useAdaptiveLutDenoiser =
                enableDenoise
                && denoiserMode
                    == ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.EdgeAdaptiveLut
                && adaptiveLutDenoiser != null
                && adaptiveLutDenoiser.IsSupported;

            bool adaptiveNeedsMotion =
                useAdaptiveLutDenoiser
                && ssgiVolume.adaptiveUseTemporal.value
                && adaptiveLutDenoiser.SupportsTemporal;

            useHybridDenoiser =
                enableDenoise
                && denoiserMode
                    == ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.HybridTemporal
                && hybridTemporalDenoiser != null
                && hybridTemporalDenoiser.IsSupported
                && singleFrameDenoiser != null
                && singleFrameDenoiser.IsSupported;

            SSGIHybridTemporalDenoiser.Settings hybridSettings = default;
            if (useHybridDenoiser)
            {
                hybridSettings = hybridTemporalDenoiser.CreateSettings(ssgiVolume);
                hybridLowMotion =
                    hybridSettings.MotionThreshold <= 0.0f
                    || cameraMotionMagnitude <= hybridSettings.MotionThreshold;
            }

            useNRDDenoiser =
                enableDenoise
                && denoiserMode
                    == ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.NRD
                && nrdDenoiser != null
                && nrdDenoiser.IsSupported;

            bool useAnySpatial =
                useSpatialDenoiser
                || useAtrousDenoiser
                || useWalrDenoiser
                || useAdaptiveLutDenoiser
                || useNRDDenoiser
                || (useHybridDenoiser && !hybridLowMotion);

            m_SSGIMaterial.SetFloat(_UseMotionVectorsID, 1.0f);

            if (overrideAmbientLighting)
            {
                SphericalHarmonicsL2 ambientProbe = RenderSettings.ambientProbe;

                m_SSGIMaterial.SetVector(
                    shAr,
                    new Vector4(
                        ambientProbe[0, 3],
                        ambientProbe[0, 1],
                        ambientProbe[0, 2],
                        ambientProbe[0, 0] - ambientProbe[0, 6]
                    )
                );
                m_SSGIMaterial.SetVector(
                    shAg,
                    new Vector4(
                        ambientProbe[1, 3],
                        ambientProbe[1, 1],
                        ambientProbe[1, 2],
                        ambientProbe[1, 0] - ambientProbe[1, 6]
                    )
                );
                m_SSGIMaterial.SetVector(
                    shAb,
                    new Vector4(
                        ambientProbe[2, 3],
                        ambientProbe[2, 1],
                        ambientProbe[2, 2],
                        ambientProbe[2, 0] - ambientProbe[2, 6]
                    )
                );
                m_SSGIMaterial.SetVector(
                    shBr,
                    new Vector4(
                        ambientProbe[0, 4],
                        ambientProbe[0, 5],
                        ambientProbe[0, 6] * 3,
                        ambientProbe[0, 7]
                    )
                );
                m_SSGIMaterial.SetVector(
                    shBg,
                    new Vector4(
                        ambientProbe[1, 4],
                        ambientProbe[1, 5],
                        ambientProbe[1, 6] * 3,
                        ambientProbe[1, 7]
                    )
                );
                m_SSGIMaterial.SetVector(
                    shBb,
                    new Vector4(
                        ambientProbe[2, 4],
                        ambientProbe[2, 5],
                        ambientProbe[2, 6] * 3,
                        ambientProbe[2, 7]
                    )
                );
                m_SSGIMaterial.SetVector(
                    shC,
                    new Vector4(ambientProbe[0, 8], ambientProbe[1, 8], ambientProbe[2, 8], 1)
                );
            }

            RenderTextureDescriptor desc = renderingData.cameraData.cameraTargetDescriptor;
            if (desc.width != width)
                desc = new RenderTextureDescriptor(width, height);
            desc.graphicsFormat = GraphicsFormat.B10G11R11_UFloatPack32;
            desc.depthBufferBits = 0; // Color and depth cannot be combined in RTHandles
            desc.stencilFormat = GraphicsFormat.None;
            desc.depthStencilFormat = GraphicsFormat.None;
            desc.msaaSamples = 1;
            desc.bindMS = false;
            RenderTextureDescriptor depthDesc = desc;

#if UNITY_6000_0_OR_NEWER
            RenderingUtils.ReAllocateHandleIfNeeded(
                ref m_IntermediateCameraColorHandle,
                desc,
                FilterMode.Point,
                TextureWrapMode.Clamp,
                name: _IntermediateCameraColorTexture
            );
#else
            RenderingUtils.ReAllocateIfNeeded(
                ref m_IntermediateCameraColorHandle,
                desc,
                FilterMode.Point,
                TextureWrapMode.Clamp,
                name: _IntermediateCameraColorTexture
            );
#endif

#if UNITY_6000_0_OR_NEWER
            RenderingUtils.ReAllocateHandleIfNeeded(
                ref m_APVLightingHandle,
                desc,
                FilterMode.Point,
                TextureWrapMode.Clamp,
                name: _APVLightingTexture
            );
#else
            RenderingUtils.ReAllocateIfNeeded(
                ref m_APVLightingHandle,
                desc,
                FilterMode.Point,
                TextureWrapMode.Clamp,
                name: _APVLightingTexture
            );
#endif

            desc.width = Mathf.FloorToInt(desc.width * resolutionScale);
            desc.height = Mathf.FloorToInt(desc.height * resolutionScale);

#if UNITY_6000_0_OR_NEWER
            RenderingUtils.ReAllocateHandleIfNeeded(
                ref m_HistoryCameraColorHandle,
                desc,
                FilterMode.Point,
                TextureWrapMode.Clamp,
                name: _SSGIHistoryCameraColorTexture
            );
#else
            RenderingUtils.ReAllocateIfNeeded(
                ref m_HistoryCameraColorHandle,
                desc,
                FilterMode.Point,
                TextureWrapMode.Clamp,
                name: _SSGIHistoryCameraColorTexture
            );
#endif
            // Avoid reprojecting from uninitialized history texture
            if (isHistoryTextureValid)
            {
                m_SSGIMaterial.SetFloat(_HistoryTextureValid, 1.0f);
                m_SSGIMaterial.SetTexture(
                    ssgiHistoryCameraColorTexture,
                    m_HistoryCameraColorHandle
                );
            }
            else
            {
                m_SSGIMaterial.SetFloat(_HistoryTextureValid, 0.0f);
                m_SSGIMaterial.SetTexture(
                    ssgiHistoryCameraColorTexture,
                    m_IntermediateCameraColorHandle
                );
                isHistoryTextureValid = true;
            }

            desc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
            depthDesc.graphicsFormat = GraphicsFormat.R32_SFloat;

            RenderTextureDescriptor denoiseDesc = desc;
            denoiseDesc.enableRandomWrite = true;

#if UNITY_6000_0_OR_NEWER
            RenderingUtils.ReAllocateHandleIfNeeded(
                ref m_DiffuseHandle,
                denoiseDesc,
                FilterMode.Point,
                TextureWrapMode.Clamp,
                name: _IndirectDiffuseTexture
            );
            RenderingUtils.ReAllocateHandleIfNeeded(
                ref m_IntermediateDiffuseHandle,
                denoiseDesc,
                FilterMode.Point,
                TextureWrapMode.Clamp,
                name: _IntermediateIndirectDiffuseTexture
            );
            denoiseDesc.enableRandomWrite = false;
            RenderingUtils.ReAllocateHandleIfNeeded(
                ref m_HistoryIndirectDiffuseHandle,
                desc,
                FilterMode.Point,
                TextureWrapMode.Clamp,
                name: _HistoryIndirectDiffuseTexture
            );
            RenderingUtils.ReAllocateHandleIfNeeded(
                ref m_HistoryDepthHandle,
                depthDesc,
                FilterMode.Point,
                TextureWrapMode.Clamp,
                name: _SSGIHistoryDepthTexture
            );
#else
            RenderingUtils.ReAllocateIfNeeded(
                ref m_DiffuseHandle,
                denoiseDesc,
                FilterMode.Point,
                TextureWrapMode.Clamp,
                name: _IndirectDiffuseTexture
            );
            RenderingUtils.ReAllocateIfNeeded(
                ref m_IntermediateDiffuseHandle,
                denoiseDesc,
                FilterMode.Point,
                TextureWrapMode.Clamp,
                name: _IntermediateIndirectDiffuseTexture
            );
            denoiseDesc.enableRandomWrite = false;
            RenderingUtils.ReAllocateIfNeeded(
                ref m_HistoryIndirectDiffuseHandle,
                desc,
                FilterMode.Point,
                TextureWrapMode.Clamp,
                name: _HistoryIndirectDiffuseTexture
            );
            RenderingUtils.ReAllocateIfNeeded(
                ref m_HistoryDepthHandle,
                depthDesc,
                FilterMode.Point,
                TextureWrapMode.Clamp,
                name: _SSGIHistoryDepthTexture
            );
#endif

            desc.graphicsFormat = GraphicsFormat.R16_SFloat;

#if UNITY_6000_0_OR_NEWER
            RenderingUtils.ReAllocateHandleIfNeeded(
                ref m_AccumulateSampleHandle,
                desc,
                FilterMode.Point,
                TextureWrapMode.Clamp,
                name: _SSGISampleTexture
            );
            RenderingUtils.ReAllocateHandleIfNeeded(
                ref m_AccumulateHistorySampleHandle,
                desc,
                FilterMode.Point,
                TextureWrapMode.Clamp,
                name: _SSGIHistorySampleTexture
            );
#else
            RenderingUtils.ReAllocateIfNeeded(
                ref m_AccumulateSampleHandle,
                desc,
                FilterMode.Point,
                TextureWrapMode.Clamp,
                name: _SSGISampleTexture
            );
            RenderingUtils.ReAllocateIfNeeded(
                ref m_AccumulateHistorySampleHandle,
                desc,
                FilterMode.Point,
                TextureWrapMode.Clamp,
                name: _SSGIHistorySampleTexture
            );
#endif

            m_SSGIMaterial.SetTexture(ssgiHistoryDepthTexture, m_HistoryDepthHandle);
            m_SSGIMaterial.SetTexture(
                historyIndirectDiffuseTexture,
                m_HistoryIndirectDiffuseHandle
            );
            m_SSGIMaterial.SetTexture(ssgiSampleTexture, m_AccumulateSampleHandle);
            m_SSGIMaterial.SetTexture(ssgiHistorySampleTexture, m_AccumulateHistorySampleHandle);

            if (useAtrousDenoiser)
            {
                RenderTextureDescriptor atrousDesc = desc;
                atrousDesc.enableRandomWrite = true;
                bool fastSchedule = ssgiVolume.fastAtrousSchedule.value;
                int atrousIterations = Mathf.Clamp(ssgiVolume.atrousIterations.value, 1, 6);
                bool needsPingPong = !fastSchedule || atrousIterations > 1;

                if (needsPingPong)
                {
#if UNITY_6000_0_OR_NEWER
                    RenderingUtils.ReAllocateHandleIfNeeded(
                        ref m_AtrousPingHandle,
                        atrousDesc,
                        FilterMode.Point,
                        TextureWrapMode.Clamp,
                        name: "_SSGI_AtrousPing"
                    );
                    RenderingUtils.ReAllocateHandleIfNeeded(
                        ref m_AtrousPongHandle,
                        atrousDesc,
                        FilterMode.Point,
                        TextureWrapMode.Clamp,
                        name: "_SSGI_AtrousPong"
                    );
#else
                    RenderingUtils.ReAllocateIfNeeded(
                        ref m_AtrousPingHandle,
                        atrousDesc,
                        FilterMode.Point,
                        TextureWrapMode.Clamp,
                        name: "_SSGI_AtrousPing"
                    );
                    RenderingUtils.ReAllocateIfNeeded(
                        ref m_AtrousPongHandle,
                        atrousDesc,
                        FilterMode.Point,
                        TextureWrapMode.Clamp,
                        name: "_SSGI_AtrousPong"
                    );
#endif
                }
                else
                {
                    m_AtrousPingHandle?.Release();
                    m_AtrousPongHandle?.Release();
                    m_AtrousPingHandle = null;
                    m_AtrousPongHandle = null;
                }
            }
            else
            {
                m_AtrousPingHandle?.Release();
                m_AtrousPongHandle?.Release();
                m_AtrousPingHandle = null;
                m_AtrousPongHandle = null;
            }

            ScriptableRenderPassInput requiredInputs = ScriptableRenderPassInput.Depth;
            if (
                !useAnySpatial
                || adaptiveNeedsMotion
                || (useHybridDenoiser && hybridLowMotion)
                || useNRDDenoiser
            )
                requiredInputs |= ScriptableRenderPassInput.Motion;
            ConfigureInput(requiredInputs);
        }
        #endregion

#if UNITY_6000_0_OR_NEWER
        #region Render Graph Pass
        // This class stores the data needed by the pass, passed as parameter to the delegate function that executes the pass
        private class PassData
        {
            internal Material ssgiMaterial;

            internal RenderTargetIdentifier[] rTHandles;

            // Camera color & direct lighting color
            internal TextureHandle cameraColorTargetHandle;
            internal TextureHandle cameraDepthTextureHandle;

            internal TextureHandle intermediateCameraColorHandle;
            internal TextureHandle historyCameraColorHandle;
            internal TextureHandle apvLightingHandle;

            // SSGI diffuse lighting
            internal TextureHandle diffuseHandle;
            internal TextureHandle intermediateDiffuseHandle;

            // Denoising
            internal TextureHandle historyDiffuseHandle;
            internal TextureHandle historyDepthHandle;
            internal TextureHandle accumulateSampleHandle;
            internal TextureHandle accumulateHistorySampleHandle;

            // GBuffers created by URP
            internal bool localGBuffers;
            internal TextureHandle gBuffer0Handle;
            internal TextureHandle gBuffer1Handle;
            internal TextureHandle gBuffer2Handle;

            internal int width;
            internal int height;

            internal bool denoise;
            internal bool secondDenoise;
            internal bool aggressiveDenoise;
            internal bool useSpatialFilter;
            internal bool useHybridTemporal;
            internal bool hybridLowMotion;
            internal SSGIHybridTemporalDenoiser.Settings hybridSettings;
            internal ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm denoiserAlgorithm;
            internal Vector4 scaleBias;
            internal bool overrideAmbientLighting;
            internal bool outputAPVLighting;
            internal bool useAdaptiveLut;
            internal bool adaptiveTemporal;
            internal SSGIAdaptiveLutDenoiser.Settings adaptiveSettings;
            internal TextureHandle adaptiveFastHistoryHandle;
            internal TextureHandle adaptiveMainHistoryHandle;
            internal TextureHandle adaptiveMomentsHandle;
            internal TextureHandle adaptiveTemporalOutputHandle;
            internal SSGIAdaptiveLutDenoiser adaptiveDenoiser;
            internal TextureHandle motionVectorHandle;
            internal TextureHandle normalTextureHandle;
            internal bool useNrd;
            internal bool nrdLowMotion;
            internal NRDDenoiser.Settings nrdSettings;
            internal NRDDenoiser nrdDenoiser;

            internal bool useSpatialSingleFrame;
            internal SSGISpatialSingleFrameDenoiser.Settings spatialSettings;
            internal SSGISpatialSingleFrameDenoiser singleFrameDenoiser;

            internal bool useAtrous;
            internal bool useAtrousFast;
            internal SSGIEdgeAwareAtrousDenoiser.Settings atrousSettings;
            internal SSGIEdgeAwareAtrousDenoiserFast.Settings atrousFastSettings;
            internal TextureHandle atrousPingHandle;
            internal TextureHandle atrousPongHandle;
            internal SSGIEdgeAwareAtrousDenoiser edgeAwareAtrousDenoiser;
            internal SSGIEdgeAwareAtrousDenoiserFast edgeAwareAtrousDenoiserFast;

            internal bool useWalr;
            internal SSGIWalrDenoiser.Settings walrSettings;
            internal SSGIWalrDenoiser walrDenoiser;

            internal SSGITemporalDenoiser temporalDenoiser;
        }

        // This static method is used to execute the pass and passed as the RenderFunc delegate to the RenderGraph render pass
        static void ExecutePass(PassData data, UnsafeGraphContext context)
        {
            CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

            data.ssgiMaterial.SetTexture(cameraDepthTexture, data.cameraDepthTextureHandle);

            if (data.localGBuffers)
            {
                data.ssgiMaterial.SetTexture(gBuffer0, data.gBuffer0Handle);
                data.ssgiMaterial.SetTexture(gBuffer1, data.gBuffer1Handle);
                data.ssgiMaterial.SetTexture(gBuffer2, data.gBuffer2Handle);
            }
            else
            {
                // Global gbuffer textures
                data.ssgiMaterial.SetTexture(gBuffer0, null);
                data.ssgiMaterial.SetTexture(gBuffer1, null);
                data.ssgiMaterial.SetTexture(gBuffer2, null);
            }

            // Copy Direct Lighting
            if (data.overrideAmbientLighting)
            {
                if (data.outputAPVLighting)
                {
                    data.rTHandles[0] = data.intermediateCameraColorHandle;
                    data.rTHandles[1] = data.apvLightingHandle;
                    // RT-1: direct lighting
                    // RT-2: indirect lighting (APV)
                    cmd.SetRenderTarget(data.rTHandles, data.intermediateCameraColorHandle);
                    Blitter.BlitTexture(
                        cmd,
                        data.cameraColorTargetHandle,
                        m_ScaleBias,
                        data.ssgiMaterial,
                        pass: 0
                    );
                    data.ssgiMaterial.SetTexture(apvLightingTexture, data.apvLightingHandle);
                }
                else
                    Blitter.BlitCameraTexture(
                        cmd,
                        data.cameraColorTargetHandle,
                        data.intermediateCameraColorHandle,
                        data.ssgiMaterial,
                        pass: 0
                    );
            }
            else
                Blitter.BlitCameraTexture(
                    cmd,
                    data.cameraColorTargetHandle,
                    data.intermediateCameraColorHandle
                );

            if (data.denoise)
            {
                // Render SSGI
                Blitter.BlitCameraTexture(
                    cmd,
                    data.intermediateCameraColorHandle,
                    data.intermediateDiffuseHandle,
                    RenderBufferLoadAction.Load,
                    RenderBufferStoreAction.Store,
                    data.ssgiMaterial,
                    pass: 1
                );
                data.ssgiMaterial.SetTexture(indirectDiffuseTexture, data.diffuseHandle);

                bool depthValid = data.cameraDepthTextureHandle.IsValid();
                bool normalValid = data.normalTextureHandle.IsValid();
                bool motionValid = data.motionVectorHandle.IsValid();
                bool hasAlbedo = data.localGBuffers && data.gBuffer0Handle.IsValid();
                TextureHandle fallbackAlbedoHandle = data.diffuseHandle;

                void ClearAccumulationTexture(TextureHandle target)
                {
                    cmd.SetRenderTarget(
                        target,
                        RenderBufferLoadAction.DontCare,
                        RenderBufferStoreAction.Store,
                        target,
                        RenderBufferLoadAction.DontCare,
                        RenderBufferStoreAction.DontCare
                    );
                    CoreUtils.ClearRenderTarget(cmd, ClearFlag.Color, Color.black);
                }

                switch (data.denoiserAlgorithm)
                {
                    case ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.SingleFrame:
                    {
                        if (
                            !data.useSpatialSingleFrame
                            || data.singleFrameDenoiser == null
                            || !depthValid
                            || !normalValid
                        )
                            goto default;

                        var settings = data.spatialSettings;
                        bool executed = data.singleFrameDenoiser.Dispatch(
                            cmd,
                            settings,
                            data.intermediateDiffuseHandle,
                            data.diffuseHandle,
                            data.cameraDepthTextureHandle,
                            data.normalTextureHandle,
                            hasAlbedo ? data.gBuffer0Handle : fallbackAlbedoHandle,
                            fallbackAlbedoHandle,
                            hasAlbedo,
                            data.width,
                            data.height
                        );
                        if (!executed)
                            cmd.CopyTexture(data.intermediateDiffuseHandle, data.diffuseHandle);

                        ClearAccumulationTexture(data.accumulateSampleHandle);
                        break;
                    }
                    case ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.EdgeAwareAtrous:
                    {
                        if (
                            !data.useAtrous
                            || (!data.useAtrousFast && data.edgeAwareAtrousDenoiser == null)
                            || (data.useAtrousFast && data.edgeAwareAtrousDenoiserFast == null)
                            || !depthValid
                            || !normalValid
                        )
                            goto default;

                        bool executed = false;

                        if (data.useAtrousFast && data.edgeAwareAtrousDenoiserFast != null)
                        {
                            executed = data.edgeAwareAtrousDenoiserFast.Dispatch(
                                cmd,
                                data.atrousFastSettings,
                                data.intermediateDiffuseHandle,
                                data.diffuseHandle,
                                data.atrousPingHandle,
                                data.atrousPongHandle,
                                data.cameraDepthTextureHandle,
                                data.normalTextureHandle,
                                hasAlbedo ? data.gBuffer0Handle : fallbackAlbedoHandle,
                                fallbackAlbedoHandle,
                                hasAlbedo,
                                data.width,
                                data.height
                            );
                        }
                        else if (data.edgeAwareAtrousDenoiser != null)
                        {
                            executed = data.edgeAwareAtrousDenoiser.Dispatch(
                                cmd,
                                data.atrousSettings,
                                data.intermediateDiffuseHandle,
                                data.diffuseHandle,
                                data.atrousPingHandle,
                                data.atrousPongHandle,
                                data.cameraDepthTextureHandle,
                                data.normalTextureHandle,
                                hasAlbedo ? data.gBuffer0Handle : fallbackAlbedoHandle,
                                fallbackAlbedoHandle,
                                hasAlbedo,
                                data.width,
                                data.height
                            );
                        }

                        if (!executed)
                            cmd.CopyTexture(data.intermediateDiffuseHandle, data.diffuseHandle);

                        ClearAccumulationTexture(data.accumulateSampleHandle);
                        break;
                    }
                    case ScreenSpaceGlobalIlluminationVolume
                        .DenoiserAlgorithm
                        .WeightedAtrousLinearRegression:
                    {
                        if (
                            !data.useWalr
                            || data.walrDenoiser == null
                            || !depthValid
                            || !normalValid
                        )
                            goto default;

                        bool executed = data.walrDenoiser.Dispatch(
                            cmd,
                            data.walrSettings,
                            data.intermediateDiffuseHandle,
                            data.diffuseHandle,
                            data.cameraDepthTextureHandle,
                            data.normalTextureHandle,
                            hasAlbedo ? data.gBuffer0Handle : fallbackAlbedoHandle,
                            fallbackAlbedoHandle,
                            hasAlbedo,
                            data.width,
                            data.height
                        );

                        if (!executed)
                            cmd.CopyTexture(data.intermediateDiffuseHandle, data.diffuseHandle);

                        ClearAccumulationTexture(data.accumulateSampleHandle);
                        break;
                    }
                    case ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.EdgeAdaptiveLut:
                    {
                        if (
                            !data.useAdaptiveLut
                            || data.adaptiveDenoiser == null
                            || !depthValid
                            || !normalValid
                        )
                            goto default;

                        var resources = new SSGIAdaptiveLutDenoiser.RenderGraphResourceSet
                        {
                            Width = data.width,
                            Height = data.height,
                            Source = data.intermediateDiffuseHandle,
                            Destination = data.diffuseHandle,
                            Depth = data.cameraDepthTextureHandle,
                            Normal = data.normalTextureHandle,
                            Motion = data.motionVectorHandle,
                            HistoryDepth = data.historyDepthHandle,
                            FastHistory = data.adaptiveFastHistoryHandle,
                            MainHistory = data.adaptiveMainHistoryHandle,
                            Moments = data.adaptiveMomentsHandle,
                            TemporalOutput = data.adaptiveTemporalOutputHandle,
                            HasTemporal = data.adaptiveTemporal && motionValid,
                        };

                        Vector4 zParams = Shader.GetGlobalVector(_ZBufferParams);
                        if (
                            !data.adaptiveDenoiser.Dispatch(
                                cmd,
                                zParams,
                                data.adaptiveSettings,
                                in resources
                            )
                        )
                            cmd.CopyTexture(data.intermediateDiffuseHandle, data.diffuseHandle);

                        ClearAccumulationTexture(data.accumulateSampleHandle);
                        break;
                    }
                    case ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.NRD:
                    {
                        if (
                            !data.useNrd
                            || data.nrdLowMotion
                            || data.temporalDenoiser == null
                            || data.singleFrameDenoiser == null
                            || !depthValid
                            || !normalValid
                        )
                            goto default;

                        float temporalIntensity = data.nrdSettings.MaxAccumulatedFrames <= 1
                            ? 0.0f
                            : Mathf.Clamp01(1.0f - (1.0f / data.nrdSettings.MaxAccumulatedFrames));

                        float originalTemporal = data.ssgiMaterial.GetFloat(_TemporalIntensity);
                        data.ssgiMaterial.SetFloat(_TemporalIntensity, temporalIntensity);

                        data.temporalDenoiser.Dispatch(
                            cmd,
                            data.ssgiMaterial,
                            data.scaleBias,
                            data.intermediateDiffuseHandle,
                            data.diffuseHandle,
                            data.accumulateSampleHandle,
                            data.rTHandles,
                            aggressiveDenoise: false,
                            data.secondDenoise
                        );

                        data.ssgiMaterial.SetFloat(_TemporalIntensity, originalTemporal);

                        cmd.CopyTexture(data.diffuseHandle, data.intermediateDiffuseHandle);

                        var spatialSettings = data.spatialSettings;
                        spatialSettings.Radius = Mathf.Max(1.0f, data.nrdSettings.SpatialRadius);
                        spatialSettings.SigmaColor = Mathf.Max(
                            1e-4f,
                            spatialSettings.SigmaColor
                                / Mathf.Max(0.001f, data.nrdSettings.SigmaMultiplier)
                        );

                        bool executed = data.singleFrameDenoiser.Dispatch(
                            cmd,
                            spatialSettings,
                            data.intermediateDiffuseHandle,
                            data.diffuseHandle,
                            data.cameraDepthTextureHandle,
                            data.normalTextureHandle,
                            hasAlbedo ? data.gBuffer0Handle : fallbackAlbedoHandle,
                            fallbackAlbedoHandle,
                            hasAlbedo,
                            data.width,
                            data.height
                        );

                        if (!executed)
                            cmd.CopyTexture(data.intermediateDiffuseHandle, data.diffuseHandle);

                        ClearAccumulationTexture(data.accumulateSampleHandle);
                        break;
                    }
                    case ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.HybridTemporal:
                    {
                        if (
                            !data.useHybridTemporal
                            || data.temporalDenoiser == null
                        )
                            goto default;

                        if (data.hybridLowMotion || !depthValid || !normalValid)
                        {
                            data.temporalDenoiser.Dispatch(
                                cmd,
                                data.ssgiMaterial,
                                data.scaleBias,
                                data.intermediateDiffuseHandle,
                                data.diffuseHandle,
                                data.accumulateSampleHandle,
                                data.rTHandles,
                                aggressiveDenoise: false,
                                data.secondDenoise
                            );
                        }
                        else if (
                            data.singleFrameDenoiser != null
                        )
                        {
                            var settings = data.spatialSettings;
                            bool executed = data.singleFrameDenoiser.Dispatch(
                                cmd,
                                settings,
                                data.intermediateDiffuseHandle,
                                data.diffuseHandle,
                                data.cameraDepthTextureHandle,
                                data.normalTextureHandle,
                                hasAlbedo ? data.gBuffer0Handle : fallbackAlbedoHandle,
                                fallbackAlbedoHandle,
                                hasAlbedo,
                                data.width,
                                data.height
                            );
                            if (!executed)
                                cmd.CopyTexture(data.intermediateDiffuseHandle, data.diffuseHandle);

                            ClearAccumulationTexture(data.accumulateSampleHandle);
                        }
                        else
                        {
                            goto default;
                        }

                        break;
                    }
                    case ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.Conservative:
                    case ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.Aggressive:
                    default:
                    {
                        // Reproject GI
                        cmd.SetRenderTarget(
                            data.accumulateSampleHandle,
                            RenderBufferLoadAction.Load,
                            RenderBufferStoreAction.Store,
                            data.accumulateSampleHandle,
                            RenderBufferLoadAction.DontCare,
                            RenderBufferStoreAction.DontCare
                        );

                        data.rTHandles[0] = data.diffuseHandle;
                        data.rTHandles[1] = data.accumulateSampleHandle;
                        // RT-1: accumulated results
                        // RT-2: accumulated sample count
                        cmd.SetRenderTarget(data.rTHandles, data.accumulateSampleHandle);
                        Blitter.BlitTexture(
                            cmd,
                            data.intermediateDiffuseHandle,
                            data.scaleBias,
                            data.ssgiMaterial,
                            pass: 2
                        );

                        if (data.aggressiveDenoise)
                        {
                            Blitter.BlitCameraTexture(
                                cmd,
                                data.diffuseHandle,
                                data.intermediateDiffuseHandle,
                                data.ssgiMaterial,
                                pass: 8
                            );
                            Blitter.BlitCameraTexture(
                                cmd,
                                data.intermediateDiffuseHandle,
                                data.diffuseHandle,
                                data.ssgiMaterial,
                                pass: 8
                            );
                        }

                        if (data.secondDenoise)
                        {
                            Blitter.BlitCameraTexture(
                                cmd,
                                data.diffuseHandle,
                                data.intermediateDiffuseHandle,
                                data.ssgiMaterial,
                                pass: 3
                            );
                            Blitter.BlitCameraTexture(
                                cmd,
                                data.intermediateDiffuseHandle,
                                data.diffuseHandle,
                                data.ssgiMaterial,
                                pass: 4
                            );
                        }
                        break;
                    }
                }

                cmd.CopyTexture(data.diffuseHandle, data.historyDiffuseHandle);

                // Update History Depth
                Blitter.BlitCameraTexture(
                    cmd,
                    data.historyDepthHandle,
                    data.historyDepthHandle,
                    data.ssgiMaterial,
                    pass: 5
                );

                cmd.CopyTexture(data.accumulateSampleHandle, data.accumulateHistorySampleHandle);
            }
            else
            {
                // SSGI
                Blitter.BlitCameraTexture(
                    cmd,
                    data.intermediateCameraColorHandle,
                    data.diffuseHandle,
                    RenderBufferLoadAction.Load,
                    RenderBufferStoreAction.Store,
                    data.ssgiMaterial,
                    pass: 1
                );
                data.ssgiMaterial.SetTexture(indirectDiffuseTexture, data.diffuseHandle);

                // Update History Depth
                Blitter.BlitCameraTexture(
                    cmd,
                    data.historyDepthHandle,
                    data.historyDepthHandle,
                    data.ssgiMaterial,
                    pass: 5
                );
            }

            // Combine
            Blitter.BlitCameraTexture(
                cmd,
                data.intermediateCameraColorHandle,
                data.cameraColorTargetHandle,
                data.ssgiMaterial,
                pass: 6
            );

            // Copy History Scene Color
            Blitter.BlitCameraTexture(
                cmd,
                data.cameraColorTargetHandle,
                data.historyCameraColorHandle,
                RenderBufferLoadAction.DontCare,
                RenderBufferStoreAction.Store,
                data.ssgiMaterial,
                pass: 9
            );
        }

        // This is where the renderGraph handle can be accessed.
        // Each ScriptableRenderPass can use the RenderGraph handle to add multiple render passes to the render graph
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            // add an unsafe render pass to the render graph, specifying the name and the data type that will be passed to the ExecutePass function
            using (
                var builder = renderGraph.AddUnsafePass<PassData>(m_ProfilerTag, out var passData)
            )
            {
                // UniversalResourceData contains all the texture handles used by the renderer, including the active color and depth textures
                // The active color and depth textures are the main color and depth buffers that the camera renders into
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                UniversalLightData lightData = frameData.Get<UniversalLightData>();
                UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();

                var visibleReflectionProbes = renderingData.cullResults.visibleReflectionProbes;
                var camera = cameraData.camera;
                bool isReflectionCamera = camera.cameraType == CameraType.Reflection;

                int currentCameraHash = camera.GetHashCode();
                int cameraHistoryIndex = GetCameraHistoryDataIndex(currentCameraHash);

                if (!hasProbeAtlas)
                    UpdateReflectionProbe(visibleReflectionProbes, camera.transform.position);
                else
                    m_SSGIMaterial.SetFloat(probeSet, 0.0f);

                m_SSGIMaterial.SetFloat(frameIndex, frameCount);
                bool useBlueNoiseSampling = ssgiVolume.blueNoiseSampling.value;
                m_SSGIMaterial.SetFloat(_SSGIUseBlueNoise, useBlueNoiseSampling ? 1.0f : 0.0f);
                m_SSGIMaterial.SetVector(
                    _ReBlurBlurRotator,
                    EvaluateRotator(k_BlurRands[frameCount % 32])
                );
                frameCount += 33;
                frameCount %= 64000;

                int width = (int)(camera.scaledPixelWidth * cameraData.renderScale);
                int height = (int)(camera.scaledPixelHeight * cameraData.renderScale);

                bool denoiseStateChanged = ssgiVolume.denoiseSS.value != enableDenoise;
                bool resolutionStateChanged = ssgiVolume.fullResolutionSS.value
                    ? resolutionScale != 1.0f
                    : ssgiVolume.resolutionScaleSS.value != resolutionScale;
                bool cameraHasChanged = cameraHistoryIndex == -1;

                // Reorder the history data array when camera is new
                UpdateCameraHistoryData(cameraHasChanged);
                // Assign the data to index 0 for the new camera
                cameraHistoryIndex = cameraHasChanged ? 0 : cameraHistoryIndex;

                ref var prevCamInvVPMatrix = ref cameraHistoryData[
                    cameraHistoryIndex
                ].prevCamInvVPMatrix;
                ref var prevCameraPositionWS = ref cameraHistoryData[
                    cameraHistoryIndex
                ].prevCameraPositionWS;
                ref var historyCameraHash = ref cameraHistoryData[cameraHistoryIndex].hash;

                Vector3 currentCameraPosition = camera.transform.position;
                cameraMotionMagnitude = cameraHasChanged
                    ? float.MaxValue
                    : Vector3.Distance(prevCameraPositionWS, currentCameraPosition);

                if (prevCamInvVPMatrix != null)
                    m_SSGIMaterial.SetMatrix(_PrevInvViewProjMatrix, prevCamInvVPMatrix);
                else
                    m_SSGIMaterial.SetMatrix(
                        _PrevInvViewProjMatrix,
                        camera.previousViewProjectionMatrix.inverse
                    );

                if (prevCameraPositionWS != null)
                    m_SSGIMaterial.SetVector(_PrevCameraPositionWS, prevCameraPositionWS);
                else
                    m_SSGIMaterial.SetVector(_PrevCameraPositionWS, camera.transform.position);

                prevCamInvVPMatrix = (
                    GL.GetGPUProjectionMatrix(camera.projectionMatrix, true)
                    * cameraData.GetViewMatrix()
                ).inverse;
                prevCameraPositionWS = currentCameraPosition;
                historyCameraHash = currentCameraHash;

                // The spread angle is used to compute the world space pixel footprint during denoising.
                // We use low FOV for orthographic cameras as a temporary solution.
                float fieldOfView = camera.orthographic ? 1.0f : camera.fieldOfView;
                m_SSGIMaterial.SetFloat(
                    _PixelSpreadAngleTangent,
                    Mathf.Tan(fieldOfView * Mathf.Deg2Rad * 0.5f)
                        * 2.0f
                        / Mathf.Min(
                            Mathf.FloorToInt(camera.scaledPixelWidth * resolutionScale),
                            Mathf.FloorToInt(camera.scaledPixelHeight * resolutionScale)
                        )
                );

                ref float historyCameraScaledWidth = ref cameraHistoryData[
                    cameraHistoryIndex
                ].scaledWidth;
                ref float historyCameraScaledHeight = ref cameraHistoryData[
                    cameraHistoryIndex
                ].scaledHeight;

                resolutionStateChanged |=
                    (historyCameraScaledWidth != width) || (historyCameraScaledHeight != height);
                if (!cameraHasChanged && (denoiseStateChanged || resolutionStateChanged))
                    isHistoryTextureValid = false;

                historyCameraScaledWidth = width;
                historyCameraScaledHeight = height;

                resolutionScale = ssgiVolume.fullResolutionSS.value
                    ? 1.0f
                    : ssgiVolume.resolutionScaleSS.value;
                m_SSGIMaterial.SetFloat(downSample, resolutionScale);

                enableDenoise = ssgiVolume.denoiseSS.value;
                bool useSpatialDenoiserRG =
                    enableDenoise
                    && ssgiVolume.denoiserAlgorithmSS.value
                        == ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.SingleFrame
                    && singleFrameDenoiser != null
                    && singleFrameDenoiser.IsSupported;

                bool useAtrousDenoiserRG =
                    enableDenoise
                    && ssgiVolume.denoiserAlgorithmSS.value
                        == ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.EdgeAwareAtrous
                    && edgeAwareAtrousDenoiser != null
                    && edgeAwareAtrousDenoiser.IsSupported;

                bool useWalrDenoiserRG =
                    enableDenoise
                    && ssgiVolume.denoiserAlgorithmSS.value
                        == ScreenSpaceGlobalIlluminationVolume
                            .DenoiserAlgorithm
                            .WeightedAtrousLinearRegression
                    && walrDenoiser != null
                    && walrDenoiser.IsSupported;

                bool useAdaptiveLutDenoiserRG =
                    enableDenoise
                    && ssgiVolume.denoiserAlgorithmSS.value
                        == ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.EdgeAdaptiveLut
                    && adaptiveLutDenoiser != null
                    && adaptiveLutDenoiser.IsSupported;

                bool adaptiveNeedsMotionRG =
                    useAdaptiveLutDenoiserRG
                    && ssgiVolume.adaptiveUseTemporal.value
                    && adaptiveLutDenoiser != null
                    && adaptiveLutDenoiser.SupportsTemporal;

                bool useHybridDenoiserRG =
                    enableDenoise
                    && ssgiVolume.denoiserAlgorithmSS.value
                        == ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.HybridTemporal
                    && hybridTemporalDenoiser != null
                    && hybridTemporalDenoiser.IsSupported
                    && singleFrameDenoiser != null
                    && singleFrameDenoiser.IsSupported;

                SSGIHybridTemporalDenoiser.Settings hybridSettingsRG = default;
                bool hybridLowMotionRG = false;
                if (useHybridDenoiserRG)
                {
                    hybridSettingsRG = hybridTemporalDenoiser.CreateSettings(ssgiVolume);
                    hybridLowMotionRG =
                        hybridSettingsRG.MotionThreshold <= 0.0f
                        || cameraMotionMagnitude <= hybridSettingsRG.MotionThreshold;
                }

                bool useNRDDenoiserRG =
                    enableDenoise
                    && ssgiVolume.denoiserAlgorithmSS.value
                        == ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.NRD
                    && nrdDenoiser != null
                    && nrdDenoiser.IsSupported;
                NRDDenoiser.Settings nrdSettingsRG = default;
                bool nrdLowMotionRG = false;
                if (useNRDDenoiserRG)
                {
                    nrdSettingsRG = nrdDenoiser.CreateSettings(ssgiVolume);
                    nrdLowMotionRG =
                        nrdSettingsRG.MotionThreshold <= 0.0f
                        || cameraMotionMagnitude <= nrdSettingsRG.MotionThreshold;
                }

                SSGISpatialSingleFrameDenoiser.Settings spatialSettingsRG = default;
                if (singleFrameDenoiser != null)
                    spatialSettingsRG = singleFrameDenoiser.CreateSettings(ssgiVolume);

                SSGIEdgeAwareAtrousDenoiser.Settings atrousSettingsRG = default;
                if (edgeAwareAtrousDenoiser != null)
                    atrousSettingsRG = edgeAwareAtrousDenoiser.CreateSettings(ssgiVolume);

                bool useFastAtrousRG =
                    ssgiVolume.fastAtrousSchedule.value
                    && edgeAwareAtrousDenoiserFast != null
                    && edgeAwareAtrousDenoiserFast.IsSupported;
                SSGIEdgeAwareAtrousDenoiserFast.Settings atrousFastSettingsRG = default;
                if (useFastAtrousRG && edgeAwareAtrousDenoiserFast != null)
                    atrousFastSettingsRG = edgeAwareAtrousDenoiserFast.CreateSettings(ssgiVolume);

                SSGIWalrDenoiser.Settings walrSettingsRG = default;
                if (walrDenoiser != null)
                    walrSettingsRG = walrDenoiser.CreateSettings(ssgiVolume);

                bool useAnySpatialRG =
                    useSpatialDenoiserRG
                    || useAtrousDenoiserRG
                    || useWalrDenoiserRG
                    || useAdaptiveLutDenoiserRG
                    || useNRDDenoiserRG
                    || (useHybridDenoiserRG && !hybridLowMotionRG);

                m_SSGIMaterial.SetFloat(_UseMotionVectorsID, 1.0f);

                passData.denoise = enableDenoise;
                passData.useSpatialFilter = useAnySpatialRG;
                passData.secondDenoise =
                    !passData.useSpatialFilter && ssgiVolume.secondDenoiserPassSS.value;
                passData.aggressiveDenoise =
                    !passData.useSpatialFilter
                    && (
                        ssgiVolume.denoiserAlgorithmSS.value
                        == ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.Aggressive
                    );
                passData.denoiserAlgorithm = ssgiVolume.denoiserAlgorithmSS.value;
                passData.useHybridTemporal = useHybridDenoiserRG;
                passData.hybridSettings = hybridSettingsRG;
                passData.hybridLowMotion = hybridLowMotionRG;
                passData.scaleBias = m_ScaleBias;
                passData.overrideAmbientLighting = overrideAmbientLighting;
                passData.outputAPVLighting = outputAPVLighting;
                passData.useNrd = useNRDDenoiserRG;
                passData.nrdSettings = nrdSettingsRG;
                passData.nrdLowMotion = nrdLowMotionRG;
                passData.nrdDenoiser = nrdDenoiser;
                passData.useSpatialSingleFrame = useSpatialDenoiserRG;
                passData.spatialSettings = spatialSettingsRG;
                passData.singleFrameDenoiser = singleFrameDenoiser;
                passData.useAtrous = useAtrousDenoiserRG;
                passData.useAtrousFast = useFastAtrousRG;
                passData.atrousSettings = atrousSettingsRG;
                passData.atrousFastSettings = atrousFastSettingsRG;
                passData.edgeAwareAtrousDenoiser = edgeAwareAtrousDenoiser;
                passData.edgeAwareAtrousDenoiserFast = edgeAwareAtrousDenoiserFast;
                passData.useWalr = useWalrDenoiserRG;
                passData.walrSettings = walrSettingsRG;
                passData.walrDenoiser = walrDenoiser;
                passData.temporalDenoiser = m_TemporalDenoiser;

                if (overrideAmbientLighting)
                {
                    SphericalHarmonicsL2 ambientProbe = RenderSettings.ambientProbe;

                    m_SSGIMaterial.SetVector(
                        shAr,
                        new Vector4(
                            ambientProbe[0, 3],
                            ambientProbe[0, 1],
                            ambientProbe[0, 2],
                            ambientProbe[0, 0] - ambientProbe[0, 6]
                        )
                    );
                    m_SSGIMaterial.SetVector(
                        shAg,
                        new Vector4(
                            ambientProbe[1, 3],
                            ambientProbe[1, 1],
                            ambientProbe[1, 2],
                            ambientProbe[1, 0] - ambientProbe[1, 6]
                        )
                    );
                    m_SSGIMaterial.SetVector(
                        shAb,
                        new Vector4(
                            ambientProbe[2, 3],
                            ambientProbe[2, 1],
                            ambientProbe[2, 2],
                            ambientProbe[2, 0] - ambientProbe[2, 6]
                        )
                    );
                    m_SSGIMaterial.SetVector(
                        shBr,
                        new Vector4(
                            ambientProbe[0, 4],
                            ambientProbe[0, 5],
                            ambientProbe[0, 6] * 3,
                            ambientProbe[0, 7]
                        )
                    );
                    m_SSGIMaterial.SetVector(
                        shBg,
                        new Vector4(
                            ambientProbe[1, 4],
                            ambientProbe[1, 5],
                            ambientProbe[1, 6] * 3,
                            ambientProbe[1, 7]
                        )
                    );
                    m_SSGIMaterial.SetVector(
                        shBb,
                        new Vector4(
                            ambientProbe[2, 4],
                            ambientProbe[2, 5],
                            ambientProbe[2, 6] * 3,
                            ambientProbe[2, 7]
                        )
                    );
                    m_SSGIMaterial.SetVector(
                        shC,
                        new Vector4(ambientProbe[0, 8], ambientProbe[1, 8], ambientProbe[2, 8], 1)
                    );
                }

                RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
                desc.graphicsFormat = GraphicsFormat.B10G11R11_UFloatPack32;
                desc.depthBufferBits = 0; // Color and depth cannot be combined in RTHandles
                desc.stencilFormat = GraphicsFormat.None;
                desc.msaaSamples = 1;
                desc.bindMS = false;

                TextureHandle intermediateCameraColorHandle =
                    UniversalRenderer.CreateRenderGraphTexture(
                        renderGraph,
                        desc,
                        name: _IntermediateCameraColorTexture,
                        false,
                        FilterMode.Point,
                        TextureWrapMode.Clamp
                    );
                TextureHandle apvLightingHandle = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph,
                    desc,
                    name: _APVLightingTexture,
                    false,
                    FilterMode.Point,
                    TextureWrapMode.Clamp
                );
                RenderTextureDescriptor depthDesc = desc;

                desc.width = Mathf.FloorToInt(desc.width * resolutionScale);
                desc.height = Mathf.FloorToInt(desc.height * resolutionScale);

                desc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                desc.enableRandomWrite = true;
                TextureHandle diffuseHandle = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph,
                    desc,
                    name: _IndirectDiffuseTexture,
                    false,
                    FilterMode.Point,
                    TextureWrapMode.Clamp
                );

                TextureHandle intermediateDiffuseHandle =
                    UniversalRenderer.CreateRenderGraphTexture(
                        renderGraph,
                        desc,
                        name: _IntermediateIndirectDiffuseTexture,
                        false,
                        FilterMode.Point,
                        TextureWrapMode.Clamp
                    );
                desc.enableRandomWrite = false;

                RenderingUtils.ReAllocateHandleIfNeeded(
                    ref m_HistoryCameraColorHandle,
                    desc,
                    FilterMode.Point,
                    TextureWrapMode.Clamp,
                    name: _SSGIHistoryCameraColorTexture
                );
                m_SSGIMaterial.SetTexture(
                    ssgiHistoryCameraColorTexture,
                    m_HistoryCameraColorHandle
                );
                TextureHandle historyCameraColorHandle = renderGraph.ImportTexture(
                    m_HistoryCameraColorHandle
                );
                passData.historyCameraColorHandle = historyCameraColorHandle;
                builder.UseTexture(historyCameraColorHandle, AccessFlags.ReadWrite);

                // Avoid reprojecting from uninitialized history texture
                if (isHistoryTextureValid)
                {
                    m_SSGIMaterial.SetFloat(_HistoryTextureValid, 1.0f);
                    m_SSGIMaterial.SetTexture(
                        ssgiHistoryCameraColorTexture,
                        m_HistoryCameraColorHandle
                    );
                }
                else
                {
                    m_SSGIMaterial.SetFloat(_HistoryTextureValid, 0.0f);
                    isHistoryTextureValid = true;
                }

                depthDesc.colorFormat = RenderTextureFormat.RFloat;
                //depthDesc.graphicsFormat = GraphicsFormat.None;
                //if (resourceData.activeDepthTexture.IsValid())
                //depthDesc.depthBufferBits = (int)resourceData.activeDepthTexture.GetDescriptor(renderGraph).depthBufferBits;
                RenderingUtils.ReAllocateHandleIfNeeded(
                    ref m_HistoryDepthHandle,
                    depthDesc,
                    FilterMode.Point,
                    TextureWrapMode.Clamp,
                    name: _SSGIHistoryDepthTexture
                );
                m_SSGIMaterial.SetTexture(ssgiHistoryDepthTexture, m_HistoryDepthHandle);
                TextureHandle historyDepthHandle = renderGraph.ImportTexture(m_HistoryDepthHandle);
                passData.historyDepthHandle = historyDepthHandle;
                builder.UseTexture(historyDepthHandle, AccessFlags.ReadWrite);

                RenderingUtils.ReAllocateHandleIfNeeded(
                    ref m_HistoryIndirectDiffuseHandle,
                    desc,
                    FilterMode.Point,
                    TextureWrapMode.Clamp,
                    name: _HistoryIndirectDiffuseTexture
                );
                m_SSGIMaterial.SetTexture(
                    historyIndirectDiffuseTexture,
                    m_HistoryIndirectDiffuseHandle
                );
                TextureHandle historyDiffuseHandle = renderGraph.ImportTexture(
                    m_HistoryIndirectDiffuseHandle
                );

                desc.colorFormat = RenderTextureFormat.RHalf;
                RenderingUtils.ReAllocateHandleIfNeeded(
                    ref m_AccumulateSampleHandle,
                    desc,
                    FilterMode.Point,
                    TextureWrapMode.Clamp,
                    name: _SSGISampleTexture
                );
                RenderingUtils.ReAllocateHandleIfNeeded(
                    ref m_AccumulateHistorySampleHandle,
                    desc,
                    FilterMode.Point,
                    TextureWrapMode.Clamp,
                    name: _SSGIHistorySampleTexture
                );
                m_SSGIMaterial.SetTexture(ssgiSampleTexture, m_AccumulateSampleHandle);
                m_SSGIMaterial.SetTexture(
                    ssgiHistorySampleTexture,
                    m_AccumulateHistorySampleHandle
                );
                TextureHandle accumulateSampleHandle = renderGraph.ImportTexture(
                    m_AccumulateSampleHandle
                );
                TextureHandle accumulateHistorySampleHandle = renderGraph.ImportTexture(
                    m_AccumulateHistorySampleHandle
                );

                TextureHandle adaptiveFastHistoryHandle = TextureHandle.nullHandle;
                TextureHandle adaptiveMainHistoryHandle = TextureHandle.nullHandle;
                TextureHandle adaptiveMomentsHandle = TextureHandle.nullHandle;
                TextureHandle adaptiveTemporalOutputHandle = TextureHandle.nullHandle;
                var adaptiveSettings = default(SSGIAdaptiveLutDenoiser.Settings);
                bool adaptiveTemporalEnabled = false;

                if (useAdaptiveLutDenoiserRG)
                {
                    adaptiveSettings = adaptiveLutDenoiser.CreateSettings(ssgiVolume);

                    bool temporalSupportedRG =
                        adaptiveSettings.UseTemporal
                        && adaptiveLutDenoiser != null
                        && adaptiveLutDenoiser.SupportsTemporal;

                    if (temporalSupportedRG)
                    {
                        RenderTextureDescriptor temporalDesc = new RenderTextureDescriptor(
                            width,
                            height,
                            GraphicsFormat.R16G16B16A16_SFloat,
                            0
                        )
                        {
                            depthStencilFormat = GraphicsFormat.None,
                            stencilFormat = GraphicsFormat.None,
                            msaaSamples = 1,
                            bindMS = false,
                            sRGB = false,
                            useMipMap = false,
                            autoGenerateMips = false,
                            enableRandomWrite = true,
                            volumeDepth = 1,
                            mipCount = 1
                        };

                        ref var fastHistoryHandleRG = ref cameraHistoryData[
                            cameraHistoryIndex
                        ].adaptiveFastHistoryHandle;
                        ref var mainHistoryHandleRG = ref cameraHistoryData[
                            cameraHistoryIndex
                        ].adaptiveMainHistoryHandle;
                        ref var momentsHandleRG = ref cameraHistoryData[
                            cameraHistoryIndex
                        ].adaptiveMomentsHandle;

#if UNITY_6000_0_OR_NEWER
                        RenderingUtils.ReAllocateHandleIfNeeded(
                            ref fastHistoryHandleRG,
                            temporalDesc,
                            FilterMode.Point,
                            TextureWrapMode.Clamp,
                            name: "_SSGIAdaptiveHistFast"
                        );
                        RenderingUtils.ReAllocateHandleIfNeeded(
                            ref mainHistoryHandleRG,
                            temporalDesc,
                            FilterMode.Point,
                            TextureWrapMode.Clamp,
                            name: "_SSGIAdaptiveHistMain"
                        );
#else
                        RenderingUtils.ReAllocateIfNeeded(
                            ref fastHistoryHandleRG,
                            temporalDesc,
                            FilterMode.Point,
                            TextureWrapMode.Clamp,
                            name: "_SSGIAdaptiveHistFast"
                        );
                        RenderingUtils.ReAllocateIfNeeded(
                            ref mainHistoryHandleRG,
                            temporalDesc,
                            FilterMode.Point,
                            TextureWrapMode.Clamp,
                            name: "_SSGIAdaptiveHistMain"
                        );
#endif

                        RenderTextureDescriptor momentDesc = temporalDesc;
                        momentDesc.graphicsFormat = GraphicsFormat.R16G16_SFloat;

#if UNITY_6000_0_OR_NEWER
                        RenderingUtils.ReAllocateHandleIfNeeded(
                            ref momentsHandleRG,
                            momentDesc,
                            FilterMode.Point,
                            TextureWrapMode.Clamp,
                            name: "_SSGIAdaptiveMoments"
                        );
                        RenderingUtils.ReAllocateHandleIfNeeded(
                            ref m_AdaptiveTemporalOutputHandle,
                            temporalDesc,
                            FilterMode.Point,
                            TextureWrapMode.Clamp,
                            name: "_SSGIAdaptiveTemporal"
                        );
#else
                        RenderingUtils.ReAllocateIfNeeded(
                            ref momentsHandleRG,
                            momentDesc,
                            FilterMode.Point,
                            TextureWrapMode.Clamp,
                            name: "_SSGIAdaptiveMoments"
                        );
                        RenderingUtils.ReAllocateIfNeeded(
                            ref m_AdaptiveTemporalOutputHandle,
                            temporalDesc,
                            FilterMode.Point,
                            TextureWrapMode.Clamp,
                            name: "_SSGIAdaptiveTemporal"
                        );
#endif

                        if (fastHistoryHandleRG != null)
                            adaptiveFastHistoryHandle = renderGraph.ImportTexture(fastHistoryHandleRG);
                        if (mainHistoryHandleRG != null)
                            adaptiveMainHistoryHandle = renderGraph.ImportTexture(mainHistoryHandleRG);
                        if (momentsHandleRG != null)
                            adaptiveMomentsHandle = renderGraph.ImportTexture(momentsHandleRG);
                        if (m_AdaptiveTemporalOutputHandle != null)
                            adaptiveTemporalOutputHandle = renderGraph.ImportTexture(m_AdaptiveTemporalOutputHandle);

                        adaptiveTemporalEnabled = adaptiveFastHistoryHandle.IsValid()
                            && adaptiveMainHistoryHandle.IsValid()
                            && adaptiveMomentsHandle.IsValid()
                            && adaptiveTemporalOutputHandle.IsValid();

                        if (!adaptiveTemporalEnabled)
                            adaptiveSettings.UseTemporal = false;
                    }
                    else
                    {
                        adaptiveSettings.UseTemporal = false;
                    }
                }

                ScriptableRenderPassInput requiredInputsRG = ScriptableRenderPassInput.Depth;
                if (
                    !useAnySpatialRG
                    || adaptiveNeedsMotionRG
                    || (useHybridDenoiserRG && hybridLowMotionRG)
                    || useNRDDenoiserRG
                )
                    requiredInputsRG |= ScriptableRenderPassInput.Motion;
                ConfigureInput(requiredInputsRG);

                // Fill up the passData with the data needed by the pass
                ApplyBlueNoiseToMaterial(m_SSGIMaterial, useBlueNoiseSampling);
                passData.ssgiMaterial = m_SSGIMaterial;
                passData.cameraColorTargetHandle = resourceData.activeColorTexture;
                passData.cameraDepthTextureHandle = resourceData.cameraDepthTexture;
                passData.diffuseHandle = diffuseHandle;
                passData.historyDiffuseHandle = historyDiffuseHandle;
                passData.intermediateDiffuseHandle = intermediateDiffuseHandle;
                passData.historyDepthHandle = historyDepthHandle;
                passData.accumulateSampleHandle = accumulateSampleHandle;
                passData.accumulateHistorySampleHandle = accumulateHistorySampleHandle;
                passData.intermediateCameraColorHandle = intermediateCameraColorHandle;
                passData.apvLightingHandle = apvLightingHandle;
                passData.rTHandles = rTHandles;
                passData.useAdaptiveLut = useAdaptiveLutDenoiserRG;
                passData.adaptiveTemporal = adaptiveTemporalEnabled;
                passData.adaptiveSettings = adaptiveSettings;
                passData.adaptiveFastHistoryHandle = adaptiveFastHistoryHandle;
                passData.adaptiveMainHistoryHandle = adaptiveMainHistoryHandle;
                passData.adaptiveMomentsHandle = adaptiveMomentsHandle;
                passData.adaptiveTemporalOutputHandle = adaptiveTemporalOutputHandle;
                passData.adaptiveDenoiser = adaptiveLutDenoiser;
                passData.motionVectorHandle = resourceData.motionVectorColor;
                passData.normalTextureHandle = resourceData.cameraNormalsTexture;
                passData.width = width;
                passData.height = height;

                // UnsafePasses don't setup the outputs using UseTextureFragment/UseTextureFragmentDepth, you should specify your writes with UseTexture instead
                builder.UseTexture(passData.cameraColorTargetHandle, AccessFlags.ReadWrite);
                builder.UseTexture(passData.cameraDepthTextureHandle, AccessFlags.Read);
                builder.UseTexture(passData.diffuseHandle, AccessFlags.ReadWrite);
                builder.UseTexture(passData.historyDiffuseHandle, AccessFlags.ReadWrite);
                builder.UseTexture(passData.intermediateDiffuseHandle, AccessFlags.Write);
                builder.UseTexture(passData.accumulateSampleHandle, AccessFlags.ReadWrite);
                builder.UseTexture(passData.accumulateHistorySampleHandle, AccessFlags.ReadWrite);
                builder.UseTexture(passData.intermediateCameraColorHandle, AccessFlags.ReadWrite);
                builder.UseTexture(passData.apvLightingHandle, AccessFlags.Write);
                if (passData.normalTextureHandle.IsValid())
                    builder.UseTexture(passData.normalTextureHandle, AccessFlags.Read);
                builder.UseTexture(resourceData.motionVectorColor, AccessFlags.Read);
                //if (enableRenderingLayers) { builder.UseTexture(resourceData.renderingLayersTexture, AccessFlags.Read); }

                if (adaptiveFastHistoryHandle.IsValid())
                    builder.UseTexture(adaptiveFastHistoryHandle, AccessFlags.ReadWrite);
                if (adaptiveMainHistoryHandle.IsValid())
                    builder.UseTexture(adaptiveMainHistoryHandle, AccessFlags.ReadWrite);
                if (adaptiveMomentsHandle.IsValid())
                    builder.UseTexture(adaptiveMomentsHandle, AccessFlags.ReadWrite);
                if (adaptiveTemporalOutputHandle.IsValid())
                    builder.UseTexture(adaptiveTemporalOutputHandle, AccessFlags.ReadWrite);

                passData.localGBuffers = resourceData.gBuffer[0].IsValid();

                if (passData.localGBuffers)
                {
                    passData.gBuffer0Handle = resourceData.gBuffer[0];
                    passData.gBuffer1Handle = resourceData.gBuffer[1];
                    passData.gBuffer2Handle = resourceData.gBuffer[2];

                    builder.UseTexture(passData.gBuffer0Handle, AccessFlags.Read);
                    builder.UseTexture(passData.gBuffer1Handle, AccessFlags.Read);
                    builder.UseTexture(passData.gBuffer2Handle, AccessFlags.Read);
                }

                TextureHandle atrousPingHandle = TextureHandle.nullHandle;
                TextureHandle atrousPongHandle = TextureHandle.nullHandle;
                if (m_AtrousPingHandle != null)
                    atrousPingHandle = renderGraph.ImportTexture(m_AtrousPingHandle);
                if (m_AtrousPongHandle != null)
                    atrousPongHandle = renderGraph.ImportTexture(m_AtrousPongHandle);
                passData.atrousPingHandle = atrousPingHandle;
                passData.atrousPongHandle = atrousPongHandle;
                if (atrousPingHandle.IsValid())
                    builder.UseTexture(atrousPingHandle, AccessFlags.ReadWrite);
                if (atrousPongHandle.IsValid())
                    builder.UseTexture(atrousPongHandle, AccessFlags.ReadWrite);

                // Assign the ExecutePass function to the render pass delegate, which will be called by the render graph when executing the pass
                builder.SetRenderFunc(
                    (PassData data, UnsafeGraphContext context) => ExecutePass(data, context)
                );
            }
        }
        #endregion
#endif

        #region Shared
        public void Dispose()
        {
            m_IntermediateCameraColorHandle?.Release();
            m_DiffuseHandle?.Release();
            m_IntermediateDiffuseHandle?.Release();
            m_AccumulateSampleHandle?.Release();
            m_APVLightingHandle?.Release();
            m_AtrousPingHandle?.Release();
            m_AtrousPongHandle?.Release();
            m_AdaptiveTemporalOutputHandle?.Release();

            // Render Graph Pass
            m_HistoryDepthHandle?.Release();
            m_HistoryCameraColorHandle?.Release();
            m_HistoryIndirectDiffuseHandle?.Release();
            m_AccumulateHistorySampleHandle?.Release();

            for (int i = 0; i < cameraHistoryData.Length; ++i)
            {
                cameraHistoryData[i].adaptiveFastHistoryHandle?.Release();
                cameraHistoryData[i].adaptiveFastHistoryHandle = null;
                cameraHistoryData[i].adaptiveMainHistoryHandle?.Release();
                cameraHistoryData[i].adaptiveMainHistoryHandle = null;
                cameraHistoryData[i].adaptiveMomentsHandle?.Release();
                cameraHistoryData[i].adaptiveMomentsHandle = null;
            }

            SpatiotemporalBlueNoise.Dispose();
        }

        Vector4 EvaluateRotator(float rand)
        {
            float ca = Mathf.Cos(rand);
            float sa = Mathf.Sin(rand);
            return new Vector4(ca, sa, -sa, ca);
        }

        // Per Camera History Data
        private struct CameraHistoryData
        {
            public int hash;
            public Matrix4x4 prevCamInvVPMatrix;
            public Vector3 prevCameraPositionWS;
            public float scaledWidth;
            public float scaledHeight;

            // Non Render Graph Pass
            public RTHandle historyDepthHandle;
            public RTHandle historyCameraColorHandle;
            public RTHandle historyIndirectDiffuseHandle;
            public RTHandle accumulateHistorySampleHandle;
            public RTHandle adaptiveFastHistoryHandle;
            public RTHandle adaptiveMainHistoryHandle;
            public RTHandle adaptiveMomentsHandle;
        }

        private const int MAX_CAMERA_COUNT = 4; // must be >= 2
        private readonly CameraHistoryData[] cameraHistoryData = new CameraHistoryData[
            MAX_CAMERA_COUNT
        ];

        private int GetCameraHistoryDataIndex(int cameraHash)
        {
            // Unroll manually for MAX_CAMERA_COUNT = 4
            if (cameraHistoryData[0].hash == cameraHash)
                return 0;
            if (cameraHistoryData[1].hash == cameraHash)
                return 1;
            if (cameraHistoryData[2].hash == cameraHash)
                return 2;
            if (cameraHistoryData[3].hash == cameraHash)
                return 3;
            return -1; // new camera
        }

        private void UpdateCameraHistoryData(bool cameraHashChanged)
        {
            if (cameraHashChanged)
            {
                const int lastIndex = MAX_CAMERA_COUNT - 1;

                // Non Render Graph Pass
                // Release the persistent textures for the last camera
                cameraHistoryData[lastIndex].historyDepthHandle?.Release();
                cameraHistoryData[lastIndex].historyCameraColorHandle?.Release();
                cameraHistoryData[lastIndex].historyIndirectDiffuseHandle?.Release();
                cameraHistoryData[lastIndex].accumulateHistorySampleHandle?.Release();
                cameraHistoryData[lastIndex].adaptiveFastHistoryHandle?.Release();
                cameraHistoryData[lastIndex].adaptiveMainHistoryHandle?.Release();
                cameraHistoryData[lastIndex].adaptiveMomentsHandle?.Release();

                // Shift the camera history data back by one
                Array.Copy(cameraHistoryData, 0, cameraHistoryData, 1, lastIndex);
            }
        }

        private void UpdateReflectionProbe(
            NativeArray<VisibleReflectionProbe> visibleReflectionProbes,
            Vector3 cameraPosition
        )
        {
            if (ssgiVolume.IsFallbackReflectionProbes() && !Shader.IsKeywordEnabled(_FORWARD_PLUS))
            {
                var reflectionProbe = GetClosestProbe(visibleReflectionProbes, cameraPosition);
                if (reflectionProbe != null)
                {
                    m_SSGIMaterial.SetTexture(specCube0, reflectionProbe.texture);
                    m_SSGIMaterial.SetVector(specCube0_HDR, reflectionProbe.textureHDRDecodeValues);
                    bool isBoxProjected = reflectionProbe.boxProjection;
                    if (isBoxProjected)
                    {
                        Vector3 probe0Position = reflectionProbe.transform.position;
                        float probe0Mode = isBoxProjected ? 1.0f : 0.0f;
                        m_SSGIMaterial.SetVector(specCube0_BoxMin, reflectionProbe.bounds.min);
                        m_SSGIMaterial.SetVector(specCube0_BoxMax, reflectionProbe.bounds.max);
                        m_SSGIMaterial.SetVector(
                            specCube0_ProbePosition,
                            new Vector4(
                                probe0Position.x,
                                probe0Position.y,
                                probe0Position.z,
                                probe0Mode
                            )
                        );
                    }
                    m_SSGIMaterial.SetFloat(probeWeight, 0.0f);
                    m_SSGIMaterial.SetFloat(probeSet, 1.0f);
                }
                else
                {
                    m_SSGIMaterial.SetFloat(probeSet, 0.0f);
                }
            }
            else
            {
                m_SSGIMaterial.SetFloat(probeSet, 0.0f);
            }
        }

        private static ReflectionProbe GetClosestProbe(
            NativeArray<VisibleReflectionProbe> visibleReflectionProbes,
            Vector3 cameraPosition
        )
        {
            ReflectionProbe closestProbe = null;
            float closestDistance = float.MaxValue;
            int highestImportance = int.MinValue;
            float smallestBoundsSize = float.MaxValue;

            foreach (var visibleProbe in visibleReflectionProbes)
            {
                ReflectionProbe probe = visibleProbe.reflectionProbe;
                Bounds probeBounds = probe.bounds;
                int probeImportance = probe.importance;
                float boundsSize = probeBounds.size.magnitude;

                if (probeBounds.Contains(cameraPosition))
                {
                    float distance = Vector3.Distance(cameraPosition, probe.transform.position);

                    bool isMoreImportant = probeImportance > highestImportance;
                    bool isSizeSmaller =
                        probeImportance == highestImportance && boundsSize < smallestBoundsSize;
                    bool isDistanceCloser =
                        boundsSize == smallestBoundsSize && distance < closestDistance;

                    // Rules:
                    // 1. Find the probe(s) with highest importance index
                    // 2. Find the probe(s) with a smallest box size
                    // 3. Find the probe(s) with a closer distance to the camera
                    bool isCloserProbe = isMoreImportant || isSizeSmaller || isDistanceCloser;

                    if (isCloserProbe)
                    {
                        closestDistance = distance;
                        highestImportance = probeImportance;
                        smallestBoundsSize = boundsSize;
                        closestProbe = probe;
                    }
                }
            }
            // Returns null if we cannot find a probe
            return closestProbe;
        }
        #endregion
    }
}
