using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnhollowerBaseLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Redline;

internal sealed record SkinBinding(byte[] Labels, int[] Welded);

internal sealed class SkinMap
{
    private readonly Dictionary<(string Name, int Vertices, int Submeshes), SkinBinding> entries = new();

    public SkinMap(string path)
    {
        if (!File.Exists(path))
            return;
        using var compressed = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var input = new BinaryReader(compressed);
        uint version = input.ReadUInt32();
        if (version != 0x31304C52 && version != 0x32304C52)
            throw new InvalidDataException("Unknown REDLINE skin-map format");
        int count = input.ReadInt32();
        if (count < 0 || count > 4096)
            throw new InvalidDataException("Invalid REDLINE skin-map entry count");
        for (int i = 0; i < count; i++)
        {
            int length = input.ReadInt32();
            int vertices = input.ReadInt32();
            int submeshes = input.ReadInt32();
            if (length < 1 || length > 512 || vertices < 1 || vertices > 100000 || submeshes < 1 || submeshes > 32)
                throw new InvalidDataException("Invalid REDLINE skin-map entry");
            string name = Encoding.UTF8.GetString(input.ReadBytes(length));
            byte[] labels = input.ReadBytes(vertices);
            if (labels.Length != vertices || Array.Exists(labels, label => label > (byte)Part.Head))
                throw new InvalidDataException("Invalid REDLINE skin-map labels");
            int[] welded = null;
            if (version == 0x32304C52)
            {
                welded = new int[vertices];
                for (int vertex = 0; vertex < vertices; vertex++)
                {
                    welded[vertex] = input.ReadInt32();
                    if ((uint)welded[vertex] > vertex)
                        throw new InvalidDataException("Invalid REDLINE welded skin vertex");
                }
            }
            entries[(name, vertices, submeshes)] = new SkinBinding(labels, welded);
        }
    }

    public int Count => entries.Count;

    public SkinBinding Find(SkinnedMeshRenderer source, Rig rig)
    {
        var mesh = source.sharedMesh;
        if (entries.TryGetValue((mesh.name, mesh.vertexCount, mesh.subMeshCount), out var binding))
            return binding;
        if (!mesh.isReadable)
            return null;
        var weights = mesh.boneWeights;
        if (weights.Length != mesh.vertexCount || weights.Length == 0)
            return null;

        var bones = source.bones;
        var owners = new byte[bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            if (!bones[i])
                continue;
            for (var part = Part.LeftLeg; part <= Part.Head; part++)
                if (rig.CutBone(part) && bones[i].IsChildOf(rig.CutBone(part)))
                    owners[i] = (byte)part;
        }

        var labels = new byte[weights.Length];
        var scores = new float[6];
        for (int i = 0; i < weights.Length; i++)
        {
            Array.Clear(scores, 0, scores.Length);
            var value = weights[i];
            Score(value.boneIndex0, value.weight0);
            Score(value.boneIndex1, value.weight1);
            Score(value.boneIndex2, value.weight2);
            Score(value.boneIndex3, value.weight3);
            for (byte part = 1; part < scores.Length; part++)
                if (scores[part] > scores[labels[i]])
                    labels[i] = part;
        }
        return new SkinBinding(labels, Weld(mesh.vertices));

        void Score(int bone, float weight)
        {
            if ((uint)bone < owners.Length)
                scores[owners[bone]] += weight;
        }
    }

    public static int[] Weld(Il2CppStructArray<Vector3> vertices)
    {
        var unique = new Dictionary<(int, int, int), int>();
        var welded = new int[vertices.Length];
        for (int i = 0; i < welded.Length; i++)
        {
            var p = vertices[i];
            var key = (Mathf.RoundToInt(p.x * 10000), Mathf.RoundToInt(p.y * 10000), Mathf.RoundToInt(p.z * 10000));
            if (!unique.TryGetValue(key, out int canonical))
                unique.Add(key, canonical = i);
            welded[i] = canonical;
        }
        return welded;
    }
}

internal sealed class SkinCut
{
    private readonly SkinnedMeshRenderer source;
    private readonly byte[] labels;
    private readonly int[] welded;
    private readonly int[][] originalTriangles;
    private readonly Mesh baked;
    private readonly bool previouslyHidden;
    private readonly MaterialPropertyBlock props = new();
    private readonly int available;
    private Il2CppStructArray<int>[] remaining;
    private CutCap liveCap;
    private MeshRenderer renderer;
    private GameObject display;

