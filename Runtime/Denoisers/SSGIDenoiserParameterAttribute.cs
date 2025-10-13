using System;
using Cone.SSGI;

namespace Cone.SSGI.Denoisers
{
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = true)]
    public sealed class SSGIDenoiserParameterAttribute : Attribute
    {
        public ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm Algorithm { get; }
        public string DisplayName { get; }
        public int Order { get; }

        public SSGIDenoiserParameterAttribute(
            ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm algorithm,
            string displayName = null,
            int order = 0
        )
        {
            Algorithm = algorithm;
            DisplayName = displayName;
            Order = order;
        }
    }
}
