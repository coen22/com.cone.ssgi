#ifndef URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_R2_CP_HLSL
#define URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_R2_CP_HLSL

static const float kGoldenRatioConjugate = 0.6180339887498948482;
static const float kGoldenRatioConjugateSquared = 0.3819660112501051518;

float2 R2Sequence(uint index)
{
    float n = float(index) + 0.5f;
    return frac(float2(n * kGoldenRatioConjugate, n * kGoldenRatioConjugateSquared));
}

float2 GenerateR2CPSample(uint sampleIndex, uint sampleCount, float2 pixelCoord, uint frameIndex)
{
    float2 cpRotation = GenerateCranleyPattersonRotation(pixelCoord, frameIndex);
    float2 baseSample = R2Sequence(sampleIndex);
    return frac(baseSample + cpRotation);
}

#endif // URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_R2_CP_HLSL
