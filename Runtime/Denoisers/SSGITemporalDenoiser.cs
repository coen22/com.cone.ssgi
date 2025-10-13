using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

namespace UnityEngine.Rendering.Universal
{
    internal sealed class SSGITemporalDenoiser : ScriptableRenderPass
    {
        public SSGITemporalDenoiser()
        {
            profilingSampler = new ProfilingSampler("SSGI Temporal");
        }

        internal void Dispatch(
            CommandBuffer cmd,
            Material material,
            Vector4 scaleBias,
            RTHandle intermediateDiffuse,
            RTHandle diffuse,
            RTHandle accumulateSample,
            RenderTargetIdentifier[] mrtHandles,
            bool aggressiveDenoise,
            bool secondPass
        )
        {
            if (
                material == null
                || intermediateDiffuse == null
                || diffuse == null
                || accumulateSample == null
            )
            {
                if (intermediateDiffuse != null && diffuse != null)
                    cmd.CopyTexture(intermediateDiffuse, diffuse);
                return;
            }

            cmd.SetRenderTarget(
                accumulateSample,
                RenderBufferLoadAction.Load,
                RenderBufferStoreAction.Store,
                accumulateSample,
                RenderBufferLoadAction.DontCare,
                RenderBufferStoreAction.DontCare
            );

            mrtHandles[0] = diffuse;
            mrtHandles[1] = accumulateSample;
            cmd.SetRenderTarget(mrtHandles, accumulateSample);
            Blitter.BlitTexture(cmd, intermediateDiffuse, scaleBias, material, pass: 2);

            if (aggressiveDenoise)
            {
                Blitter.BlitCameraTexture(cmd, diffuse, intermediateDiffuse, material, pass: 8);
                Blitter.BlitCameraTexture(cmd, intermediateDiffuse, diffuse, material, pass: 8);
            }

            if (secondPass)
            {
                Blitter.BlitCameraTexture(cmd, diffuse, intermediateDiffuse, material, pass: 3);
                Blitter.BlitCameraTexture(cmd, intermediateDiffuse, diffuse, material, pass: 4);
            }
        }

        internal void Execute(
            CommandBuffer cmd,
            Material material,
            Vector4 scaleBias,
            RTHandle intermediateDiffuse,
            RTHandle diffuse,
            RTHandle accumulateSample,
            RenderTargetIdentifier[] mrtHandles,
            bool aggressiveDenoise,
            bool secondPass
        ) => Dispatch(cmd, material, scaleBias, intermediateDiffuse, diffuse, accumulateSample, mrtHandles, aggressiveDenoise, secondPass);

#if UNITY_6000_0_OR_NEWER
        internal void Dispatch(
            CommandBuffer cmd,
            Material material,
            Vector4 scaleBias,
            TextureHandle intermediateDiffuse,
            TextureHandle diffuse,
            TextureHandle accumulateSample,
            RenderTargetIdentifier[] mrtHandles,
            bool aggressiveDenoise,
            bool secondPass
        )
        {
            if (
                material == null
                || !intermediateDiffuse.IsValid()
                || !diffuse.IsValid()
                || !accumulateSample.IsValid()
            )
            {
                if (intermediateDiffuse.IsValid() && diffuse.IsValid())
                    cmd.CopyTexture(intermediateDiffuse, diffuse);
                return;
            }

            cmd.SetRenderTarget(
                accumulateSample,
                RenderBufferLoadAction.Load,
                RenderBufferStoreAction.Store,
                accumulateSample,
                RenderBufferLoadAction.DontCare,
                RenderBufferStoreAction.DontCare
            );

            mrtHandles[0] = diffuse;
            mrtHandles[1] = accumulateSample;
            cmd.SetRenderTarget(mrtHandles, accumulateSample);
            Blitter.BlitTexture(cmd, intermediateDiffuse, scaleBias, material, pass: 2);

            if (aggressiveDenoise)
            {
                Blitter.BlitCameraTexture(cmd, diffuse, intermediateDiffuse, material, pass: 8);
                Blitter.BlitCameraTexture(cmd, intermediateDiffuse, diffuse, material, pass: 8);
            }

            if (secondPass)
            {
                Blitter.BlitCameraTexture(cmd, diffuse, intermediateDiffuse, material, pass: 3);
                Blitter.BlitCameraTexture(cmd, intermediateDiffuse, diffuse, material, pass: 4);
            }
        }

        internal void Execute(
            CommandBuffer cmd,
            Material material,
            Vector4 scaleBias,
            TextureHandle intermediateDiffuse,
            TextureHandle diffuse,
            TextureHandle accumulateSample,
            RenderTargetIdentifier[] mrtHandles,
            bool aggressiveDenoise,
            bool secondPass
        ) => Dispatch(cmd, material, scaleBias, intermediateDiffuse, diffuse, accumulateSample, mrtHandles, aggressiveDenoise, secondPass);
#endif
    }
}
