#ifndef URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_BLUE_NOISE_DIFFUSION_HLSL
#define URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_BLUE_NOISE_DIFFUSION_HLSL

float2 ApplyScreenSpaceDiffusion(float2 sample, float2 pixelCoord, uint frameIndex, uint sampleIndex)
{
    float2 diffused = sample;

    for (uint i = 0u; i < 3u; ++i)
    {
        float2 bn = SampleScreenBlueNoise(pixelCoord + (float2(i, i ^ 1u) - 1.0f) * 1.37f, frameIndex + i, sampleIndex + i);
        float strength = 0.35f / (float(i) + 1.0f);
        diffused = frac(diffused + (bn - 0.5f) * strength);
    }

    return diffused;
}

float2 GenerateBlueNoiseDiffusionSample(uint sampleIndex, uint sampleCount, float2 pixelCoord, uint frameIndex)
{
    float2 sobol = GenerateSobolBlueNoiseSample(sampleIndex, sampleCount, pixelCoord, frameIndex);
    return ApplyScreenSpaceDiffusion(sobol, pixelCoord, frameIndex, sampleIndex);
}

#endif // URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_SAMPLING_BLUE_NOISE_DIFFUSION_HLSL