    public SkinCut(SkinnedMeshRenderer source, SkinBinding binding)
    {
        this.source = source;
        labels = binding.Labels;
        previouslyHidden = source.forceRenderingOff;
        baked = new Mesh { name = $"REDLINE Skin {source.name}" };
        baked.MarkDynamic();
        source.BakeMesh(baked);
        if (baked.vertexCount != labels.Length)
        {
            Object.Destroy(baked);
            throw new InvalidDataException($"Baked vertex count differs for {source.name}");
        }
        welded = binding.Welded ?? SkinMap.Weld(baked.vertices);
        originalTriangles = new int[baked.subMeshCount][];
        for (int i = 0; i < baked.subMeshCount; i++)
        {
            int[] submesh = baked.GetTriangles(i);
            originalTriangles[i] = submesh;
            for (int j = 0; j < submesh.Length; j += 3)
                available |= 1 << (int)Owner(submesh, j);
        }
    }

    public bool HasPart(Part part) => (available & (1 << (int)part)) != 0;

    public void Sever(Part part, int missing, DetachedLimb limb)
    {
        if (!source || !HasPart(part))
            return;
        if (source.enabled && source.gameObject.activeInHierarchy && !previouslyHidden)
            MakeDetachedMesh(1 << (int)part, limb);

        if (!display)
        {
            display = new GameObject("REDLINE Cut Skin") { layer = source.gameObject.layer };
            display.transform.SetParent(source.transform, false);
            display.AddComponent<MeshFilter>().sharedMesh = baked;
            renderer = display.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = source.sharedMaterials;
            renderer.shadowCastingMode = source.shadowCastingMode;
            renderer.receiveShadows = source.receiveShadows;
            source.forceRenderingOff = true;
        }
        RebuildRemaining(missing);
    }

    public void Refresh()
    {
        if (!display || !source)
            return;
        bool visible = source.enabled && source.gameObject.activeInHierarchy && !previouslyHidden;
        renderer.enabled = visible;
        if (!visible)
            return;
        source.BakeMesh(baked);
        liveCap?.Refresh(baked.vertices);
        ApplyTriangles(baked, remaining);
        source.GetPropertyBlock(props);
        renderer.SetPropertyBlock(props);
    }

    public void RestoreParts(int missing)
    {
        if (!display || !source)
            return;
        if ((available & missing) == 0)
        {
            renderer.enabled = false;
            liveCap?.Dispose();
            liveCap = null;
            Object.Destroy(display);
            display = null;
            source.forceRenderingOff = previouslyHidden;
            return;
        }
        RebuildRemaining(missing);
    }

    public void Dispose()
    {
        liveCap?.Dispose();
        if (renderer) renderer.enabled = false;
        if (source && display) source.forceRenderingOff = previouslyHidden;
        if (display) Object.Destroy(display);
        if (baked) Object.Destroy(baked);
    }

    private void RebuildRemaining(int missing)
    {
        remaining = Filter(~missing);
        liveCap?.Dispose();
        liveCap = CutCap.Create(display.transform, display.layer, baked.vertices,
            CutBoundary.Find(originalTriangles, labels, welded, ~missing));
        Refresh();
    }

    private int Owner(int[] triangle, int i) =>
        CutBoundary.Owner(labels, triangle[i], triangle[i + 1], triangle[i + 2]);

    private Il2CppStructArray<int>[] Filter(int parts)
    {
        var output = new Il2CppStructArray<int>[originalTriangles.Length];
        for (int sub = 0; sub < originalTriangles.Length; sub++)
        {
            var selected = new List<int>();
            int[] input = originalTriangles[sub];
            for (int i = 0; i < input.Length; i += 3)
            {
                if ((parts & (1 << Owner(input, i))) == 0)
                    continue;
                selected.Add(input[i]);
                selected.Add(input[i + 1]);
                selected.Add(input[i + 2]);
            }
            output[sub] = selected.ToArray();
        }
        return output;
    }

