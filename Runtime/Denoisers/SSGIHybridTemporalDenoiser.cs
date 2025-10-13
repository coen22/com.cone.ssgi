using System;
using UnityEngine;
using Cone.SSGI;

namespace Cone.SSGI.Denoisers
{
    internal sealed class SSGIHybridTemporalDenoiser
        : ISSGIDenoiser<SSGIHybridTemporalDenoiser.Settings>
    {
        public struct Settings
        {
            public float MotionThreshold;
        }

        public SSGIHybridTemporalDenoiser()
        {
            profilingSampler = new UnityEngine.Rendering.ProfilingSampler("SSGI Hybrid Temporal");
        }

        public override ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm Algorithm =>
            ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm.HybridTemporal;

        public override string DisplayName => "Hybrid Temporal";

        public override Type SettingsType => typeof(Settings);

        internal override void Configure(ScreenSpaceGlobalIlluminationURP feature) { }

        public override bool IsSupported => true;

        public override Settings CreateSettings(ScreenSpaceGlobalIlluminationVolume volume)
        {
            if (volume == null)
                return default;

            return new Settings
            {
                MotionThreshold = Mathf.Max(0.0f, volume.hybridMotionThreshold.value),
            };
        }

        internal override void ConfigurePass(
            ScreenSpaceGlobalIlluminationURP feature,
            ScreenSpaceGlobalIlluminationPass pass,
            ScreenSpaceGlobalIlluminationVolume volume
        )
        {
            pass.hybridTemporalDenoiser = this;
            var spatial = feature.AcquireDenoiser<SSGISpatialSingleFrameDenoiser>();
            spatial?.ConfigurePass(feature, pass, volume);
        }
    }
}
