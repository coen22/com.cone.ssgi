using UnityEngine;
using UnityEngine.Rendering;

namespace Cone.SSGI.Sampling
{
    /// <summary>
    /// Represents a strategy that configures shader keywords and constants for a sampling sequence.
    /// </summary>
    internal interface ISSGISamplingStrategy
    {
        /// <summary>
        /// Gets the sampling sequence identifier used by the volume component.
        /// </summary>
        ScreenSpaceGlobalIlluminationVolume.SamplingSequence Id { get; }

        /// <summary>
        /// Applies strategy specific material configuration and activates the correct keyword.
        /// </summary>
        /// <param name="material">The material used for the SSGI pass.</param>
        /// <param name="volume">The active SSGI volume settings.</param>
        /// <param name="enabled">Whether the strategy should be enabled.</param>
        void Apply(Material material, ScreenSpaceGlobalIlluminationVolume volume, bool enabled);
    }

    /// <summary>
    /// Base class for sampling strategies that only need to enable or disable shader keywords.
    /// </summary>
    internal abstract class SSGISamplingStrategyBase : ISSGISamplingStrategy
    {
        protected SSGISamplingStrategyBase(
            ScreenSpaceGlobalIlluminationVolume.SamplingSequence id,
            string keyword
        )
        {
            Id = id;
            m_Keyword = keyword;
        }

        public ScreenSpaceGlobalIlluminationVolume.SamplingSequence Id { get; }

        private readonly string m_Keyword;
        private LocalKeyword m_LocalKeyword;
        private bool m_IsKeywordInitialized;

        public void Apply(
            Material material,
            ScreenSpaceGlobalIlluminationVolume volume,
            bool enabled
        )
        {
            if (material == null)
                return;

            if (!m_IsKeywordInitialized)
            {
                m_LocalKeyword = new LocalKeyword(material.shader, m_Keyword);
                m_IsKeywordInitialized = true;
            }

            material.SetKeyword(m_LocalKeyword, enabled);

            if (enabled)
                ConfigureMaterial(material, volume);
        }

        protected virtual void ConfigureMaterial(
            Material material,
            ScreenSpaceGlobalIlluminationVolume volume
        )
        {
        }
    }

    internal sealed class HammersleyCranleyPattersonSamplingStrategy : SSGISamplingStrategyBase
    {
        public HammersleyCranleyPattersonSamplingStrategy()
            : base(
                ScreenSpaceGlobalIlluminationVolume.SamplingSequence.HammersleyCranleyPatterson,
                "SSGI_SAMPLING_HAMMERSLEY_CP"
            )
        {
        }
    }

    internal sealed class R2CranleyPattersonSamplingStrategy : SSGISamplingStrategyBase
    {
        public R2CranleyPattersonSamplingStrategy()
            : base(
                ScreenSpaceGlobalIlluminationVolume.SamplingSequence.R2CranleyPatterson,
                "SSGI_SAMPLING_R2_CP"
            )
        {
        }
    }

    internal sealed class OwenScrambledSobolSamplingStrategy : SSGISamplingStrategyBase
    {
        public OwenScrambledSobolSamplingStrategy()
            : base(
                ScreenSpaceGlobalIlluminationVolume.SamplingSequence.OwenScrambledSobolBlueNoise,
                "SSGI_SAMPLING_SOBOL_BLUE_NOISE"
            )
        {
        }
    }

    internal sealed class CorrelatedMultiJitteredSamplingStrategy : SSGISamplingStrategyBase
    {
        public CorrelatedMultiJitteredSamplingStrategy()
            : base(
                ScreenSpaceGlobalIlluminationVolume.SamplingSequence.CorrelatedMultiJittered,
                "SSGI_SAMPLING_CMJ"
            )
        {
        }
    }

    internal sealed class ProgressiveMultiJitteredSamplingStrategy : SSGISamplingStrategyBase
    {
        public ProgressiveMultiJitteredSamplingStrategy()
            : base(
                ScreenSpaceGlobalIlluminationVolume.SamplingSequence.ProgressiveMultiJittered,
                "SSGI_SAMPLING_PMJ"
            )
        {
        }
    }

    internal sealed class ProgressiveMultiJitteredBlueNoiseSamplingStrategy : SSGISamplingStrategyBase
    {
        public ProgressiveMultiJitteredBlueNoiseSamplingStrategy()
            : base(
                ScreenSpaceGlobalIlluminationVolume.SamplingSequence.ProgressiveMultiJitteredBlueNoise,
                "SSGI_SAMPLING_PMJ_BLUE_NOISE"
            )
        {
        }
    }

    internal sealed class SobolBurleySamplingStrategy : SSGISamplingStrategyBase
    {
        public SobolBurleySamplingStrategy()
            : base(
                ScreenSpaceGlobalIlluminationVolume.SamplingSequence.SobolBurley,
                "SSGI_SAMPLING_SOBOL_BURLEY"
            )
        {
        }
    }

    internal sealed class OrthogonalArraySamplingStrategy : SSGISamplingStrategyBase
    {
        public OrthogonalArraySamplingStrategy()
            : base(
                ScreenSpaceGlobalIlluminationVolume.SamplingSequence.OrthogonalArray,
                "SSGI_SAMPLING_ORTHOGONAL_ARRAY"
            )
        {
        }
    }

    internal sealed class BlueNoiseDiffusionSamplingStrategy : SSGISamplingStrategyBase
    {
        public BlueNoiseDiffusionSamplingStrategy()
            : base(
                ScreenSpaceGlobalIlluminationVolume.SamplingSequence.ScreenSpaceBlueNoiseDiffusion,
                "SSGI_SAMPLING_BLUE_NOISE_DIFFUSION"
            )
        {
        }
    }
}
