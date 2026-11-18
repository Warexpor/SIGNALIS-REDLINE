using System;
using System.Collections.Generic;
using System.Numerics;

namespace Redline;

internal static class CutBoundary
{
    public static int[][] Find(int[][] submeshes, byte[] labels, int[] welded, int parts)
    {
        var edges = new Dictionary<(int, int), Edge>();
        foreach (var indices in submeshes)
        {
            for (int i = 0; i < indices.Length; i += 3)
            {
                int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                if (welded[a] == welded[b] || welded[b] == welded[c] || welded[c] == welded[a])
                    continue;
                bool kept = (parts & (1 << Owner(labels, a, b, c))) != 0;
                Add(a, b, kept);
                Add(b, c, kept);
                Add(c, a, kept);
            }
        }

        var neighbors = new Dictionary<int, List<int>>();
        var vertices = new Dictionary<int, int>();
        foreach (var pair in edges)
        {
            if (!pair.Value.Kept || !pair.Value.Removed)
                continue;
            var (a, b) = pair.Key;
            Connect(a, b);
            Connect(b, a);
            vertices[a] = pair.Value.A;
            vertices[b] = pair.Value.B;
        }

        var visited = new HashSet<int>();
        var loops = new List<int[]>();
        foreach (int start in neighbors.Keys)
        {
            if (visited.Contains(start) || neighbors[start].Count != 2)
                continue;
            var loop = new List<int>();
            int previous = -1;
            int current = start;
            while (neighbors[current].Count == 2 && visited.Add(current))
            {
                loop.Add(vertices[current]);
                var next = neighbors[current];
                int following = next[0] == previous ? next[1] : next[0];
                previous = current;
                current = following;
            }
            if (current == start && loop.Count >= 3)
                loops.Add(loop.ToArray());
        }
        return loops.ToArray();

        void Add(int a, int b, bool kept)
        {
            if (welded[a] > welded[b])
                (a, b) = (b, a);
            var key = (welded[a], welded[b]);
            edges.TryGetValue(key, out var edge);
            edges[key] = kept ? new Edge(a, b, true, edge.Removed) : edge with { Removed = true };
        }

        void Connect(int a, int b)
        {
            if (!neighbors.TryGetValue(a, out var list))
                neighbors[a] = list = new List<int>(2);
            list.Add(b);
        }
    }

    public static int Owner(byte[] labels, int i, int j, int k)
    {
        byte a = labels[i], b = labels[j], c = labels[k];
        if (a == b || a == c) return a;
        return b == c ? b : 0;
    }

    public static int[] Triangulate(Vector3[] loop)
    {
        // Unfold the boundary onto its dominant plane, but keep the original vertices for rendering.
        var normal = Vector3.Zero;
        for (int i = 0; i < loop.Length; i++)
            normal += Vector3.Cross(loop[i] - loop[0], loop[(i + 1) % loop.Length] - loop[0]);
        float x = Math.Abs(normal.X), y = Math.Abs(normal.Y), z = Math.Abs(normal.Z);
        var flat = new Vector2[loop.Length];
        var polygon = new List<int>(loop.Length);
        for (int i = 0; i < loop.Length; i++)
        {
            var p = loop[i] - loop[0];
            flat[i] = x >= y && x >= z ? new Vector2(p.Y, p.Z) :
                y >= z ? new Vector2(p.Z, p.X) : new Vector2(p.X, p.Y);
            polygon.Add(i);
        }
        if ((x >= y && x >= z ? normal.X : y >= z ? normal.Y : normal.Z) < 0)
            polygon.Reverse();

        var triangles = new List<int>((loop.Length - 2) * 3);
        while (polygon.Count > 3)
        {
            int clipped = 0;
            while (clipped < polygon.Count && !IsEar(clipped))
                clipped++;
            if (clipped == polygon.Count)
            {
                // A folded animated seam can overlap in projection. Keep every edge covered.
                for (int i = 1; i < polygon.Count - 1; i++)
                    AddTriangle(polygon[0], polygon[i], polygon[i + 1]);
                return triangles.ToArray();
            }
            AddTriangle(polygon[(clipped + polygon.Count - 1) % polygon.Count], polygon[clipped],
                polygon[(clipped + 1) % polygon.Count]);
            polygon.RemoveAt(clipped);
        }
        AddTriangle(polygon[0], polygon[1], polygon[2]);
        return triangles.ToArray();

        bool IsEar(int index)
        {
            int a = polygon[(index + polygon.Count - 1) % polygon.Count];
            int b = polygon[index], c = polygon[(index + 1) % polygon.Count];
            if (Turn(a, b, c) <= 0)
                return false;
            foreach (int p in polygon)
                if (p != a && p != b && p != c && Turn(a, b, p) >= 0 && Turn(b, c, p) >= 0 && Turn(c, a, p) >= 0)
                    return false;
            return true;
        }

        float Turn(int a, int b, int c)
        {
            var edge = flat[b] - flat[a];
            var other = flat[c] - flat[a];
            return edge.X * other.Y - edge.Y * other.X;
        }

        void AddTriangle(int a, int b, int c)
        {
            triangles.Add(a);
            triangles.Add(b);
            triangles.Add(c);
        }
    }

    private readonly record struct Edge(int A, int B, bool Kept, bool Removed);
}
