using System;
using System.Collections.Generic;
using Cone.SSGI;

namespace Cone.SSGI.Denoisers
{
    internal static class DenoisersExtensions
    {
        private static readonly Dictionary<Type, ISSGIDenoiser> s_SharedDenoisers = new();
        private static readonly object s_SharedDenoisersLock = new();

        private static readonly Lazy<Dictionary<Type, Func<ISSGIDenoiser>>> s_DenoiserFactories =
            new Lazy<Dictionary<Type, Func<ISSGIDenoiser>>>(DiscoverDenoiserFactories);

        private static readonly Lazy<Dictionary<ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm, Type>>
            s_AlgorithmLookup =
                new Lazy<Dictionary<ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm, Type>>(
                    BuildAlgorithmLookup
                );

        internal static bool TryGetFactory(Type type, out Func<ISSGIDenoiser> factory) =>
            s_DenoiserFactories.Value.TryGetValue(type, out factory);

        internal static Type GetDenoiserType(ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm algorithm) =>
            s_AlgorithmLookup.Value.TryGetValue(algorithm, out Type type) ? type : null;

        internal static IReadOnlyList<ISSGIDenoiser> GetAllDenoisers()
        {
            var factories = s_DenoiserFactories.Value;
            var instances = new List<ISSGIDenoiser>(factories.Count);

            foreach (Func<ISSGIDenoiser> factory in factories.Values)
            {
                ISSGIDenoiser instance = factory();
                if (instance != null)
                    instances.Add(instance);
            }

            return instances;
        }

        private static Dictionary<Type, Func<ISSGIDenoiser>> DiscoverDenoiserFactories()
        {
            var assembly = typeof(ISSGIDenoiser).Assembly;
            var result = new Dictionary<Type, Func<ISSGIDenoiser>>();

            foreach (Type type in assembly.GetTypes())
            {
                if (type.IsAbstract || !typeof(ISSGIDenoiser).IsAssignableFrom(type))
                    continue;

                if (type.GetConstructor(Type.EmptyTypes) == null)
                    continue;

                result[type] = () => (ISSGIDenoiser)Activator.CreateInstance(type);
            }

            return result;
        }

        private static Dictionary<ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm, Type> BuildAlgorithmLookup()
        {
            var lookup = new Dictionary<ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm, Type>();
            var factories = s_DenoiserFactories.Value;

            foreach (KeyValuePair<Type, Func<ISSGIDenoiser>> pair in factories)
            {
                ISSGIDenoiser instance = pair.Value();
                var algorithm = instance.Algorithm;

                if (!lookup.TryGetValue(algorithm, out Type existingType))
                {
                    lookup[algorithm] = pair.Key;
                }
                else
                {
                    ISSGIDenoiser existingInstance = factories[existingType]();
                    if (!existingInstance.IsDefaultVariant && instance.IsDefaultVariant)
                        lookup[algorithm] = pair.Key;
                }
            }

            return lookup;
        }

        internal static ISSGIDenoiser AcquireDenoiser(
            this ScreenSpaceGlobalIlluminationURP feature,
            ScreenSpaceGlobalIlluminationVolume.DenoiserAlgorithm algorithm
        )
        {
            Type type = GetDenoiserType(algorithm);
            return feature.AcquireDenoiser(type);
        }

        internal static T AcquireDenoiser<T>(this ScreenSpaceGlobalIlluminationURP feature)
            where T : ISSGIDenoiser
        {
            return feature.AcquireDenoiser(typeof(T)) as T;
        }

        internal static ISSGIDenoiser AcquireDenoiser(
            this ScreenSpaceGlobalIlluminationURP feature,
            Type type
        )
        {
            if (type == null)
                return null;

            if (!TryGetFactory(type, out Func<ISSGIDenoiser> factory))
                return null;

            ISSGIDenoiser denoiser;

            lock (s_SharedDenoisersLock)
            {
                if (!s_SharedDenoisers.TryGetValue(type, out denoiser) || denoiser == null)
                {
                    denoiser = factory();
                    s_SharedDenoisers[type] = denoiser;
                }
            }

            feature.ActiveDenoiser = denoiser;
            denoiser.Configure(feature);
            return denoiser;
        }
    }
}
