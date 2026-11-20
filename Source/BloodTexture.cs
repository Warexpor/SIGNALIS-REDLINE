using System;

namespace Redline;

internal enum BloodSurface { Drop, Pool, Impact }

internal static class BloodTexture
{
    public const int Cell = 48;
    public const int Variants = 4;
    public const int ImpactVariants = 16;
    public const int AtlasColumns = 8;
    public const int AtlasRows = (2 * Variants + ImpactVariants) / AtlasColumns;

    public static int Tile(BloodSurface surface, int variant) => surface switch
    {
        BloodSurface.Drop => variant,
        BloodSurface.Pool => Variants + variant,
        _ => 2 * Variants + variant,
    };

    public static float PoolRadius(float progress) => 0.075f + 0.925f * Smooth(0f, 1f, progress);

    public static (byte R, byte G, byte B, byte A) Pixel(int x, int y, int variant, bool pool)
    {
        float u = (x + 0.5f) / Cell * 2f - 1f;
        float v = (y + 0.5f) / Cell * 2f - 1f;
        float radius = (float)Math.Sqrt(u * u + v * v);
        float grain = Noise(x, y, variant);
        float ripple = 0.5f + 0.5f * (float)Math.Sin(10f * u + 4.5f * v + variant * 1.9f
            + Math.Sin(v * 7f - u * 3f));
        float rim = Smooth(0.64f, 0.96f, radius + (ripple - 0.5f) * 0.12f);
        float thinEdge = Smooth(0.82f, 1.09f, radius + (grain - 0.5f) * 0.16f);
        float sheenLine = Math.Abs(v - (0.21f + 0.26f * u * u + 0.045f * (float)Math.Sin(5f * u + variant)));
        float sheen = (1f - Smooth(0.025f, 0.095f, sheenLine))
            * (1f - Smooth(0.34f, 0.81f, Math.Abs(u))) * Smooth(0.50f, 0.93f, ripple);
        sheen *= pool ? 1f : 0.23f;
        float body = 0.93f + ripple * 0.11f + (grain - 0.5f) * (pool ? 0.18f : 0.25f);
        float depth = 1f - rim * (pool ? 0.45f : 0.29f);
        float opacity = ((pool ? 0.91f : 0.82f) + (grain - 0.5f) * 0.10f) * (1f - thinEdge * 0.88f);
        var (red, green, blue) = Crimson(body * depth, sheen, pool);
        return (red, green, blue, Byte(255f * opacity));
    }

    public static (byte R, byte G, byte B, byte A) ImpactPixel(int x, int y, int variant)
    {
        float u = (x + 0.5f) / Cell * 2f - 1f;
        float v = (y + 0.5f) / Cell * 2f - 1f;
        int family = variant / Variants;
        // Dense blot, jagged burst, narrow streak, or scattered drops.
        var (width, height, size, raggedness, count) = family switch
        {
            0 => (1.04f, 0.94f, 0.67f, 0.58f, 4),
            1 => (1f, 1f, 0.63f, 1.20f, 5),
            2 => (1.13f, 0.45f, 0.66f, 0.92f, 6),
            _ => (0.92f, 0.96f, 0.30f, 0.68f, 9),
        };
        float coreX = (u + (family == 2 ? 0.12f : 0f)) / width;
        float coreY = (v - (family == 2 ? (variant % 2 == 0 ? 0.24f : -0.24f) * u : 0f)) / height;
        float angle = (float)Math.Atan2(coreY, coreX);
        float radius = (float)Math.Sqrt(coreX * coreX + coreY * coreY);
        float grain = Noise(x, y, variant);
        float edge = size + raggedness * (0.10f * (float)Math.Sin(3f * angle + variant * 1.4f)
            + 0.077f * (float)Math.Sin(7f * angle - variant)) + 0.065f * (grain - 0.5f);
        float fan = 1f - Smooth(edge - 0.058f, edge + 0.038f, radius);
        float flecks = 0f;
        // Detached flecks share the impact's projection and lighting sample.
        for (int i = 0; i < count; i++)
        {
            float bearing = i * (6.283f / count) + variant * 0.28f + (Noise(i, 0, variant) - 0.5f) * 0.56f;
            float distance = family == 3 ? 0.37f + 0.32f * Noise(i, 1, variant) : 0.72f + 0.078f * Noise(i, 1, variant);
            float dx = u - (float)Math.Cos(bearing) * distance;
            float dy = v - (float)Math.Sin(bearing) * distance * (family == 2 ? 0.57f : 1f);
            float speck = family == 3 ? 0.10f + 0.15f * Noise(i, 2, variant) : 0.055f + 0.075f * Noise(i, 2, variant);
            float shape = (dx * dx * (family == 2 ? 0.32f : 0.78f) + dy * dy) / (speck * speck);
            flecks = Math.Max(flecks, 1f - Smooth(0.44f, 1.12f, shape));
        }
        float tileEdge = 1f - Smooth(0.85f * 0.85f, 0.91f * 0.91f, u * u + v * v);
        float opacity = Math.Max(fan, flecks) * tileEdge * (0.88f + 0.09f * grain);
        float pigment = 0.83f + 0.25f * grain - 0.13f * Smooth(0.35f, 0.88f, radius);
        var (red, green, blue) = Crimson(pigment, 0f, false);
        return (red, green, blue, Byte(255f * opacity));
    }

    public static (byte R, byte G, byte B) Crimson(float density, float sheen, bool pool) =>
        (Byte(Math.Min(36f, (pool ? 25f : 29f) * density + 7f * sheen)), Byte(2.5f * density + 6f * sheen),
            Byte(4.8f * density + 4.5f * sheen));

    private static float Noise(int x, int y, int seed)
    {
        uint value = unchecked((uint)(x * 374761393 + y * 668265263 + seed * 224682251));
        value = unchecked((value ^ (value >> 13)) * 1274126177);
        return ((value ^ (value >> 16)) & 255) / 255f;
    }

    private static float Smooth(float start, float end, float value)
    {
        float t = Math.Max(0f, Math.Min(1f, (value - start) / (end - start)));
        return t * t * (3f - 2f * t);
    }

    private static byte Byte(float value) => (byte)Math.Max(0f, Math.Min(255f, value));
}
