using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Rendering.Universal;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

namespace Cone.SSGI
{
    public class BackfaceDataPass : ScriptableRenderPass
    {
        const string m_ProfilerTag = "Render Backface Data";
        private readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler(m_ProfilerTag);

        private RTHandle m_BackDepthHandle;
        private RTHandle m_BackColorHandle;
        public bool backfaceLighting;

        private const string CameraBackDepthTextureName = "_CameraBackDepthTexture";
        private const string CameraBackOpaqueTextureName = "_CameraBackOpaqueTexture";
        private static readonly int cameraBackDepthTexture = Shader.PropertyToID(
            CameraBackDepthTextureName
        );
        private static readonly int cameraBackOpaqueTexture = Shader.PropertyToID(
            CameraBackOpaqueTextureName
        );

        private RenderStateBlock m_DepthRenderStateBlock = new RenderStateBlock(
            RenderStateMask.Nothing
        );
        private readonly ShaderTagId[] m_LitTags = new ShaderTagId[2];

        private const string k_DepthOnly = "DepthOnly";
        private const string k_UniversalForward = "UniversalForward";
        private const string k_UniversalForwardOnly = "UniversalForwardOnly";
        private readonly ShaderTagId depthOnly = new ShaderTagId(k_DepthOnly);
        private readonly ShaderTagId universalForward = new ShaderTagId(k_UniversalForward);
        private readonly ShaderTagId universalForwardOnly = new ShaderTagId(k_UniversalForwardOnly);

        #region Non Render Graph Pass

#if !UNITY_6000_4_OR_NEWER
#if UNITY_6000_0_OR_NEWER
        [Obsolete]
#endif
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var depthDesc = renderingData.cameraData.cameraTargetDescriptor;
            depthDesc.msaaSamples = 1;
            depthDesc.bindMS = false;
            depthDesc.graphicsFormat = GraphicsFormat.None;

