using System;
using System.Collections.Generic;
using System.Numerics;

namespace Redline;

internal static class WaistGeometry
{
    internal readonly record struct Intersection(int A, int B, float Fraction);
    internal sealed record Cut(Intersection[] Intersections, int[][] Upper, int[][] Lower, int[][] Rims,
        int[][] UpperStumps, int[][] LowerStumps);

    public static Cut Split(Vector3[] positions, int[][] sections, int[] welded, byte[] labels,
        Vector3 center, Vector3 up, int[][] stumpRims = null)
    {
        const float tolerance = 0.0001f;
        var distances = new float[positions.Length];
        var keepUpper = new bool[positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            // Preserve the arms and head even if the pose puts them below the waist.
            if (labels != null && labels[i] >= 3)
                keepUpper[welded[i]] = true;
        }
        for (int i = 0; i < distances.Length; i++)
        {
            int weld = welded[i];
            float distance = Vector3.Dot(positions[weld] - center, up);
            distances[i] = keepUpper[weld] && distance < 0 ? -distance
                : Math.Abs(distance) < tolerance ? tolerance : distance;
        }

        var upper = new int[sections.Length][];
        var lower = new int[sections.Length][];
        var crossings = new List<Intersection>();
        var edgeVertices = new Dictionary<(int A, int B), int>();
        var rimNodes = new Dictionary<(int A, int B), int>();
        var neighbors = new Dictionary<int, List<int>>();
        var rimVertices = new Dictionary<int, int>();
        var polygon = new int[3];
        for (int section = 0; section < sections.Length; section++)
        {
            var above = new List<int>();
            var below = new List<int>();
            int[] triangles = sections[section];
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                if (welded[a] == welded[b] || welded[b] == welded[c] || welded[c] == welded[a])
                    continue;
                polygon[0] = a;
                polygon[1] = b;
                polygon[2] = c;
                int first = -1, second = -1;
                for (int j = 0; j < 3; j++)
                {
                    int from = polygon[j], to = polygon[(j + 1) % 3];
                    if ((distances[from] > 0) == (distances[to] > 0))
                        continue;
                    var key = Ordered(welded[from], welded[to]);
                    if (!rimNodes.TryGetValue(key, out int node))
                    {
                        node = rimNodes.Count;
                        rimNodes.Add(key, node);
                        rimVertices.Add(node, Crossing(from, to));
                    }
                    if (first == -1) first = node;
                    else second = node;
                }
                if (second != -1)
                {
                    Connect(first, second);
                    Connect(second, first);
                }
                Clip(above, true);
                Clip(below, false);
            }
            upper[section] = above.ToArray();
            lower[section] = below.ToArray();
        }
        return new Cut(crossings.ToArray(), upper, lower, CutBoundary.TraceLoops(neighbors, rimVertices),
            KeepStumps(true), KeepStumps(false));

        int[][] KeepStumps(bool above) => stumpRims == null ? Array.Empty<int[]>()
            : Array.FindAll(stumpRims, rim => Array.TrueForAll(rim, index => (distances[index] > 0) == above));

        void Clip(List<int> triangles, bool above)
        {
            int first = -1, previous = -1;
            for (int i = 0; i < 3; i++)
            {
                int from = polygon[i], to = polygon[(i + 1) % 3];
                bool fromInside = (distances[from] > 0) == above;
                if (fromInside)
                    Append(from);
                if (fromInside != ((distances[to] > 0) == above))
                    Append(Crossing(from, to));
            }
            void Append(int vertex)
            {
                if (first == -1) first = vertex;
                else if (previous != first)
                {
                    triangles.Add(first);
                    triangles.Add(previous);
                    triangles.Add(vertex);
                }
                previous = vertex;
            }
        }

        int Crossing(int from, int to)
        {
            var key = Ordered(from, to);
            if (!edgeVertices.TryGetValue(key, out int vertex))
            {
                vertex = positions.Length + crossings.Count;
                edgeVertices.Add(key, vertex);
                var (a, b) = key;
                crossings.Add(new Intersection(a, b, distances[a] / (distances[a] - distances[b])));
            }
            return vertex;
        }

        void Connect(int from, int to)
        {
            if (!neighbors.TryGetValue(from, out var connected))
                neighbors[from] = connected = new List<int>(2);
            if (!connected.Contains(to))
                connected.Add(to);
        }
    }

    private static (int, int) Ordered(int a, int b) => a < b ? (a, b) : (b, a);
}
