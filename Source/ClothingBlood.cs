using System;
using System.Collections.Generic;
using Point = System.Numerics.Vector3;
using UV = System.Numerics.Vector2;

namespace Redline;

internal sealed class ClothingBlood
{
    private readonly Drop[] drops = new Drop[12];
    private readonly float opacity;

    public ClothingBlood(Random random, float height, float strength, bool stomp)
    {
        float scale = 0.68f + 0.34f * strength;
        opacity = 0.43f + 0.47f * strength;
        float center = Between(-0.70f, 0.70f);
        height += Between(-0.60f, 0.60f);
        for (int i = 0; i < drops.Length; i++)
        {
            bool broad = i < 3;
            float x = center + Between(-2.30f, 2.30f) * (i == 0 ? 0.2f : 1f);
            float y = height + Between(stomp ? -2.25f : -2.85f, stomp ? 1.75f : 2.9f) * (i == 0 ? 0.25f : 1f);
            float size = (broad ? Between(0.49f, 0.89f) : Between(0.11f, 0.35f)) * scale;
            drops[i] = new Drop(x, y, size, size * Between(1.18f, 1.90f), Between(-0.42f, 0.42f), random.Next());
        }

        float Between(float min, float max) => min + (max - min) * (float)random.NextDouble();
    }

    public static float Reach(float distance, bool heavy)
    {
        float near = 12f;
        float far = heavy ? 43f : 36f;
        return 1f - Smooth((distance - near) / (far - near));
    }

    public static float Residue(float age, float lifetime) => 1f - Smooth((age - 8f) / (lifetime - 8f));
    public static float Wetness(float age) => 1f - Smooth(age / 7f);

    public List<Ink> Project(Point[] vertices, Point[] normals, UV[] uv, int[] triangles, Point toward, int width, int height)
    {
        var mask = new byte[width * height];
        var side = new Point(-toward.Y, toward.X, 0);
        var positions = new UV[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
            positions[i] = new UV(Point.Dot(vertices[i], side), -vertices[i].Z);

        for (int i = 0; i < triangles.Length; i += 3)
        {
            int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
            var normal = Point.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
            float length = normal.LengthSquared();
            if (length < 0.000001f)
                continue;
            if (Point.Dot(normal, normals[a] + normals[b] + normals[c]) < 0)
                normal = -normal;
            float facing = Math.Min(1f, 0.18f + 1.3f * Point.Dot(normal, toward) / (float)Math.Sqrt(length));
            if (facing <= 0)
                continue;

            UV origin = uv[a];
            UV u = uv[b] - origin;
            UV v = uv[c] - origin;
            float det = u.X * v.Y - u.Y * v.X;
            if (Math.Abs(det) < 0.0000001f)
                continue;
            var min = UV.Min(origin, UV.Min(uv[b], uv[c]));
            var max = UV.Max(origin, UV.Max(uv[b], uv[c]));
            int firstX = Math.Max(0, (int)(min.X * width));
            int firstY = Math.Max(0, (int)(min.Y * height));
            int lastX = Math.Min(width - 1, (int)(max.X * width));
            int lastY = Math.Min(height - 1, (int)(max.Y * height));
            UV xStep = positions[b] - positions[a];
            UV yStep = positions[c] - positions[a];

            for (int y = firstY; y <= lastY; y++)
                for (int x = firstX; x <= lastX; x++)
                {
                    float dx = (x + 0.5f) / width - origin.X;
                    float dy = (y + 0.5f) / height - origin.Y;
                    float s = (v.Y * dx - v.X * dy) / det;
                    float t = (u.X * dy - u.Y * dx) / det;
                    if (s < 0 || t < 0 || s + t > 1f)
                        continue;
                    UV point = positions[a] + xStep * s + yStep * t;
                    byte ink = (byte)(255f * opacity * facing * At(point));
                    int pixel = y * width + x;
                    if (ink > mask[pixel])
                        mask[pixel] = ink;
                }
        }

        var painted = new List<Ink>();
        for (int i = 0; i < mask.Length; i++)
            if (mask[i] > 5)
                painted.Add(new Ink(i, mask[i]));
        return painted;
    }

    private float At(UV point)
    {
        float ink = 0;
        foreach (var drop in drops)
        {
            float y = (point.Y - drop.Y) / drop.Height;
            if (Math.Abs(y) > 1)
                continue;
            float x = (point.X - drop.X) / drop.Width + drop.Slant * y;
            float radius = x * x + y * y;
            if (radius >= 1f)
                continue;
            int grain = unchecked((int)(point.X * 34f) * 73856093 ^ (int)(point.Y * 34f) * 19349663 ^ drop.Seed);
            float edge = 0.70f + ((grain ^ (grain >> 11)) & 31) * 0.009f;
            ink = Math.Max(ink, 1f - Smooth((radius - edge) * 6.2f));
        }
        return ink;
    }

    private static float Smooth(float value)
    {
        float t = Math.Min(1f, Math.Max(0f, value));
        return t * t * (3f - 2f * t);
    }

    public readonly record struct Ink(int Pixel, byte Coverage);
    private readonly record struct Drop(float X, float Y, float Width, float Height, float Slant, int Seed);
}
