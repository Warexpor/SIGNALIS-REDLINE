using System;
using System.Collections.Generic;
using Point = System.Numerics.Vector3;
using UV = System.Numerics.Vector2;

namespace Redline;

internal sealed class ClothingBlood
{
    private readonly Drop[] drops = new Drop[12];
    private readonly float opacity;
    private readonly Action<string> log;

    public ClothingBlood(Action<string> log, Random random, float height, float strength, bool stomp)
    {
        this.log = log;
        float scale = 0.60f + 0.32f * strength;
        opacity = 0.30f + 0.34f * strength;
        float center = Between(-0.50f, 0.50f);
        height += Between(-0.45f, 0.45f);
        for (int i = 0; i < drops.Length; i++)
        {
            bool broad = i < 3;
            float x = center + Between(-1.60f, 1.60f) * (i == 0 ? 0.3f : 1f);
            float y = height + Between(stomp ? -1.60f : -2.00f, stomp ? 1.20f : 2.00f) * (i == 0 ? 0.3f : 1f);
            float size = (broad ? Between(0.36f, 0.68f) : Between(0.09f, 0.25f)) * scale;
            float length = size * Between(1.18f, 1.90f);
            float slant = Between(-0.42f, 0.42f);
            float span = size * (float)Math.Sqrt(1f + slant * slant) + 0.00001f;
            drops[i] = new Drop(x, y, size, length, slant, random.Next(),
                new UV(x - span, y - length - 0.00001f), new UV(x + span, y + length + 0.00001f));
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
        // Anchor the pattern at the lowest vertex instead of trusting the caller
        // origin to sit at the feet. The player position rides near the torso,
        // which used to float every drop above a standing body.
        float feet = float.PositiveInfinity;
        for (int i = 0; i < vertices.Length; i++)
            if (vertices[i].Z < feet)
                feet = vertices[i].Z;
        if (float.IsPositiveInfinity(feet))
            return new List<Ink>();
        var positions = new UV[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
            positions[i] = new UV(Point.Dot(vertices[i], side), vertices[i].Z - feet);

        var candidates = new Drop[drops.Length];
        for (int i = 0; i < triangles.Length; i += 3)
        {
            int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
            var low = UV.Min(positions[a], UV.Min(positions[b], positions[c]));
            var high = UV.Max(positions[a], UV.Max(positions[b], positions[c]));
            int count = 0;
            foreach (var drop in drops)
                if (drop.Min.X <= high.X && drop.Max.X >= low.X && drop.Min.Y <= high.Y && drop.Max.Y >= low.Y)
                    candidates[count++] = drop;
            if (count == 0)
                continue;

            var normal = Point.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
            float length = normal.LengthSquared();
            if (length < 0.000001f)
                continue;
            if (Point.Dot(normal, normals[a] + normals[b] + normals[c]) < 0)
                normal = -normal;
            // Wrap the spray around the body: the facing side stains strongest,
            // the far side still soaks through instead of staying clean.
            float align = Point.Dot(normal, toward) / (float)Math.Sqrt(length);
            float facing = align >= 0 ? 0.38f + 0.62f * Math.Min(1f, align)
                : 0.30f + 0.25f * Math.Min(1f, -align);
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
                    byte ink = (byte)(255f * opacity * facing * At(point, candidates, count));
                    int pixel = y * width + x;
                    if (ink > mask[pixel])
                        mask[pixel] = ink;
                }
        }

        var painted = new List<Ink>();
        for (int i = 0; i < mask.Length; i++)
            if (mask[i] > 10)
                painted.Add(new Ink(i, mask[i]));
        if (painted.Count == 0)
            return Audit(vertices, normals, uv);
        return painted;
    }

    private List<Ink> Audit(Point[] vertices, Point[] normals, UV[] uv)
    {
        float ux0 = float.MaxValue, ux1 = float.MinValue, uy0 = float.MaxValue, uy1 = float.MinValue;
        foreach (var p in uv)
        {
            if (p.X < ux0) ux0 = p.X;
            if (p.X > ux1) ux1 = p.X;
            if (p.Y < uy0) uy0 = p.Y;
            if (p.Y > uy1) uy1 = p.Y;
        }
        float px0 = float.MaxValue, px1 = float.MinValue, py0 = float.MaxValue, py1 = float.MinValue;
        foreach (var p in vertices)
        {
            if (p.X < px0) px0 = p.X;
            if (p.X > px1) px1 = p.X;
            if (p.Y < py0) py0 = p.Y;
            if (p.Y > py1) py1 = p.Y;
        }
        log($"elster blood audit uvx=[{ux0:F2},{ux1:F2}] uvy=[{uy0:F2},{uy1:F2}] posx=[{px0:F1},{px1:F1}] posy=[{py0:F1},{py1:F1}] verts={vertices.Length} normals={normals.Length}");
        return new List<Ink>();
    }

    private static float At(UV point, Drop[] candidates, int count)
    {
        float ink = 0;
        for (int i = 0; i < count; i++)
        {
            var drop = candidates[i];
            float y = (point.Y - drop.Y) / drop.Height;
            if (Math.Abs(y) > 1)
                continue;
            float x = (point.X - drop.X) / drop.Width + drop.Slant * y;
            float radius = x * x + y * y;
            if (radius >= 1f)
                continue;
            int grain = unchecked((int)(point.X * 34f) * 73856093 ^ (int)(point.Y * 34f) * 19349663 ^ drop.Seed);
            float edge = 0.64f + ((grain ^ (grain >> 11)) & 31) * 0.008f;
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
    private readonly record struct Drop(float X, float Y, float Width, float Height, float Slant, int Seed, UV Min, UV Max);
}
