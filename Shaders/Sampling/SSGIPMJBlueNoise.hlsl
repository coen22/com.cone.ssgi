#ifndef URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_PMJ_BLUE_NOISE_HLSL
#define URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_PMJ_BLUE_NOISE_HLSL

float2 GeneratePMJBlueNoiseSample(uint sampleIndex, uint sampleCount, float2 pixelCoord, uint frameIndex)
{
    float2 pmj = GeneratePMJSample(sampleIndex, sampleCount, pixelCoord, frameIndex);

    float2 blueNoise = SampleScreenBlueNoise(pixelCoord, frameIndex, sampleIndex);
    float diffusionScale = rcp(max(1.0f, sqrt((float)sampleCount)) * 1.5f);
    pmj = frac(pmj + (blueNoise - 0.5f) * diffusionScale);

    return pmj;
}

#endif // URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_PMJ_BLUE_NOISE_HLSL
