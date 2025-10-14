#ifndef URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_CMJ_HLSL
#define URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_CMJ_HLSL

float2 GenerateCMJSample(uint sampleIndex, uint sampleCount, float2 pixelCoord, uint frameIndex)
{
    sampleCount = max(1u, sampleCount);

    uint2 pixel = (uint2)floor(pixelCoord);
    uint3 seed = uint3(pixel, frameIndex);

    float root = sqrt((float)sampleCount);
    uint m = max(1u, (uint)floor(root));
    uint n = max(1u, (uint)ceil((float)sampleCount / (float)m));

    uint sx = sampleIndex % m;
    uint sy = sampleIndex / m;

    uint permSeedX = Hash32(seed ^ uint3(0x243F6A88u, 0x85A308D3u, 0x13198A2Eu));
    uint permSeedY = Hash32(seed ^ uint3(0x9E3779B9u, 0xBB67AE85u, 0x3C6EF372u));

    sx = Permute(sx, m, permSeedX);
    sy = Permute(sy, n, permSeedY);

    uint jitterSeed = Hash32(seed ^ uint3(sampleIndex, permSeedX, permSeedY));
    float jx = HashToUnitFloat(LowBiasHash(jitterSeed ^ 0xA511E9B3u));
    float jy = HashToUnitFloat(LowBiasHash(jitterSeed ^ 0x63D83595u));

    float invM = rcp((float)m);
    float invN = rcp((float)n);

    float x = (float(sx) + (float(sy) + jx) * invN) * invM;
    float y = (float(sy) + (float(sx) + jy) * invM) * invN;

    return float2(x, y);
}

#endif // URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_CMJ_HLSL
