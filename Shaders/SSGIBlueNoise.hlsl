#ifndef URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_BLUE_NOISE_HLSL
#define URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_BLUE_NOISE_HLSL

#define SSGI_BLUE_NOISE_AVAILABLE 1

uint SSGIPermute(uint value)
{
    value ^= value >> 17;
    value *= 0xed5ad4bbu;
    value ^= value >> 11;
    value *= 0xac4c1b51u;
    value ^= value >> 15;
    value *= 0x31848babu;
    value ^= value >> 14;
    return value;
}

float HashToUnitFloat(uint seed)
{
    return (SSGIPermute(seed) & 0x00FFFFFFu) / 16777216.0f;
}

uint2 ComputeBlueNoiseTileCoord(uint2 pixel, uint frameIndex, uint sequence)
{
    uint tileSize = max((uint)_SSGI_BlueNoiseTextureParams.z, 1u);
    uint2 baseCoord = uint2(pixel.x % tileSize, pixel.y % tileSize);

    uint hash = pixel.x ^ (pixel.y * 0x9E3779B9u);
    hash ^= frameIndex * 0x7f4a7c15u;
    hash ^= sequence * 0x94d049bbu;
    hash = SSGIPermute(hash);

    uint offsetX = hash % tileSize;
    uint offsetY = (hash >> 8) % tileSize;
    return (baseCoord + uint2(offsetX, offsetY)) % tileSize;
}

float2 SampleSpatiotemporalBlueNoise(uint2 pixel, uint frameIndex, uint sequence)
{
    uint sliceCount = (uint)_SSGI_BlueNoiseTextureParams.w;
    if (sliceCount == 0u)
    {
        uint baseSeed = pixel.x ^ (pixel.y * 0x9E3779B9u) ^ (frameIndex * 0x7f4a7c15u) ^ (sequence * 0x94d049bbu);
        return float2(HashToUnitFloat(baseSeed), HashToUnitFloat(baseSeed ^ 0x68bc21ebu));
    }

    uint2 tileCoord = ComputeBlueNoiseTileCoord(pixel, frameIndex, sequence);
    float2 uv = (float2(tileCoord) + 0.5f) * _SSGI_BlueNoiseTextureParams.xy;

    uint slice = (frameIndex + sequence) % sliceCount;
    float4 value = SAMPLE_TEXTURE2D_ARRAY(_SSGI_BlueNoiseTexture, sampler_SSGI_BlueNoiseTexture, uv, slice);

    return saturate(value.rg);
}

#endif // URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_BLUE_NOISE_HLSL
