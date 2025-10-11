using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEngine.Rendering.Universal
{
    internal sealed class SSGITemporalDenoiser
    {
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
    }
}
