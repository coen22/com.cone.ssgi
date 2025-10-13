using System;
using UnityEngine.Rendering.Universal;
using Cone.SSGI;

namespace Cone.SSGI.Denoisers
{
    public abstract class ISSGIDenoiser : ScriptableRenderPass
    {
        public abstract ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm Algorithm { get; }
        public abstract string DisplayName { get; }
        public abstract Type SettingsType { get; }
        public abstract bool IsSupported { get; }
        public virtual bool IsDefaultVariant => true;
        internal abstract void Configure(ScreenSpaceGlobalIlluminationURP feature);

        internal virtual void ConfigurePass(
            ScreenSpaceGlobalIlluminationURP feature,
            ScreenSpaceGlobalIlluminationPass pass,
            ScreenSpaceGlobalIlluminationVolume volume
        )
        {
        }
    }

    public abstract class ISSGIDenoiser<TSettings> : ISSGIDenoiser where TSettings : struct
    {
        public abstract TSettings CreateSettings(ScreenSpaceGlobalIlluminationVolume volume);
    }
}
