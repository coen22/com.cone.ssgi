#ifndef URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_UTILITIES_HLSL
#define URP_SCREEN_SPACE_GLOBAL_ILLUMINATION_UTILITIES_HLSL

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/BRDF.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/SpaceTransforms.hlsl"

#if UNITY_VERSION >= 202310
#if defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2)
#include "Packages/com.unity.render-pipelines.core/Runtime/Lighting/ProbeVolume/ProbeVolume.hlsl"

void SSGIEvaluateAdaptiveProbeVolume(in float3 posWS, in half3 normalWS, in half3 viewDir, in float2 positionSS, in uint renderingLayer,
    out half3 bakeDiffuseLighting, out half4 probeOcclusion)
{
    bakeDiffuseLighting = half3(0.0, 0.0, 0.0);

#if UNITY_VERSION >= 202330
    posWS = AddNoiseToSamplingPosition(posWS, positionSS, viewDir);
#else
    posWS = AddNoiseToSamplingPosition(posWS, positionSS);
#endif

#if UNITY_VERSION >= 600000
    APVSample apvSample = SampleAPV(posWS, normalWS, renderingLayer, viewDir);
#else
    APVSample apvSample = SampleAPV(posWS, normalWS, viewDir);
#endif
    
#ifdef USE_APV_PROBE_OCCLUSION
    probeOcclusion = apvSample.probeOcclusion;
#else
    probeOcclusion = 1;
#endif

    EvaluateAdaptiveProbeVolume(apvSample, normalWS, bakeDiffuseLighting);
}

#endif
#endif

#include "./SSGIConfig.hlsl"
#include "./SSGIInput.hlsl"

static const float kTau = 6.28318530718;
static const float kReciprocalUInt = 2.3283064365386963e-10;

#if !defined(SSGI_SAMPLING_HAMMERSLEY_CP) && !defined(SSGI_SAMPLING_R2_CP) \
    && !defined(SSGI_SAMPLING_SOBOL_BLUE_NOISE) \
    && !defined(SSGI_SAMPLING_CMJ) \
    && !defined(SSGI_SAMPLING_PMJ) \
    && !defined(SSGI_SAMPLING_PMJ_BLUE_NOISE) \
    && !defined(SSGI_SAMPLING_SOBOL_BURLEY) \
    && !defined(SSGI_SAMPLING_ORTHOGONAL_ARRAY) \
    && !defined(SSGI_SAMPLING_BLUE_NOISE_DIFFUSION)
#define SSGI_SAMPLING_HAMMERSLEY_CP
#endif

float Hash31(float3 p)
{
    return frac(sin(dot(p, float3(12.9898, 78.233, 37.719))) * 43758.5453);
}

float2 SampleScreenBlueNoise(float2 pixelCoord, uint frameIndex, uint sampleIndex)
{
    float3 seed0 = float3(pixelCoord, float(frameIndex) + float(sampleIndex) * 19.0f);
    float3 seed1 = float3(pixelCoord.yx, float(frameIndex) * 1.37f + float(sampleIndex) * 47.0f);
    return float2(Hash31(seed0), Hash31(seed1));
}

uint SSGIReverseBits32(uint bits)
{
    bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
    bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
    bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
    bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
    bits = (bits << 16) | (bits >> 16);
    return bits;
}

uint LowBiasHash(uint x)
{
    x ^= x >> 17;
    x *= 0xED5AD4BBu;
    x ^= x >> 11;
    x *= 0xAC4C1B51u;
    x ^= x >> 15;
    x *= 0x31848BABu;
    x ^= x >> 14;
    return x;
}

uint Hash32(uint2 v)
{
    uint h = 0x9E3779B9u;
    h = LowBiasHash(v.x ^ h);
    h = LowBiasHash(v.y ^ h);
    return h;
}

uint Hash32(uint3 v)
{
    uint h = 0x9E3779B9u;
    h = LowBiasHash(v.x ^ h);
    h = LowBiasHash(v.y ^ h);
    h = LowBiasHash(v.z ^ h);
    return h;
}

float HashToUnitFloat(uint x)
{
    return (float(x) + 0.5f) * kReciprocalUInt;
}

float2 HashToUnitFloat2(uint3 seed, uint salt0, uint salt1)
{
    uint baseHash = Hash32(seed);
    uint h0 = LowBiasHash(baseHash ^ salt0);
    uint h1 = LowBiasHash(baseHash ^ salt1);
    return float2(HashToUnitFloat(h0), HashToUnitFloat(h1));
}

