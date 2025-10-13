using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Minimal NRD-inspired denoiser scaffold. The temporal/spatial logic is intentionally
    /// /// lightweight for now and can be expanded toward full NRD parity.
    /// </summary>
    internal sealed class NRDDenoiser : ScriptableRenderPass, ISSGIDenoiser<NRDDenoiser.Settings>
    {
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

            public RenderTargetIdentifier HistoryColor;
            public RenderTargetIdentifier HistoryMoments;
            public RenderTargetIdentifier HistoryFast;

            public bool EnableTemporal;
        }

        public NRDDenoiser()
        {
            profilingSampler = new ProfilingSampler("SSGI NRD");
        }

        public ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm Algorithm =>
            ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.NRD;

        public string DisplayName => "NRD";

        public Type SettingsType => typeof(Settings);

        public void UpdateShader(ComputeShader shader) { }

        public bool IsSupported => true;

        public Settings CreateSettings(ScreenSpaceGlobalIlluminationVolume volume)
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

        internal bool Dispatch(CommandBuffer cmd, in Settings settings, in ResourceSet resources)
        {
            if (cmd == null)
                return false;

            if (resources.Width <= 0 || resources.Height <= 0)
                return false;

            if (resources.Source == resources.Destination)
                return true;

            // Placeholder implementation: simply copy the source to destination.
            // Temporal/spatial operations will be added in follow-up work.
            cmd.CopyTexture(resources.Source, resources.Destination);
            return true;
        }

        internal bool Execute(CommandBuffer cmd, in Settings settings, in ResourceSet resources) => Dispatch(cmd, in settings, in resources);
    }
}
