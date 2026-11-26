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
        var accessory = mesh.name switch
        {
            "FemaleMilitaryHat" => Part.Head,
            "EuleKnife" => Part.RightArm,
            _ => Part.Body
        };
        if (accessory != Part.Body)
        {
            var accessoryLabels = new byte[mesh.vertexCount];
            for (int i = 0; i < accessoryLabels.Length; i++)
                accessoryLabels[i] = (byte)accessory;
            return new SkinBinding(accessoryLabels, null);
        }
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
    public static bool IsStockGlitchQuad(Renderer source)
    {
        foreach (var material in source.sharedMaterials)
            if (material && material.name is "GlitchTest" or "GlitchForMirror")
                return true;
        return false;
    }

    public static bool IsEffectsPass(Renderer source)
    {
        foreach (var material in source.sharedMaterials)
            if (material && material.name.StartsWith("monsterFX", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public static Transform CopyAccessory(MeshRenderer source, Transform parent)
    {
        var filter = source.GetComponent<MeshFilter>();
        if (!filter || !filter.sharedMesh)
            return null;
        var copy = new GameObject("REDLINE Rigid Accessory") { layer = source.gameObject.layer };
        copy.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
        copy.transform.localScale = source.transform.lossyScale;
        copy.transform.SetParent(parent, true);
        copy.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
        var renderer = copy.AddComponent<MeshRenderer>();
        CopyRenderSettings(source, renderer);
        var properties = new MaterialPropertyBlock();
        source.GetPropertyBlock(properties);
        renderer.SetPropertyBlock(properties);
        return copy.transform;
    }

    public static void CopyRenderSettings(Renderer source, MeshRenderer destination)
    {
        destination.sharedMaterials = source.sharedMaterials;
        destination.renderingLayerMask = source.renderingLayerMask;
        destination.sortingLayerID = source.sortingLayerID;
        destination.sortingOrder = source.sortingOrder;
        destination.shadowCastingMode = source.shadowCastingMode;
        destination.receiveShadows = source.receiveShadows;
        destination.lightProbeUsage = source.lightProbeUsage;
        destination.reflectionProbeUsage = source.reflectionProbeUsage;
        destination.probeAnchor = source.probeAnchor;
    }

    private readonly SkinnedMeshRenderer source;
    private readonly byte[] labels;
    private readonly int[] welded;
    private readonly int[][] originalTriangles;
    private readonly Mesh baked;
    private readonly bool previouslyHidden;
    private readonly bool effectsPass;
    private readonly bool expectsUv;
    private readonly MaterialPropertyBlock props = new();
    private readonly int available;
    private Il2CppStructArray<int>[] remaining;
    private CutCap liveCap;
    private MeshRenderer renderer;
    private GameObject display;
    private Transform corpseRoot;
    private int missingParts;
    private int[] visibleVertices;
    private Il2CppStructArray<Vector3> posedVertices;
    private Vector3[] floorVertices;
    private bool hasRemainingTriangles;
    private bool prepared;

    public SkinnedMeshRenderer Source => source;
    public int RimCount { get; private set; }
    public bool CanDisplay => source && !previouslyHidden && source.enabled && source.gameObject.activeInHierarchy;
    public bool CanSever => corpseRoot ? prepared && renderer && renderer.enabled : CanDisplay;
    public bool SupportsFloor { get; }
    public string DrawStatus => $"layer={source.gameObject.layer} fxPass={effectsPass} prepared={prepared} enabled={(renderer && renderer.enabled)}";

    public SkinCut(SkinnedMeshRenderer source, SkinBinding binding)
    {
        this.source = source;
        previouslyHidden = source.forceRenderingOff;
        effectsPass = IsEffectsPass(source);
        SupportsFloor = !effectsPass && source.sharedMesh.name.IndexOf("body", StringComparison.OrdinalIgnoreCase) >= 0;
        expectsUv = source.sharedMesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0);
        baked = new Mesh { name = $"REDLINE Skin {source.name}" };
        baked.MarkDynamic();
        source.BakeMesh(baked);
        labels = binding?.Labels ?? new byte[baked.vertexCount];
        if (baked.vertexCount != labels.Length)
        {
            Object.Destroy(baked);
            throw new InvalidDataException($"Baked vertex count differs for {source.name}");
        }
        welded = binding == null ? null : binding.Welded ?? SkinMap.Weld(baked.vertices);
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

    public void SetProceduralPose(bool show)
    {
        if (missingParts != 0 || corpseRoot)
            return;
        if (show && CanDisplay)
        {
            EnsureDisplay();
            remaining ??= Filter(~0);
            hasRemainingTriangles = true;
        }
        else if (display)
        {
            RestoreSource();
            Object.Destroy(display);
            display = null;
        }
    }

    public void SetSplitHidden(bool hidden)
    {
        if (display)
            display.SetActive(!hidden);
    }

    public bool HasReplacement => prepared && display && CanDisplay;

    public void GetSplitTopology(out int[][] sections, out int[][] rims)
    {
        if (missingParts == 0)
        {
            sections = originalTriangles;
            rims = Array.Empty<int[]>();
            return;
        }
        sections = new int[remaining.Length][];
        for (int i = 0; i < sections.Length; i++)
            sections[i] = remaining[i];
        rims = welded == null ? Array.Empty<int[]>() : CutBoundary.Find(originalTriangles, labels, welded, ~missingParts);
    }

    public bool AddShotSurface(ShotHitboxes.Geometry body)
    {
        if (effectsPass || !prepared || !display || !renderer || !renderer.enabled || !display.activeInHierarchy)
            return false;
        body.Add(baked, display.transform);
        liveCap?.AddShotSurface(body);
        return true;
    }

    public void Sever(Part part, int missing, DetachedLimb limb, Vector3 corpseOffset = default)
    {
        if (!source || !HasPart(part))
            return;
        if (CanSever)
            MakeDetachedMesh(1 << (int)part, limb, corpseOffset);

        ShowMissingParts(missing);
        if (corpseRoot && display)
            display.transform.position += corpseOffset;
    }

    public void ShowMissingParts(int missing)
    {
        if (!source || (available & missing) == 0)
            return;
        EnsureDisplay();
        RebuildRemaining(missing);
    }

    private void EnsureDisplay()
    {
        if (!display)
        {
            display = new GameObject("REDLINE Cut Skin") { layer = source.gameObject.layer };
            display.transform.SetParent(source.transform, false);
            display.AddComponent<MeshFilter>().sharedMesh = baked;
            renderer = display.AddComponent<MeshRenderer>();
            renderer.enabled = false;
            CopyRenderSettings(source, renderer);
        }
    }

    public void BeginCorpse(Transform root)
    {
        EnsureDisplay();
        corpseRoot = root;
        display.transform.SetParent(root, true);
        remaining ??= Filter(~missingParts);
        visibleVertices = GetVisibleVertices();
        hasRemainingTriangles = visibleVertices.Length != 0;
    }

    public void EndCorpse()
    {
        corpseRoot = null;
        visibleVertices = null;
        if (!display || !source)
        {
            if (source && prepared)
                RestoreSource();
            return;
        }
        if (missingParts == 0)
        {
            RestoreParts(0);
            return;
        }

        display.transform.SetParent(source.transform, false);
        display.transform.localPosition = Vector3.zero;
        display.transform.localRotation = Quaternion.identity;
        display.transform.localScale = Vector3.one;
        try { Refresh(); }
        catch { RestoreParts(0); }
    }

    public float LowestPoint(out Vector3 contact)
    {
        contact = default;
        if (!renderer || !renderer.enabled || visibleVertices == null)
            return float.NegativeInfinity;
        var world = display.transform.localToWorldMatrix;
        var vertices = floorVertices;
        float depth = float.NegativeInfinity;
        int lowest = -1;
        foreach (int index in visibleVertices)
        {
            var point = vertices[index];
            float z = world.m20 * point.x + world.m21 * point.y + world.m22 * point.z + world.m23;
            if (z > depth)
            {
                depth = z;
                lowest = index;
            }
        }
        if (lowest >= 0)
            contact = world.MultiplyPoint3x4(vertices[lowest]);
        return depth;
    }

    public void AddHitboxBounds(Matrix4x4 boxSpace, ref System.Numerics.Vector3 min, ref System.Numerics.Vector3 max)
    {
        if (effectsPass || !renderer || !renderer.enabled || visibleVertices == null)
            return;
        var transform = boxSpace * display.transform.localToWorldMatrix;
        foreach (int index in visibleVertices)
        {
            var p = floorVertices[index];
            var point = new System.Numerics.Vector3(
                transform.m00 * p.x + transform.m01 * p.y + transform.m02 * p.z + transform.m03,
                transform.m10 * p.x + transform.m11 * p.y + transform.m12 * p.z + transform.m13,
                transform.m20 * p.x + transform.m21 * p.y + transform.m22 * p.z + transform.m23);
            min = System.Numerics.Vector3.Min(min, point);
            max = System.Numerics.Vector3.Max(max, point);
        }
    }

    private int[] GetVisibleVertices()
    {
        var indices = new HashSet<int>();
        foreach (var section in remaining)
            foreach (int index in section)
                indices.Add(index);

        var result = new int[indices.Count];
        indices.CopyTo(result);
        return result;
    }

    public void Refresh(bool followMaterials = false)
    {
        if (!display || !source)
        {
            if (source && prepared)
                RestoreSource();
            return;
        }
        bool visible = corpseRoot || CanDisplay;
        if (corpseRoot)
            display.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
        display.layer = source.gameObject.layer;
        if (!visible)
        {
            RestoreSource();
            return;
        }
        try
        {
            source.BakeMesh(baked);
            if (baked.vertexCount != labels.Length || baked.vertexCount == 0
                || (expectsUv && !baked.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0)))
                throw new InvalidDataException($"Incomplete baked surface for {source.name}");

            posedVertices = missingParts != 0 || visibleVertices != null ? baked.vertices : null;
            if (visibleVertices != null)
                MeshArrays.Read(posedVertices, ref floorVertices);
            liveCap?.Refresh(posedVertices);
            if (missingParts != 0)
                ApplyTriangles(baked, remaining);
            if (followMaterials && renderer.sharedMaterial != source.sharedMaterial)
                renderer.sharedMaterials = source.sharedMaterials;
            source.GetPropertyBlock(props);
            renderer.SetPropertyBlock(props);
            renderer.enabled = hasRemainingTriangles;
            prepared = true;
            source.forceRenderingOff = true;
        }
        catch
        {
            RestoreSource();
            throw;
        }
    }

    private void RestoreSource()
    {
        prepared = false;
        if (renderer)
            renderer.enabled = false;
        if (source) source.forceRenderingOff = previouslyHidden;
    }

    public RimAnchor FindWoundRim(Part part, Vector3 cut, Vector3 outward)
    {
        if (!display || !CanDisplay || welded == null || posedVertices == null)
            return null;

        RimAnchor best = null;
        foreach (var loop in CutBoundary.Find(originalTriangles, labels, welded, ~(1 << (int)part)))
        {
            var anchor = new RimAnchor(this, loop);
            if (!anchor.TrySample(out var center, out var normal) || (center - cut).sqrMagnitude > 6.25f)
                continue;
            if (best != null && best.Perimeter >= anchor.Perimeter)
                continue;
            anchor.Orientation = Vector3.Dot(normal, outward) < 0 ? -1 : 1;
            best = anchor;
        }
        return best;
    }

    internal sealed class RimAnchor(SkinCut skin, int[] loop)
    {
        public float Perimeter { get; private set; }
        public int Orientation = 1;

        public bool TrySample(out Vector3 center, out Vector3 normal)
        {
            center = normal = Vector3.zero;
            if (!skin.display || !skin.renderer.enabled || skin.posedVertices == null)
                return false;
            var world = skin.display.transform.localToWorldMatrix;
            var vertices = skin.posedVertices;
            var first = world.MultiplyPoint3x4(vertices[loop[0]]);
            Vector3 previous = first;
            center = first;
            Perimeter = 0;
            for (int i = 1; i < loop.Length; i++)
            {
                var current = world.MultiplyPoint3x4(vertices[loop[i]]);
                center += current;
                normal += Vector3.Cross(previous - first, current - first);
                Perimeter += Vector3.Distance(previous, current);
                previous = current;
            }
            Perimeter += Vector3.Distance(previous, first);
            center /= loop.Length;
            normal = normal.normalized * Orientation;
            return normal.sqrMagnitude > 0.01f;
        }
    }

    public void RestoreParts(int missing)
    {
        if (!display || !source)
            return;
        if ((available & missing) == 0)
        {
            missingParts = 0;
            RimCount = 0;
            remaining = null;
            prepared = false;
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
        if (source && prepared) source.forceRenderingOff = previouslyHidden;
        if (display) Object.Destroy(display);
        if (baked) Object.Destroy(baked);
    }

    private void RebuildRemaining(int missing)
    {
        missingParts = missing;
        remaining = Filter(~missing);
        hasRemainingTriangles = Array.Exists(remaining, section => section.Length != 0);
        if (corpseRoot)
            visibleVertices = GetVisibleVertices();
        liveCap?.Dispose();
        var rims = welded == null ? Array.Empty<int[]>() : CutBoundary.Find(originalTriangles, labels, welded, ~missing);
        RimCount = rims.Length;
        liveCap = CutCap.Create(display.transform, display.layer, baked.vertices, rims);
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

    private void MakeDetachedMesh(int parts, DetachedLimb limb, Vector3 corpseOffset)
    {
        var fragment = new Mesh { name = "REDLINE Detached Skin" };
        limb.Meshes.Add(fragment);
        source.BakeMesh(fragment);
        var origin = source.transform;
        var destination = limb.Visual.transform;
        var skinned = fragment.vertices;
        var normals = fragment.normals;
        Vector3[] positions = null;
        Vector3[] directions = null;
        MeshArrays.Read(skinned, ref positions);
        MeshArrays.Read(normals, ref directions);
        Matrix4x4 conversion = destination.worldToLocalMatrix * Matrix4x4.Translate(corpseOffset) * origin.localToWorldMatrix;
        Matrix4x4 normalTransform = conversion.inverse.transpose;
        for (int i = 0; i < positions.Length; i++)
        {
            var p = positions[i];
            positions[i] = new Vector3(conversion.m00 * p.x + conversion.m01 * p.y + conversion.m02 * p.z + conversion.m03,
                conversion.m10 * p.x + conversion.m11 * p.y + conversion.m12 * p.z + conversion.m13,
                conversion.m20 * p.x + conversion.m21 * p.y + conversion.m22 * p.z + conversion.m23);
        }
        for (int i = 0; i < directions.Length; i++)
        {
            var n = directions[i];
            var transformed = new System.Numerics.Vector3(normalTransform.m00 * n.x + normalTransform.m01 * n.y + normalTransform.m02 * n.z,
                normalTransform.m10 * n.x + normalTransform.m11 * n.y + normalTransform.m12 * n.z,
                normalTransform.m20 * n.x + normalTransform.m21 * n.y + normalTransform.m22 * n.z);
            if (transformed.LengthSquared() > 0.0000000001f)
                transformed = System.Numerics.Vector3.Normalize(transformed);
            else transformed = default;
            directions[i] = new Vector3(transformed.X, transformed.Y, transformed.Z);
        }
        MeshArrays.Write(positions, skinned);
        MeshArrays.Write(directions, normals);
        fragment.vertices = skinned;
        fragment.normals = normals;
        var triangles = Filter(parts);
        ApplyTriangles(fragment, triangles);
        if (!effectsPass)
            limb.AddSurface(skinned, triangles);

        var part = new GameObject("REDLINE Limb Surface") { layer = source.gameObject.layer };
        part.transform.SetParent(destination, false);
        part.AddComponent<MeshFilter>().sharedMesh = fragment;
        var material = part.AddComponent<MeshRenderer>();
        CopyRenderSettings(source, material);
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
    private readonly Il2CppStructArray<Color32> colors;
    private float nextLight;
    private Color32 tint;
    private bool lit;

    private CutCap(GameObject root, Mesh mesh, int[] boundary)
    {
        this.root = root;
        Mesh = mesh;
        this.boundary = boundary;
        vertices = new Il2CppStructArray<Vector3>(boundary.Length);
        colors = new Il2CppStructArray<Color32>(boundary.Length);
    }

    public Mesh Mesh { get; }

    public void AddShotSurface(ShotHitboxes.Geometry body)
    {
        if (root && root.activeInHierarchy)
            body.Add(Mesh, root.transform);
    }

    public static CutCap Create(Transform parent, int layer, Il2CppStructArray<Vector3> positions, int[][] loops)
    {
        if (loops.Length == 0)
            return null;
        if (!material)
            material = BloodMaterial.CreateCutSurface();
        if (!material)
            return null;

        var boundary = new List<int>();
        var uv = new List<Vector2>();
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
                uv.Add(BloodMaterial.CutUv(i % 3 == 1 ? 0.06f : 0.88f));
            }
            foreach (int index in CutBoundary.Triangulate(points))
                triangles.Add(start + index);
        }

        var root = new GameObject("REDLINE Cut Surface") { layer = layer };
        root.transform.SetParent(parent, false);
        var mesh = new Mesh { name = "REDLINE Cut Surface" };
        mesh.MarkDynamic();
        var cap = new CutCap(root, mesh, boundary.ToArray());
        cap.RefreshVertices(positions);
        mesh.uv = uv.ToArray();
        mesh.triangles = triangles.ToArray();
        cap.RefreshLighting();
        root.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = root.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return cap;
    }

    public void Refresh(Il2CppStructArray<Vector3> positions)
    {
        RefreshVertices(positions);
        RefreshLighting();
        Mesh.RecalculateBounds();
    }

    private void RefreshLighting()
    {
        float now = Time.time;
        if (lit && now < nextLight)
            return;
        nextLight = now + 0.24f;
        var world = root.transform;
        var room = BloodLighting.Room;
        room.Refresh(now);
        var sample = world.TransformPoint((vertices[0] + vertices[1] + vertices[2]) / 3f);
        var shaded = room.SurfaceTint(sample, Battlefield.Up);
        if (lit && shaded.r == tint.r && shaded.g == tint.g && shaded.b == tint.b)
            return;
        tint = shaded;
        float fog = BloodFog.Factor(sample);
        if (fog > 0f)
            tint = BloodFog.Apply(tint, fog);
        lit = true;
        for (int i = 0; i < boundary.Length; i++)
            colors[i] = tint;
        Mesh.colors32 = colors;
    }

    private void RefreshVertices(Il2CppStructArray<Vector3> positions)
    {
        for (int i = 0; i < boundary.Length; i++)
            vertices[i] = positions[boundary[i]];
        Mesh.vertices = vertices;
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

    public static void ReleaseMaterial()
    {
        if (material) Object.Destroy(material);
        material = null;
    }
}
