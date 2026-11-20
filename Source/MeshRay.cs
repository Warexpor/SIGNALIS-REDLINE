using System;
using System.Numerics;

namespace Redline;

internal static class MeshRay
{
    public static bool Trace(Vector3[] vertices, int[] triangles, Vector3 origin, Vector3 direction,
        ref float nearest, out Vector3 normal, out int face, out Vector2 barycentric)
    {
        normal = default;
        face = -1;
        barycentric = default;
        const float epsilon = 0.000001f;
        for (int i = 0; i < triangles.Length; i += 3)
        {
            var first = vertices[triangles[i]];
            var ab = vertices[triangles[i + 1]] - first;
            var ac = vertices[triangles[i + 2]] - first;
            var cross = Vector3.Cross(direction, ac);
            float det = Vector3.Dot(ab, cross);
            if (Math.Abs(det) < epsilon)
                continue;
            float inverse = 1 / det;
            var offset = origin - first;
            float u = Vector3.Dot(offset, cross) * inverse;
            if (u < -epsilon || u > 1 + epsilon)
                continue;
            var q = Vector3.Cross(offset, ab);
            float v = Vector3.Dot(direction, q) * inverse;
            if (v < -epsilon || u + v > 1 + epsilon)
                continue;
            float distance = Vector3.Dot(ac, q) * inverse;
            if (distance < epsilon || distance > nearest)
                continue;
            nearest = distance;
            var surface = Vector3.Normalize(Vector3.Cross(ab, ac));
            normal = det > 0 ? surface : -surface;
            face = i / 3;
            barycentric = new Vector2(u, v);
        }
        return face >= 0;
    }
}
