using System;

namespace UnityEngine.Rendering.Universal
{
    public interface ISSGIDenoiser
    {
        ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm Algorithm { get; }
        string DisplayName { get; }
        Type SettingsType { get; }
        bool IsSupported { get; }
        void UpdateShader(UnityEngine.ComputeShader shader);
    }

    public interface ISSGIDenoiser<TSettings> : ISSGIDenoiser where TSettings : struct
    {
        TSettings CreateSettings(ScreenSpaceGlobalIlluminationVolume volume);
    }
}