float2 GenerateCranleyPattersonRotation(float2 pixelCoord, uint frameIndex)
{
    uint2 pixel = (uint2)floor(pixelCoord);
    uint3 seed = uint3(pixel, frameIndex);
    return HashToUnitFloat2(seed, 0x68BC21EBu, 0x02E5BE93u);
}

#include "./Sampling/SSGISamplingCommon.hlsl"
#include "./Sampling/SSGIHammersleyCP.hlsl"
#include "./Sampling/SSGIR2CP.hlsl"
#include "./Sampling/SSGISobolBlueNoise.hlsl"
#include "./Sampling/SSGICMJ.hlsl"
#include "./Sampling/SSGIPMJ.hlsl"
#include "./Sampling/SSGIPMJBlueNoise.hlsl"
#include "./Sampling/SSGISobolBurley.hlsl"
#include "./Sampling/SSGIOA.hlsl"
#include "./Sampling/SSGIBlueNoiseDiffusion.hlsl"

float2 GenerateSequenceSample(uint sampleIndex, uint sampleCount, float2 pixelCoord, uint frameIndex)
{
#if defined(SSGI_SAMPLING_R2_CP)
    return GenerateR2CPSample(sampleIndex, sampleCount, pixelCoord, frameIndex);
#elif defined(SSGI_SAMPLING_SOBOL_BLUE_NOISE)
    return GenerateSobolBlueNoiseSample(sampleIndex, sampleCount, pixelCoord, frameIndex);
#elif defined(SSGI_SAMPLING_CMJ)
    return GenerateCMJSample(sampleIndex, sampleCount, pixelCoord, frameIndex);
#elif defined(SSGI_SAMPLING_PMJ)
    return GeneratePMJSample(sampleIndex, sampleCount, pixelCoord, frameIndex);
#elif defined(SSGI_SAMPLING_PMJ_BLUE_NOISE)
    return GeneratePMJBlueNoiseSample(sampleIndex, sampleCount, pixelCoord, frameIndex);
#elif defined(SSGI_SAMPLING_SOBOL_BURLEY)
    return GenerateSobolBurleySample(sampleIndex, sampleCount, pixelCoord, frameIndex);
#elif defined(SSGI_SAMPLING_ORTHOGONAL_ARRAY)
    return GenerateOrthogonalArraySample(sampleIndex, sampleCount, pixelCoord, frameIndex);
#elif defined(SSGI_SAMPLING_BLUE_NOISE_DIFFUSION)
    return GenerateBlueNoiseDiffusionSample(sampleIndex, sampleCount, pixelCoord, frameIndex);
#else
    return GenerateHammersleyCPSample(sampleIndex, sampleCount, pixelCoord, frameIndex);
#endif
}

void BuildOrthonormalBasis(float3 normal, out float3 tangent, out float3 bitangent)
{
    float3 upVector = (abs(normal.z) < 0.999f) ? float3(0.0f, 0.0f, 1.0f) : float3(0.0f, 1.0f, 0.0f);
    tangent = normalize(cross(upVector, normal));
    bitangent = cross(normal, tangent);
}

float3 TransformSampleToWorld(float3 sampleDir, float3 normal, float3 tangent, float3 bitangent)
{
    return sampleDir.x * tangent + sampleDir.y * bitangent + sampleDir.z * normal;
}

float3 CosineWeightedHemisphere(float2 xi)
{
    float phi = kTau * xi.x;
    float cosTheta = sqrt(saturate(1.0f - xi.y));
    float sinTheta = sqrt(saturate(xi.y));
    return float3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);
}

float3 UniformHemisphere(float2 xi)
{
    float phi = kTau * xi.x;
    float cosTheta = saturate(xi.y);
    float sinTheta = sqrt(saturate(1.0f - cosTheta * cosTheta));
    return float3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);
}