    private void MakeDetachedMesh(int parts, DetachedLimb limb)
    {
        var fragment = new Mesh { name = "REDLINE Detached Skin" };
        limb.Meshes.Add(fragment);
        source.BakeMesh(fragment);
        var origin = source.transform;
        var destination = limb.Body.transform;
        var skinned = fragment.vertices;
        var normals = fragment.normals;
        Matrix4x4 conversion = destination.worldToLocalMatrix * origin.localToWorldMatrix;
        Matrix4x4 normalTransform = conversion.inverse.transpose;
        for (int i = 0; i < skinned.Length; i++)
            skinned[i] = conversion.MultiplyPoint3x4(skinned[i]);
        for (int i = 0; i < normals.Length; i++)
            normals[i] = normalTransform.MultiplyVector(normals[i]).normalized;
        fragment.vertices = skinned;
        fragment.normals = normals;
        ApplyTriangles(fragment, Filter(parts));

        var part = new GameObject("REDLINE Limb Surface") { layer = source.gameObject.layer };
        part.transform.SetParent(destination, false);
        part.AddComponent<MeshFilter>().sharedMesh = fragment;
        var material = part.AddComponent<MeshRenderer>();
        material.sharedMaterials = source.sharedMaterials;
        source.GetPropertyBlock(props);
        material.SetPropertyBlock(props);

        var cap = CutCap.Create(part.transform, part.layer, skinned, CutBoundary.Find(originalTriangles, labels, welded, parts));
        if (cap != null)
            limb.Meshes.Add(cap.Mesh);
    }

    private static void ApplyTriangles(Mesh mesh, Il2CppStructArray<int>[] triangles)
    {
        mesh.subMeshCount = triangles.Length;
        for (int sub = 0; sub < triangles.Length; sub++)
            mesh.SetTriangles(triangles[sub], sub, false, 0);
        mesh.RecalculateBounds();
    }
}

internal sealed class CutCap
{
    private static Material material;
    private readonly GameObject root;
    private readonly int[] boundary;
    private readonly Il2CppStructArray<Vector3> vertices;

    private CutCap(GameObject root, Mesh mesh, int[] boundary)
    {
        this.root = root;
        Mesh = mesh;
        this.boundary = boundary;
        vertices = new Il2CppStructArray<Vector3>(boundary.Length);
    }

    public Mesh Mesh { get; }

    public static CutCap Create(Transform parent, int layer, Il2CppStructArray<Vector3> positions, int[][] loops)
    {
        if (loops.Length == 0)
            return null;
        if (!material)
        {
            var shader = Shader.Find("Sprites/Default");
            if (!shader)
                shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
            if (!shader)
                return null;
            material = new Material(shader) { name = "REDLINE Cut Surface", mainTexture = Texture2D.whiteTexture };
        }

        var boundary = new List<int>();
        var colors = new List<Color32>();
        var triangles = new List<int>();
        foreach (int[] loop in loops)
        {
            int start = boundary.Count;
            var points = new System.Numerics.Vector3[loop.Length];
            for (int i = 0; i < loop.Length; i++)
            {
                var p = positions[loop[i]];
                points[i] = new System.Numerics.Vector3(p.x, p.y, p.z);
                boundary.Add(loop[i]);
                colors.Add(i % 3 == 1 ? new Color32(107, 29, 36, 255) : new Color32(62, 9, 18, 255));
            }
            foreach (int index in CutBoundary.Triangulate(points))
                triangles.Add(start + index);
        }

        var root = new GameObject("REDLINE Cut Surface") { layer = layer };
        root.transform.SetParent(parent, false);
        var mesh = new Mesh { name = "REDLINE Cut Surface" };
        mesh.MarkDynamic();
        var cap = new CutCap(root, mesh, boundary.ToArray());
        cap.Refresh(positions);
        mesh.colors32 = colors.ToArray();
        mesh.triangles = triangles.ToArray();
        root.AddComponent<MeshFilter>().sharedMesh = mesh;
        root.AddComponent<MeshRenderer>().sharedMaterial = material;
        return cap;
    }

    public void Refresh(Il2CppStructArray<Vector3> positions)
    {
        for (int i = 0; i < boundary.Length; i++)
            vertices[i] = positions[boundary[i]];
        Mesh.vertices = vertices;
        Mesh.RecalculateBounds();
    }

    public void Dispose()
    {
        if (root)
        {
            root.SetActive(false);
            Object.Destroy(root);
        }
        if (Mesh) Object.Destroy(Mesh);
    }
}
