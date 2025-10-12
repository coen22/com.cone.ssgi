using System;
using UnityEngine;

namespace UnityEngine.Rendering.Universal
{
    internal sealed class SSGIHybridTemporalDenoiser
        : ISSGIDenoiser<SSGIHybridTemporalDenoiser.Settings>
    {
        public struct Settings
        {
            public float MotionThreshold;
        }

        public ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm Algorithm =>
            ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.HybridTemporal;

        public string DisplayName => "Hybrid Temporal";

        public Type SettingsType => typeof(Settings);

        public void UpdateShader(ComputeShader shader) { }

        public bool IsSupported => true;

        public Settings CreateSettings(ScreenSpaceGlobalIlluminationVolume volume)
        {
            if (volume == null)
                return default;

            return new Settings
            {
                MotionThreshold = Mathf.Max(0.0f, volume.hybridMotionThreshold.value),
            };
        }
    }
}