float3 SampleImportanceBiasedDirection(
    float3 normalWS,
    float2 cosineXi,
    float2 uniformXi,
    float2 blueNoise,
    float normalBias,
    float brightness,
    float sampleProgress,
    float3 mainLightDirWS
)
{
    float3 tangent;
    float3 bitangent;
    BuildOrthonormalBasis(normalWS, tangent, bitangent);

    float rotationAngle = (blueNoise.x * 2.0f - 1.0f) * kTau;
    float sRotation = sin(rotationAngle);
    float cRotation = cos(rotationAngle);
    float3 rotatedTangent = normalize(tangent * cRotation + bitangent * sRotation);
    float3 rotatedBitangent = cross(normalWS, rotatedTangent);

    float3 cosineWorld = TransformSampleToWorld(
        CosineWeightedHemisphere(cosineXi),
        normalWS,
        rotatedTangent,
        rotatedBitangent
    );
    float3 uniformWorld = TransformSampleToWorld(
        UniformHemisphere(uniformXi),
        normalWS,
        rotatedTangent,
        rotatedBitangent
    );

    float biasWeight = saturate(normalBias);
    float3 direction = normalize(lerp(uniformWorld, cosineWorld, biasWeight));

    float uniformBlend = saturate((sampleProgress - 0.7f) * 3.33333333f);
    direction = normalize(lerp(direction, uniformWorld, uniformBlend * (1.0f - biasWeight)));

    float3 biasedDirection = direction;
    float lightDirLength = dot(mainLightDirWS, mainLightDirWS);
    if (lightDirLength > 0.0f)
    {
        float3 lightDir = mainLightDirWS * rsqrt(lightDirLength);
        float facing = saturate(dot(normalWS, lightDir));
        float lightBias = saturate(0.15f + brightness * 0.6f) * biasWeight;
        biasedDirection = normalize(lerp(direction, lightDir, lightBias * facing));
    }

    return biasedDirection;
}

void UpdateAmbientSH()
{
    unity_SHAr = ssgi_SHAr;
    unity_SHAg = ssgi_SHAg;
    unity_SHAb = ssgi_SHAb;
    unity_SHBr = ssgi_SHBr;
    unity_SHBg = ssgi_SHBg;
    unity_SHBb = ssgi_SHBb;
    unity_SHC = ssgi_SHC;
}

half3 SSGIEvaluateAmbientProbe(half3 normalWS)
{
    // Linear + constant polynomial terms
    half3 res = SHEvalLinearL0L1(normalWS, ssgi_SHAr, ssgi_SHAg, ssgi_SHAb);

    // Quadratic polynomials
    res += SHEvalLinearL2(normalWS, ssgi_SHBr, ssgi_SHBg, ssgi_SHBb, ssgi_SHC);

    return res;
}

half3 SSGISampleProbeVolumePixel(in float3 absolutePositionWS, in float3 normalWS, in float3 viewDir, in float2 screenUV, out half4 probeOcclusion)
{
    probeOcclusion = 1.0;

#if defined(EVALUATE_SH_VERTEX) || defined(EVALUATE_SH_MIXED)
    return half3(0.0, 0.0, 0.0);
#elif defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2)
    half3 bakedGI;
    if (_EnableProbeVolumes)
    {
        // TODO: get the actual rendering layer
        uint meshRenderingLayer = 0xFFFFFFFF; // RenderingLayerMask.Everything

        SSGIEvaluateAdaptiveProbeVolume(absolutePositionWS, normalWS, viewDir, screenUV * _ScreenSize.xy, meshRenderingLayer, bakedGI, probeOcclusion);
    }
    else
    {
        bakedGI = SSGIEvaluateAmbientProbe(normalWS);
    }
#ifdef UNITY_COLORSPACE_GAMMA
    bakedGI = LinearToSRGB(bakedGI);
#endif
    return bakedGI;
#else
    return half3(0, 0, 0);
#endif
}

half3 SSGIEvaluateAmbientProbeSRGB(half3 normalWS)
{
    half3 res = SSGIEvaluateAmbientProbe(normalWS);
#ifdef UNITY_COLORSPACE_GAMMA
    res = LinearToSRGB(res);
#endif
    return res;
}

#ifndef kDieletricSpec
#define kDieletricSpec half4(0.04, 0.04, 0.04, 1.0 - 0.04) // standard dielectric reflectivity coef at incident angle (= 4%)
#endif

#include "./SSGIFallback.hlsl" // Reflection Probes Sampling

// position  : world space ray origin
// direction : world space ray direction
struct Ray
{
    float3 position;
    half3  direction;
};

// position  : world space hit position
// distance  : distance that ray travels
// ...       : surfaceData of hit position
struct RayHit
{
    float3 position;
    float  distance;
    half3  normal;
    half3  emission;
};

