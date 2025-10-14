using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using static Cone.SSGI.ScreenSpaceGlobalIlluminationShaderConstants;
using static Cone.SSGI.ScreenSpaceGlobalIlluminationURP;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

namespace Cone.SSGI
{
    public class PreRenderScreenSpaceGlobalIlluminationPass : ScriptableRenderPass
    {
        /// Motion vectors may not render correctly in the scene view
        /// This pass is used to "fix" camera motion vectors to improve scene view denoising
        private const string m_ProfilerTag = "Prepare Screen Space Global Illumination";
        private readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler(m_ProfilerTag);

        public Material m_SSGIMaterial;

        private Matrix4x4 camVPMatrix;
        private Matrix4x4 prevCamVPMatrix;

        // This pass is editor only
        const string _PrevViewProjMatrix = "_PrevViewProjMatrix";
        const string _NonJitteredViewProjMatrix = "_NonJitteredViewProjMatrix";
        public PreRenderScreenSpaceGlobalIlluminationPass() { }

        #region Non Render Graph Pass
#if UNITY_6000_0_OR_NEWER
        [Obsolete]
#endif
        public override void Execute(
            ScriptableRenderContext context,
            ref RenderingData renderingData
        )
        {
            CommandBuffer cmd = CommandBufferPool.Get();
            // Fix scene view motion vectors
            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                cmd.SetGlobalMatrix(_PrevViewProjMatrix, prevCamVPMatrix);
                cmd.SetGlobalMatrix(_NonJitteredViewProjMatrix, camVPMatrix);
                prevCamVPMatrix = camVPMatrix;
                var motionVectorPass = motionVectorPassFieldInfo.GetValue(
                    renderingData.cameraData.renderer
                );
                if (motionVectorPass != null)
                {
                    if (
                        motionVectorColorHandleFieldInfo != null
                        && motionVectorDepthHandleFieldInfo != null
                        && motionVectorColorHandleFieldInfo.GetValue(motionVectorPass)
                            is RTHandle motionColorHandle
                        && motionVectorDepthHandleFieldInfo.GetValue(motionVectorPass)
                            is RTHandle motionDepthHandle
                    )
                    {
                        cmd.SetRenderTarget(motionColorHandle, motionDepthHandle);
                        Blitter.BlitTexture(
                            cmd,
                            motionColorHandle,
                            m_ScaleBias,
                            m_SSGIMaterial,
                            pass: 7
                        );
                    }
                }
            }

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            CommandBufferPool.Release(cmd);
        }

#if UNITY_6000_0_OR_NEWER
        [Obsolete]
#endif
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var cameraData = renderingData.cameraData;
            var camera = cameraData.camera;
            camVPMatrix =
                GL.GetGPUProjectionMatrix(camera.nonJitteredProjectionMatrix, true)
                * cameraData.GetViewMatrix();
            prevCamVPMatrix =
                prevCamVPMatrix == null ? camera.previousViewProjectionMatrix : prevCamVPMatrix;
        }
        #endregion

#if UNITY_6000_0_OR_NEWER
        #region Render Graph Pass
        // This class stores the data needed by the pass, passed as parameter to the delegate function that executes the pass
        private class PassData
        {
            internal Matrix4x4 prevCamVPMatrix;
            internal Matrix4x4 camVPMatrix;
        }

        // This static method is used to execute the pass and passed as the RenderFunc delegate to the RenderGraph render pass
        static void ExecutePass(PassData data, UnsafeGraphContext context)
        {
            CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

            // Fix scene view motion vectors
            cmd.SetGlobalMatrix(_PrevViewProjMatrix, data.prevCamVPMatrix);
            cmd.SetGlobalMatrix(_NonJitteredViewProjMatrix, data.camVPMatrix);
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
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

                var camera = cameraData.camera;
                camVPMatrix =
                    GL.GetGPUProjectionMatrix(camera.nonJitteredProjectionMatrix, true)
                    * cameraData.GetViewMatrix();
                passData.camVPMatrix = camVPMatrix;
                passData.prevCamVPMatrix =
                    prevCamVPMatrix == null ? camera.previousViewProjectionMatrix : prevCamVPMatrix;
                prevCamVPMatrix = camVPMatrix;

                // This pass is editor only
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);

                // Assign the ExecutePass function to the render pass delegate, which will be called by the render graph when executing the pass
                builder.SetRenderFunc(
                    (PassData data, UnsafeGraphContext context) => ExecutePass(data, context)
                );
            }
        }
        #endregion
#endif

        #region Shared
        public void Dispose() { }
        #endregion
    }
}
