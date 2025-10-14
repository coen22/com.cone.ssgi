#ifndef URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_SOBOL_BLUE_NOISE_HLSL
#define URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_SOBOL_BLUE_NOISE_HLSL

static const uint SOBOL_DIRECTION_NUMBERS[2][32] =
{
    {
        0x80000000u, 0xC0000000u, 0xA0000000u, 0xF0000000u, 0x88000000u, 0xCC000000u, 0xAA000000u, 0xFF000000u,
        0x80800000u, 0xC0C00000u, 0xA0A00000u, 0xF0F00000u, 0x88880000u, 0xCCCC0000u, 0xAAAA0000u, 0xFFFF0000u,
        0x80008000u, 0xC000C000u, 0xA000A000u, 0xF000F000u, 0x88008800u, 0xCC00CC00u, 0xAA00AA00u, 0xFF00FF00u,
        0x80808080u, 0xC0C0C0C0u, 0xA0A0A0A0u, 0xF0F0F0F0u, 0x88888888u, 0xCCCCCCCCu, 0xAAAAAAAAu, 0xFFFFFFFFu
    },
    {
        0x80000000u, 0xC0000000u, 0x60000000u, 0x90000000u, 0xE8000000u, 0x5C000000u, 0x8E000000u, 0xC5000000u,
        0x68800000u, 0x9CC00000u, 0xEE600000u, 0x55900000u, 0x80680000u, 0xC09C0000u, 0x60EE0000u, 0x90550000u,
        0xE8808000u, 0x5CC0C000u, 0x8E606000u, 0xC5909000u, 0x6868E800u, 0x9C9C5C00u, 0xEEEE8E00u, 0x5555C500u,
        0x8000E880u, 0xC0005CC0u, 0x60008E60u, 0x9000C590u, 0xE8006868u, 0x5C009C9Cu, 0x8E00EEEEu, 0xC5005555u
    }
};

uint SobolBinary(uint index, uint dimension)
{
    uint result = 0u;
    uint i = index;
    uint bit = 0u;
    while (i != 0u)
    {
        if ((i & 1u) != 0u)
        {
            result ^= SOBOL_DIRECTION_NUMBERS[dimension][bit];
        }
        i >>= 1u;
        ++bit;
    }
    return result;
}

uint OwenScramble(uint value, uint seed)
{
    value = SSGIReverseBits32(value);
    value ^= value * 0x3D20ADEAu ^ seed;
    value ^= value * 0x05526C56u ^ seed;
    value ^= value * 0x53A22864u ^ seed;
    return SSGIReverseBits32(value);
}

float SobolSample(uint index, uint dimension, uint scramble)
{
    uint value = SobolBinary(index, dimension);
    value = OwenScramble(value, scramble);
    return HashToUnitFloat(value);
}

uint Permute(uint index, uint length, uint seed)
{
    uint mask = length - 1u;
    mask |= mask >> 1u;
    mask |= mask >> 2u;
    mask |= mask >> 4u;
    mask |= mask >> 8u;
    mask |= mask >> 16u;

    do
    {
        index ^= seed;
        index *= 0xE170893Du;
        index ^= seed >> 16;
        index ^= (index & mask) >> 4;
        index ^= seed >> 8;
        index *= 0x0929EB3Fu;
        index ^= seed >> 23;
        index ^= (index & mask) >> 1;
        index *= 1u | (seed >> 27);
        index *= 0x6935FA69u;
        index ^= (index & mask) >> 11;
        index *= 0x74DCB303u;
        index ^= (index & mask) >> 2;
        index *= 0x9E501CC3u;
        index ^= (index & mask) >> 2;
        index *= 0xC860A3DFu;
        index &= mask;
        index ^= index >> 5;
    }
    while (index >= length);

    return (index + seed) % max(1u, length);
}

float2 GenerateSobolBlueNoiseSample(
    uint sampleIndex,
    uint sampleCount,
    float2 pixelCoord,
    uint frameIndex)
{
    uint2 pixel = (uint2)floor(pixelCoord);
    uint3 seed = uint3(pixel, frameIndex);
    uint permuteSeed = Hash32(seed ^ uint3(0xB5297A4Du, 0x68E31DA4u, 0x1B56C4E9u));
    uint scrambledIndex = Permute(sampleIndex, max(1u, sampleCount), permuteSeed);

    uint scramble0 = LowBiasHash(Hash32(seed ^ uint3(0xCD9E8D57u, 0xD2511F53u, 0x9E3779B9u)));
    uint scramble1 = LowBiasHash(Hash32(seed ^ uint3(0x7F4A7C15u, 0x94D049BBu, 0xF39CC060u)));

    return float2(
        SobolSample(scrambledIndex, 0u, scramble0),
        SobolSample(scrambledIndex, 1u, scramble1)
    );
}

#endif // URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_SOBOL_BLUE_NOISE_HLSL
