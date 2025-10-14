#ifndef URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_HAMMERSLEY_CP_HLSL
#define URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_HAMMERSLEY_CP_HLSL

float RadicalInverse_VdC(uint bits)
{
    bits = (bits << 16) | (bits >> 16);
    bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
    bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
    bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
    bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
    return float(bits) * kReciprocalUInt;
}

float2 HammersleySequence(uint index, uint sampleCount)
{
    float invCount = rcp(float(max(1u, sampleCount)));
    return float2((float(index) + 0.5f) * invCount, RadicalInverse_VdC(index));
}

float2 GenerateHammersleyCPSample(uint sampleIndex, uint sampleCount, float2 pixelCoord, uint frameIndex)
{
    float2 cpRotation = GenerateCranleyPattersonRotation(pixelCoord, frameIndex);
    float2 baseSample = HammersleySequence(sampleIndex, sampleCount);
    return frac(baseSample + cpRotation);
}

#endif // URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_HAMMERSLEY_CP_HLSL