// position : the intersection between Ray and Scene.
// distance : the distance from Ray's starting position to intersection.
// normal   : the normal direction of the intersection.
// ...      : material information from GBuffer.
RayHit InitializeRayHit()
{
    RayHit rayHit;
    rayHit.position = float3(0.0, 0.0, 0.0);
    rayHit.distance = REAL_EPS;
    rayHit.normal = half3(0.0, 0.0, 0.0);
    rayHit.emission = half3(0.0, 0.0, 0.0);
    return rayHit;
}

uint UnpackMaterialFlags(float packedMaterialFlags)
{
    return uint((packedMaterialFlags * 255.0h) + 0.5h);
}

// Generate a random value according to the current noise method.
// Counter is built into the function. (_Seed)
float GenerateRandomValue(float2 screenUV)
{
    _Seed += 1.0;
    return GenerateHashedRandomFloat(uint3(screenUV * _BlitTexture_TexelSize.zw, _FrameIndex + _Seed));
}

// Supports perspective and orthographic projections
float ConvertLinearEyeDepth(float deviceDepth)
{
    UNITY_BRANCH
    if (IsPerspectiveProjection())
        return LinearEyeDepth(deviceDepth, _ZBufferParams);
    else
    {
    #if UNITY_REVERSED_Z
        deviceDepth = 1.0 - deviceDepth;
    #endif
        return lerp(_ProjectionParams.y, _ProjectionParams.z, deviceDepth);
    }

}

void HitSurfaceDataFromGBuffer(float2 screenUV, inout RayHit rayHit, bool isBackHit = false)
{
#if defined(_FOVEATED_RENDERING_NON_UNIFORM_RASTER)
    screenUV = (screenUV * 2.0 - 1.0) * _ScreenSize.zw;
#endif

    
#if _RENDER_PASS_ENABLED // Unused
    half4 gbuffer0 = LOAD_FRAMEBUFFER_INPUT(GBUFFER0, screenUV);
    half4 gbuffer1 = LOAD_FRAMEBUFFER_INPUT(GBUFFER1, screenUV);
    half4 gbuffer2 = LOAD_FRAMEBUFFER_INPUT(GBUFFER2, screenUV);
#else
    // Using SAMPLE_TEXTURE2D is faster than using LOAD_TEXTURE2D on iOS platforms (5% faster shader).
    // Possible reason: HLSLcc upcasts Load() operation to float, which doesn't happen for Sample()?
    half4 gbuffer0 = SAMPLE_TEXTURE2D_X_LOD(_GBuffer0, my_point_clamp_sampler, screenUV, 0);
    half4 gbuffer1 = SAMPLE_TEXTURE2D_X_LOD(_GBuffer1, my_point_clamp_sampler, screenUV, 0);
    half4 gbuffer2 = SAMPLE_TEXTURE2D_X_LOD(_GBuffer2, my_point_clamp_sampler, screenUV, 0);
#endif

    half3 gbuffer3;
    if (!isBackHit || _BackDepthEnabled != 2.0)
    {
        gbuffer3 = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, my_point_clamp_sampler, screenUV, 0).rgb;
        //half3 prevColor = SAMPLE_TEXTURE2D_X_LOD(_HistoryIndirectDiffuseTexture, my_point_clamp_sampler, screenUV, 0).rgb;
        //gbuffer3 += prevColor * gbuffer0.rgb; // (1.0 - metallic)
    }
    else
        gbuffer3 = SAMPLE_TEXTURE2D_X_LOD(_CameraBackOpaqueTexture, my_point_clamp_sampler, screenUV, 0).rgb;
        
    rayHit.normal = gbuffer2.rgb;
        
#if defined(_GBUFFER_NORMALS_OCT)
    half2 remappedOctNormalWS = half2(Unpack888ToFloat2(rayHit.normal));                // values between [ 0, +1]
    half2 octNormalWS = remappedOctNormalWS.xy * half(2.0) - half(1.0);                 // values between [-1, +1]
    rayHit.normal = half3(UnpackNormalOctQuadEncode(octNormalWS));                      // values between [-1, +1]
#endif
    rayHit.normal = isBackHit ? -rayHit.normal : rayHit.normal;

    rayHit.emission = gbuffer3.rgb;
}
#endif