            if (!backfaceLighting)
            {
                if (m_BackColorHandle != null)
                {
                    m_BackColorHandle.Release();
                    m_BackColorHandle = null;
                }
#if UNITY_6000_0_OR_NEWER
                RenderingUtils.ReAllocateHandleIfNeeded(
                    ref m_BackDepthHandle,
                    depthDesc,
                    FilterMode.Point,
                    TextureWrapMode.Clamp,
                    name: CameraBackDepthTextureName
                );
#else
                RenderingUtils.ReAllocateIfNeeded(
                    ref m_BackDepthHandle,
                    depthDesc,
                    FilterMode.Point,
                    TextureWrapMode.Clamp,
                    name: CameraBackDepthTextureName
                );
#endif
                cmd.SetGlobalTexture(cameraBackDepthTexture, m_BackDepthHandle);

                ConfigureTarget(m_BackDepthHandle, m_BackDepthHandle);
                ConfigureClear(ClearFlag.Depth, Color.clear);
            }
            else
            {
                var colorDesc = renderingData.cameraData.cameraTargetDescriptor;
                colorDesc.depthStencilFormat = GraphicsFormat.None;
                colorDesc.msaaSamples = 1;
                colorDesc.bindMS = false;
                colorDesc.graphicsFormat = GraphicsFormat.B10G11R11_UFloatPack32;

#if UNITY_6000_0_OR_NEWER
                RenderingUtils.ReAllocateHandleIfNeeded(
                    ref m_BackColorHandle,
                    colorDesc,
                    FilterMode.Point,
                    TextureWrapMode.Clamp,
                    name: CameraBackOpaqueTextureName
                );
                RenderingUtils.ReAllocateHandleIfNeeded(
                    ref m_BackDepthHandle,
                    depthDesc,
                    FilterMode.Point,
                    TextureWrapMode.Clamp,
                    name: CameraBackDepthTextureName
                );
#else
                RenderingUtils.ReAllocateIfNeeded(
                    ref m_BackColorHandle,
                    colorDesc,
                    FilterMode.Point,
                    TextureWrapMode.Clamp,
                    name: CameraBackOpaqueTextureName
                );
                RenderingUtils.ReAllocateIfNeeded(
                    ref m_BackDepthHandle,
                    depthDesc,
                    FilterMode.Point,
                    TextureWrapMode.Clamp,
                    name: CameraBackDepthTextureName
                );
#endif

                cmd.SetGlobalTexture(cameraBackDepthTexture, m_BackDepthHandle);
                cmd.SetGlobalTexture(cameraBackOpaqueTexture, m_BackColorHandle);

                ConfigureTarget(m_BackColorHandle, m_BackDepthHandle);
                ConfigureClear(ClearFlag.Color | ClearFlag.Depth, Color.clear);
            }
        }

#if UNITY_6000_0_OR_NEWER
        [Obsolete]
#endif
        public override void Execute(
            ScriptableRenderContext context,
            ref RenderingData renderingData
        )
        {
            CommandBuffer cmd = CommandBufferPool.Get();

            bool useReversedZBuffer = SystemInfo.usesReversedZBuffer;
            CompareFunction depthCompareFunction = useReversedZBuffer
                ? CompareFunction.LessEqual
                : CompareFunction.GreaterEqual;

            // Render backface depth
            if (!backfaceLighting)
            {
                using (new ProfilingScope(cmd, m_ProfilingSampler))
                {
                    RendererListDesc rendererListDesc = new RendererListDesc(
                        depthOnly,
                        renderingData.cullResults,
                        renderingData.cameraData.camera
                    );
                    m_DepthRenderStateBlock.depthState = new DepthState(
                        true,
                        depthCompareFunction
                    );
                    m_DepthRenderStateBlock.mask |= RenderStateMask.Depth;
                    m_DepthRenderStateBlock.rasterState = new RasterState(CullMode.Front);
                    m_DepthRenderStateBlock.mask |= RenderStateMask.Raster;
                    rendererListDesc.stateBlock = m_DepthRenderStateBlock;
                    rendererListDesc.sortingCriteria = renderingData
                        .cameraData
                        .defaultOpaqueSortFlags;
                    rendererListDesc.renderQueueRange = RenderQueueRange.opaque;
                    RendererList rendererList = context.CreateRendererList(rendererListDesc);

                    cmd.DrawRendererList(rendererList);
                }
            }
            // Render backface depth + color
            else
            {
                using (new ProfilingScope(cmd, m_ProfilingSampler))
                {
                    m_LitTags[0] = universalForward;
                    m_LitTags[1] = universalForwardOnly;

                    RendererListDesc rendererListDesc = new RendererListDesc(
                        m_LitTags,
                        renderingData.cullResults,
                        renderingData.cameraData.camera
                    );
                    m_DepthRenderStateBlock.depthState = new DepthState(
                        true,
                        depthCompareFunction
                    );
                    m_DepthRenderStateBlock.mask |= RenderStateMask.Depth;
                    m_DepthRenderStateBlock.rasterState = new RasterState(CullMode.Front);
                    m_DepthRenderStateBlock.mask |= RenderStateMask.Raster;
                    rendererListDesc.stateBlock = m_DepthRenderStateBlock;
                    rendererListDesc.sortingCriteria = renderingData
                        .cameraData
                        .defaultOpaqueSortFlags;
                    rendererListDesc.renderQueueRange = RenderQueueRange.opaque;
                    RendererList rendererList = context.CreateRendererList(rendererListDesc);

                    cmd.DrawRendererList(rendererList);
                }
            }
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            CommandBufferPool.Release(cmd);
        }
#endif
        #endregion

#if UNITY_6000_0_OR_NEWER
        #region Render Graph Pass
        // This class stores the data needed by the pass, passed as parameter to the delegate function that executes the pass
        private class PassData
        {
            internal RendererListHandle rendererListHandle;
        }

