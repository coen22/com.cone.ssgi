using UnityEngine;
using UnityEngine.Rendering;

namespace Cone.SSGI
{
/// <summary>
/// Generates a small spatiotemporal blue-noise tile used to decorrelate SSGI sampling.
/// The texture is created on demand and kept in memory for the lifetime of the feature.
/// </summary>
internal static class SpatiotemporalBlueNoise
{
    private const int k_TileSize = 8;
    private const int k_SliceCount = 64;
    private const float k_JitterScale = 0.25f;

    private static readonly int[] k_Bayer8x8 =
    {
        0,
        48,
        12,
        60,
        3,
        51,
        15,
        63,
        32,
        16,
        44,
        28,
        35,
        19,
        47,
        31,
        8,
        56,
        4,
        52,
        11,
        59,
        7,
        55,
        40,
        24,
        36,
        20,
        43,
        27,
        39,
        23,
        2,
        50,
        14,
        62,
        1,
        49,
        13,
        61,
        34,
        18,
        46,
        30,
        33,
        17,
        45,
        29,
        10,
        58,
        6,
        54,
        9,
        57,
        5,
        53,
        42,
        26,
        38,
        22,
        41,
        25,
        37,
        21,
    };

    private static Texture2DArray s_Texture;
    private static Vector4 s_TextureParams;

    public static Texture2DArray Texture
    {
        get
        {
            if (s_Texture == null)
                CreateTexture();

            return s_Texture;
        }
    }

    public static Vector4 TextureParams
    {
        get
        {
            if (s_Texture == null)
                CreateTexture();

            return s_TextureParams;
        }
    }

    public static void Dispose()
    {
        if (s_Texture != null)
        {
            CoreUtils.Destroy(s_Texture);
            s_Texture = null;
        }
    }

    private static void CreateTexture()
    {
        s_Texture = new Texture2DArray(
            k_TileSize,
            k_TileSize,
            k_SliceCount,
            TextureFormat.RGHalf,
            false,
            true
        )
        {
            name = "SSGI_BlueNoise",
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Point,
            anisoLevel = 0,
        };

        var colors = new Color[k_TileSize * k_TileSize];
        int tileArea = k_TileSize * k_TileSize;

        for (int slice = 0; slice < k_SliceCount; ++slice)
        {
            int offsetR = (slice * 7) % tileArea;
            int offsetG = (slice * 11) % tileArea;

            for (int i = 0; i < tileArea; ++i)
            {
                int ranked = k_Bayer8x8[i];

                float baseValue = BlueNoiseValue(ranked, offsetR, slice);
                float secondaryValue = BlueNoiseValue(ranked, offsetG, slice * 3 + 17);

                colors[i] = new Color(baseValue, secondaryValue, 0f, 0f);
            }

            s_Texture.SetPixels(colors, slice);
        }

        s_Texture.Apply(false, true);
        s_TextureParams = new Vector4(1f / k_TileSize, 1f / k_TileSize, k_TileSize, k_SliceCount);
    }

    private static float BlueNoiseValue(int baseRank, int offset, int jitterSeed)
    {
        int tileArea = k_TileSize * k_TileSize;
        int wrappedRank = (baseRank + offset) % tileArea;
        float uniform = (wrappedRank + 0.5f) / tileArea;
        float jitter = Hash01(jitterSeed ^ wrappedRank) * k_JitterScale / tileArea;
        return Mathf.Repeat(uniform + jitter, 1f);
    }

    private static float Hash01(int seed)
    {
        unchecked
        {
            uint state = (uint)seed;
            state ^= state >> 17;
            state *= 0xed5ad4bbu;
            state ^= state >> 11;
            state *= 0xac4c1b51u;
            state ^= state >> 15;
            state *= 0x31848babu;
            state ^= state >> 14;
            return (state & 0x00FFFFFFu) / 16777216f;
        }
    }
}
