using UnityEngine;

namespace Redline;

internal static class BloodMaterial
{
    private const int Tones = 32;
    private static Texture2D palette;
    private static Texture2D surface;

    public static Material CreateCutSurface()
    {
        var shader = Shader.Find("Sprites/Default");
        if (!shader)
            return null;
        if (!palette)
        {
            var pixels = new Color32[Tones];
            for (int x = 0; x < Tones; x++)
            {
                float tone = x / (Tones - 1f);
                var (r, g, b) = BloodTexture.Crimson(1f - 0.46f * tone, 0.14f * (1f - tone), true);
                pixels[x] = new Color32(r, g, b, 255);
            }
            palette = new Texture2D(Tones, 1, TextureFormat.RGBA32, false)
            {
                name = "REDLINE Blood Palette", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear
            };
            palette.SetPixels32(pixels);
            palette.Apply(false, true);
        }
        return new Material(shader) { name = "REDLINE Cut Surface", mainTexture = palette };
    }

    public static Material CreateSurface()
    {
        var shader = Shader.Find("Sprites/Default");
        if (!shader)
            return null;
        if (!surface)
        {
            const int cell = BloodTexture.Cell;
            const int width = cell * BloodTexture.AtlasColumns;
            const int height = cell * BloodTexture.AtlasRows;
            var pixels = new Color32[width * height];
            const int impactStart = 2 * BloodTexture.Variants;
            for (int tile = 0; tile < impactStart + BloodTexture.ImpactVariants; tile++)
            {
                int row = tile / BloodTexture.AtlasColumns * cell;
                int column = tile % BloodTexture.AtlasColumns * cell;
                for (int y = 0; y < cell; y++)
                    for (int x = 0; x < cell; x++)
                    {
                        var (r, g, b, a) = tile >= impactStart ? BloodTexture.ImpactPixel(x, y, tile - impactStart)
                            : BloodTexture.Pixel(x, y, tile % BloodTexture.Variants, tile >= BloodTexture.Variants);
                        pixels[(row + y) * width + column + x] = new Color32(r, g, b, a);
                    }
            }
            surface = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "REDLINE Blood Surface", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear
            };
            surface.SetPixels32(pixels);
            surface.Apply(false, true);
        }
        return new Material(shader) { name = "REDLINE Blood", mainTexture = surface };
    }

    public static Vector2 SurfaceUv(float x, float y, int variant, BloodSurface surface)
    {
        const float padding = 0.5f / BloodTexture.Cell;
        const float span = 1f - 2f * padding;
        int tile = BloodTexture.Tile(surface, variant);
        return new Vector2((tile % BloodTexture.AtlasColumns + padding + x * span) / BloodTexture.AtlasColumns,
            (tile / BloodTexture.AtlasColumns + padding + y * span) / BloodTexture.AtlasRows);
    }

    public static Vector2 CutUv(float tone) => new((0.5f + tone * (Tones - 1)) / Tones, 0.5f);

    public static void Dispose()
    {
        if (palette) Object.Destroy(palette);
        if (surface) Object.Destroy(surface);
        palette = null;
        surface = null;
    }
}