        // This static method is used to execute the pass and passed as the RenderFunc delegate to the RenderGraph render pass
        static void ExecutePass(PassData data, RasterGraphContext context)
        {
            context.cmd.DrawRendererList(data.rendererListHandle);
        }

        // This is where the renderGraph handle can be accessed.
        // Each ScriptableRenderPass can use the RenderGraph handle to add multiple render passes to the render graph
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            // add a raster render pass to the render graph, specifying the name and the data type that will be passed to the ExecutePass function
            using (
                var builder = renderGraph.AddRasterRenderPass<PassData>(
                    m_ProfilerTag,
                    out var passData
                )
            )
            {
                // UniversalResourceData contains all the texture handles used by the renderer, including the active color and depth textures
                // The active color and depth textures are the main color and depth buffers that the camera renders into
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                UniversalRenderingData universalRenderingData =
                    frameData.Get<UniversalRenderingData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                UniversalLightData lightData = frameData.Get<UniversalLightData>();

                bool useReversedZBuffer = SystemInfo.usesReversedZBuffer;
                CompareFunction depthCompareFunction = useReversedZBuffer
                    ? CompareFunction.LessEqual
                    : CompareFunction.GreaterEqual;

                //var depthDesc = cameraData.cameraTargetDescriptor;
                //depthDesc.msaaSamples = 1;
                //depthDesc.bindMS = false;
                //depthDesc.graphicsFormat = GraphicsFormat.None;

                TextureDesc depthDesc;
                if (!resourceData.isActiveTargetBackBuffer)
                {
                    depthDesc = resourceData.activeDepthTexture.GetDescriptor(renderGraph);
                }
                else
                {
                    depthDesc = resourceData.cameraDepthTexture.GetDescriptor(renderGraph);
                    var backBufferInfo = renderGraph.GetRenderTargetInfo(
                        resourceData.backBufferDepth
                    );
                    depthDesc.colorFormat = backBufferInfo.format;
                }
                depthDesc.name = CameraBackDepthTextureName;
                depthDesc.useMipMap = false;
                depthDesc.clearBuffer = true;
                depthDesc.msaaSamples = MSAASamples.None;
                depthDesc.bindTextureMS = false;
                depthDesc.filterMode = FilterMode.Point;
                depthDesc.wrapMode = TextureWrapMode.Clamp;

                //if (resourceData.activeDepthTexture.IsValid())
                //    depthDesc.depthBufferBits = (int)resourceData.activeDepthTexture.GetDescriptor(renderGraph).depthBufferBits;

                // Render backface depth
                if (!backfaceLighting)
                {
                    //TextureHandle backDepthHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, depthDesc, name: CameraBackDepthTextureName, true, FilterMode.Point, TextureWrapMode.Clamp);
                    TextureHandle backDepthHandle = renderGraph.CreateTexture(depthDesc);

                    RendererListDesc rendererListDesc = new RendererListDesc(
                        new ShaderTagId(k_DepthOnly),
                        universalRenderingData.cullResults,
                        cameraData.camera
                    );
                    m_DepthRenderStateBlock.depthState = new DepthState(
                        true,
                        depthCompareFunction
                    );
                    m_DepthRenderStateBlock.mask |= RenderStateMask.Depth;
                    m_DepthRenderStateBlock.rasterState = new RasterState(CullMode.Front);
                    m_DepthRenderStateBlock.mask |= RenderStateMask.Raster;
                    rendererListDesc.stateBlock = m_DepthRenderStateBlock;
                    rendererListDesc.sortingCriteria = cameraData.defaultOpaqueSortFlags;
                    rendererListDesc.renderQueueRange = RenderQueueRange.opaque;

                    passData.rendererListHandle = renderGraph.CreateRendererList(rendererListDesc);

                    // We declare the RendererList we just created as an input dependency to this pass, via UseRendererList()
                    builder.UseRendererList(passData.rendererListHandle);

                    // Set to read & write to avoid texture reusing, since this texture will be used by other passes later.
                    builder.SetRenderAttachmentDepth(backDepthHandle, AccessFlags.ReadWrite);

                    builder.SetGlobalTextureAfterPass(backDepthHandle, cameraBackDepthTexture);

                    // Assign the ExecutePass function to the render pass delegate, which will be called by the render graph when executing the pass
                    builder.SetRenderFunc(
                        (PassData data, RasterGraphContext context) => ExecutePass(data, context)
                    );
                }
                // Render backface depth + color
                else
                {
                    //TextureHandle backDepthHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, depthDesc, name: CameraBackDepthTextureName, true, FilterMode.Point, TextureWrapMode.Clamp);
                    TextureHandle backDepthHandle = renderGraph.CreateTexture(depthDesc);

                    //var colorDesc = cameraData.cameraTargetDescriptor;
                    //colorDesc.msaaSamples = 1;
                    //colorDesc.bindMS = false;
                    //colorDesc.depthStencilFormat = GraphicsFormat.None;
                    //colorDesc.graphicsFormat = GraphicsFormat.B10G11R11_UFloatPack32;

                    var colorDesc = resourceData.cameraColor.GetDescriptor(renderGraph);
                    colorDesc.name = CameraBackOpaqueTextureName;
                    colorDesc.useMipMap = false;
                    colorDesc.clearBuffer = true;
                    colorDesc.msaaSamples = MSAASamples.None;
                    colorDesc.bindTextureMS = false;
                    colorDesc.filterMode = FilterMode.Point;
                    colorDesc.wrapMode = TextureWrapMode.Clamp;
                    colorDesc.colorFormat = GraphicsFormat.B10G11R11_UFloatPack32;

                    //TextureHandle backColorHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, colorDesc, name: CameraBackOpaqueTextureName, true, FilterMode.Point, TextureWrapMode.Clamp);
                    TextureHandle backColorHandle = renderGraph.CreateTexture(colorDesc);

                    m_LitTags[0] = new ShaderTagId(k_UniversalForward);
                    m_LitTags[1] = new ShaderTagId(k_UniversalForwardOnly);

                    RendererListDesc rendererListDesc = new RendererListDesc(
                        m_LitTags,
                        universalRenderingData.cullResults,
                        cameraData.camera
                    );
                    m_DepthRenderStateBlock.depthState = new DepthState(
                        true,
                        depthCompareFunction
                    );
                    m_DepthRenderStateBlock.mask |= RenderStateMask.Depth;
                    m_DepthRenderStateBlock.rasterState = new RasterState(CullMode.Front);
                    m_DepthRenderStateBlock.mask |= RenderStateMask.Raster;
                    rendererListDesc.stateBlock = m_DepthRenderStateBlock;
                    rendererListDesc.sortingCriteria = cameraData.defaultOpaqueSortFlags;
                    rendererListDesc.renderQueueRange = RenderQueueRange.opaque;

                    passData.rendererListHandle = renderGraph.CreateRendererList(rendererListDesc);

                    // We declare the RendererList we just created as an input dependency to this pass, via UseRendererList()
                    builder.UseRendererList(passData.rendererListHandle);

                    builder.SetRenderAttachment(backColorHandle, 0);
                    builder.SetRenderAttachmentDepth(backDepthHandle);

                    builder.SetGlobalTextureAfterPass(backColorHandle, cameraBackOpaqueTexture);
                    builder.SetGlobalTextureAfterPass(backDepthHandle, cameraBackDepthTexture);

                    // Assign the ExecutePass function to the render pass delegate, which will be called by the render graph when executing the pass
                    builder.SetRenderFunc(
                        (PassData data, RasterGraphContext context) => ExecutePass(data, context)
                    );
                }
            }
        }
        #endregion
#endif

        #region Shared
        public void Dispose()
        {
            m_BackDepthHandle?.Release();
            m_BackDepthHandle = null;

            m_BackColorHandle?.Release();
            m_BackColorHandle = null;
        }
        #endregion
    }
}
