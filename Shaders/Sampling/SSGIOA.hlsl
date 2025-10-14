#ifndef URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_ORTHOGONAL_ARRAY_HLSL
#define URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_ORTHOGONAL_ARRAY_HLSL

float2 GenerateOrthogonalArraySample(uint sampleIndex, uint sampleCount, float2 pixelCoord, uint frameIndex)
{
    sampleCount = max(1u, sampleCount);

    uint2 pixel = (uint2)floor(pixelCoord);
    uint3 seed = uint3(pixel, frameIndex);

    uint side = max(1u, (uint)ceil(sqrt((float)sampleCount)));
    uint cellX = sampleIndex % side;
    uint cellY = sampleIndex / side;

    uint permSeedX = Hash32(seed ^ uint3(0xB7E15162u, 0xBF715880u, 0x9E3779B9u));
    uint permSeedY = Hash32(seed ^ uint3(0x3C6EF372u, 0xA54FF53Au, 0x510E527Fu));

    cellX = Permute(cellX, side, permSeedX);
    cellY = Permute(cellY, side, permSeedY);

    uint oaOrder = min(4u, side);
    uint patternSeed = Hash32(seed ^ uint3(sampleIndex, permSeedX, permSeedY));
    uint pattern = Permute(sampleIndex, sampleCount, patternSeed);

    uint oaX = pattern % oaOrder;
    uint oaY = (pattern / oaOrder) % oaOrder;

    float jitterX = HashToUnitFloat(LowBiasHash(patternSeed ^ 0xA511E9B3u));
    float jitterY = HashToUnitFloat(LowBiasHash(patternSeed ^ 0x63D83595u));

    float invSide = rcp((float)side);
    float invOA = rcp((float)oaOrder);

    float x = (float(cellX) + (float(oaX) + jitterX) * invOA) * invSide;
    float y = (float(cellY) + (float(oaY) + jitterY) * invOA) * invSide;

    return float2(x, y);
}

#endif // URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_ORTHOGONAL_ARRAY_HLSL
