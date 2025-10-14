#ifndef URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_PMJ_HLSL
#define URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_PMJ_HLSL

float2 GeneratePMJSample(uint sampleIndex, uint sampleCount, float2 pixelCoord, uint frameIndex)
{
    sampleCount = max(1u, sampleCount);

    uint2 pixel = (uint2)floor(pixelCoord);
    uint3 seed = uint3(pixel, frameIndex);

    float root = sqrt((float)sampleCount);
    uint m = max(1u, (uint)floor(root));
    uint n = max(1u, (uint)ceil((float)sampleCount / (float)m));

    uint sx = sampleIndex % m;
    uint sy = sampleIndex / m;

    uint permSeed0 = Hash32(seed ^ uint3(0xF1BBCDC8u, 0x5BE0CD19u, 0x9E3779B9u));
    uint permSeed1 = Hash32(seed ^ uint3(0x243F6A88u, 0x13198A2Eu, 0xA4093822u));
    uint permSeed2 = Hash32(seed ^ uint3(0xC0EF1F5Bu, 0xE94F7E8Cu, 0x94D049BBu));

    sx = Permute(sx, m, permSeed0);
    sy = Permute(sy, n, permSeed1);

    uint permutedIndex = Permute(sampleIndex, sampleCount, permSeed2);
    uint tx = permutedIndex % n;
    uint ty = permutedIndex / n;

    uint jitterSeed = Hash32(seed ^ uint3(permutedIndex, permSeed0, permSeed1));
    float jx = HashToUnitFloat(LowBiasHash(jitterSeed ^ 0xA511E9B3u));
    float jy = HashToUnitFloat(LowBiasHash(jitterSeed ^ 0x63D83595u));

    float invM = rcp((float)m);
    float invN = rcp((float)n);

    float x = (float(sx) + (float(tx) + jx) * invN) * invM;
    float y = (float(sy) + (float(ty) + jy) * invM) * invN;

    return float2(x, y);
}

#endif // URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_PMJ_HLSL
