#ifndef URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_SOBOL_BURLEY_HLSL
#define URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_SOBOL_BURLEY_HLSL

uint OwenScrambleBurley(uint value, uint seed)
{
    value ^= value * 0x3D20ADEAu + seed;
    value ^= value * 0x05526C56u + seed;
    value ^= value * 0x53A22864u + seed;
    value ^= value * 0x0019660Du + seed;
    return value;
}

float SobolBurleySample(uint index, uint dimension, uint scramble)
{
    uint value = SobolBinary(index, dimension);
    value = OwenScrambleBurley(value, scramble);
    return HashToUnitFloat(value);
}

float2 GenerateSobolBurleySample(uint sampleIndex, uint sampleCount, float2 pixelCoord, uint frameIndex)
{
    sampleCount = max(1u, sampleCount);

    uint2 pixel = (uint2)floor(pixelCoord);
    uint3 seed = uint3(pixel, frameIndex);

    uint permuteSeed = Hash32(seed ^ uint3(0x51633E2Du, 0xA24BAEDBu, 0x9E3779B9u));
    uint scrambledIndex = Permute(sampleIndex, sampleCount, permuteSeed);

    uint scramble0 = LowBiasHash(Hash32(seed ^ uint3(0xCD9E8D57u, 0xD2511F53u, 0x9E3779B9u)));
    uint scramble1 = LowBiasHash(Hash32(seed ^ uint3(0x7F4A7C15u, 0x94D049BBu, 0xF39CC060u)));

    float2 result = float2(
        SobolBurleySample(scrambledIndex, 0u, scramble0),
        SobolBurleySample(scrambledIndex, 1u, scramble1)
    );

    return result;
}

#endif // URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_SOBOL_BURLEY_HLSL
