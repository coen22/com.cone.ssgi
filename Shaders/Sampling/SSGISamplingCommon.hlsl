#ifndef URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_COMMON_HLSL
#define URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_COMMON_HLSL

uint Permute(uint index, uint length, uint seed)
{
    length = max(1u, length);

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

#endif // URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_COMMON_HLSL
