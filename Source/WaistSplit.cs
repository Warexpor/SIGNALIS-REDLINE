using System;
using System.Collections.Generic;
using UnhollowerBaseLib;
using UnityEngine;
using Object = UnityEngine.Object;
using Point = System.Numerics.Vector3;

namespace Redline;

internal sealed class WaistSplit : IDisposable
{
    private readonly DetachedLimb upper;
    private readonly DetachedLimb lower;
    private readonly List<Renderer> originals = new();
    private readonly List<SkinCut> cutDisplays = new();
    private readonly List<(Mesh Mesh, Transform Frame)> shotSurfaces = new();
    private readonly StumpBleed upperFlow = new(Time.time, true);
    private readonly StumpBleed lowerFlow = new(Time.time, true);
    private readonly Vector3 origin;
    private Vector3 upperRim;
    private Vector3 lowerRim;
    private Vector3 upperNormal;
    private Vector3 lowerNormal;
    private bool originalsHidden;
    private int emissions;

    private WaistSplit(DetachedLimb upper, DetachedLimb lower)
    {
        this.upper = upper;
        this.lower = lower;
        origin = upper.Body.transform.position;
    }

    public static WaistSplit TryStart(EnemyController target, Rig rig, SkinMap map, List<SkinCut> skins, EffectsPhysics physics,
        Vector3 direction, float floor, bool rifle, float waistFraction, Action<string> log)
    {
        if (!rig.Hips || !rig.Chest)
            return null;
        Vector3 up = (rig.Chest.position - rig.Hips.position).normalized;
        if (up.sqrMagnitude < 0.5f)
            return null;
        Vector3 waist = Vector3.Lerp(rig.Hips.position, rig.Chest.position, waistFraction);
        Vector3 upperCenter = Vector3.Lerp(waist, rig.Chest.position, 0.72f);
        Vector3 lowerCenter = Vector3.LerpUnclamped(waist, rig.Hips.position, 1.35f);
        var upper = physics.CreatePiece("REDLINE Waist Upper", upperCenter, upperCenter + up * 2f, floor, 1.2f);
        DetachedLimb lower = null;
        WaistSplit split = null;
        bool started = false;
        var baked = new Mesh { name = "REDLINE Waist Bake" };
        try
        {
            lower = physics.CreatePiece("REDLINE Waist Lower", lowerCenter, lowerCenter - up * 3f, floor, 1.1f);
            split = new WaistSplit(upper, lower);
            float bestPerimeter = 0;
            Vector3 wound = waist;
            var properties = new MaterialPropertyBlock();
            foreach (var source in target.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!source || !source.sharedMesh || source.bones.Length == 0)
                    continue;
                var skin = skins.Find(existing => existing.Source == source && existing.HasReplacement);
                if (skin != null)
                    split.cutDisplays.Add(skin);
                else if (source.forceRenderingOff)
                    continue;
                else
                    split.originals.Add(source);
                if (!source.enabled || !source.gameObject.activeInHierarchy)
                    continue;
                source.BakeMesh(baked);
                var binding = map.Find(source, rig);
                var vertices = baked.vertices;
                var positions = new Point[vertices.Length];
                var world = source.transform.localToWorldMatrix;
                for (int i = 0; i < positions.Length; i++)
                    positions[i] = ToPoint(world.MultiplyPoint3x4(vertices[i]));
                int[][] sections;
                int[][] stumpRims = null;
                if (skin != null)
                    skin.GetSplitTopology(out sections, out stumpRims);
                else
                {
                    sections = new int[baked.subMeshCount][];
                    for (int i = 0; i < sections.Length; i++)
                        sections[i] = baked.GetTriangles(i);
                }

                var welded = binding?.Welded ?? SkinMap.Weld(vertices);
                var cut = WaistGeometry.Split(positions, sections, welded, binding?.Labels, ToPoint(waist), ToPoint(up), stumpRims);
                if (stumpRims != null && cut.UpperStumps.Length + cut.LowerStumps.Length != stumpRims.Length)
                {
                    log($"waist split declined: split plane crosses an existing stump on {source.sharedMesh.name}");
                    return null;
                }
                source.GetPropertyBlock(properties);
                bool upperCap = split.AddSurface(source, baked, cut, cut.Upper, cut.UpperStumps, upper, properties);
                bool lowerCap = split.AddSurface(source, baked, cut, cut.Lower, cut.LowerStumps, lower, properties);
                log($"waist surface={source.sharedMesh.name} rimLoops={cut.Rims.Length} stumpRims={cut.UpperStumps.Length + cut.LowerStumps.Length} upperCap={upperCap} lowerCap={lowerCap}");
                if (cut.Rims.Length == 0 || !upperCap || !lowerCap || binding == null || SkinCut.IsEffectsPass(source))
                    continue;
                foreach (int[] rim in cut.Rims)
                {
                    Vector3 center = Vector3.zero;
                    float perimeter = 0;
                    Vector3 previous = WorldPoint(rim[rim.Length - 1]);
                    foreach (int index in rim)
                    {
                        Vector3 current = WorldPoint(index);
                        center += current;
                        perimeter += Vector3.Distance(previous, current);
                        previous = current;
                    }
                    center /= rim.Length;
                    if (perimeter > bestPerimeter && (center - waist).sqrMagnitude < 5.0f)
                    {
                        bestPerimeter = perimeter;
                        wound = center;
                    }
                }

                Vector3 WorldPoint(int index)
                {
                    var edge = cut.Intersections[index - positions.Length];
                    var p = Point.Lerp(positions[edge.A], positions[edge.B], edge.Fraction);
                    return new Vector3(p.X, p.Y, p.Z);
                }
            }
            if (bestPerimeter < 1.0f)
            {
                log("waist split declined: no closed waist rim on a mapped body surface");
                return null;
            }

            split.AddRigidAccessories(target, rig, waist, up);
            split.upperRim = upper.Body.transform.InverseTransformPoint(wound);
            split.lowerRim = lower.Body.transform.InverseTransformPoint(wound);
            split.upperNormal = upper.Body.transform.InverseTransformDirection(-up);
            split.lowerNormal = lower.Body.transform.InverseTransformDirection(up);
            Vector3 push = direction.sqrMagnitude > 0.01f ? direction
                : Battlefield.OnPlane(target.transform.forward).normalized;
            Vector3 roll = Vector3.Cross(Battlefield.Up, push);
            var tuning = physics.LaunchTuning;
            upper.Launch(push * tuning.Push(rifle ? 6.4f : 5.55f)
                + Battlefield.Up * tuning.VerticalSpeed(tuning.Extreme ? 1.55f : 0.54f),
                tuning.AngularSpeed(roll * (tuning.Extreme ? 2.15f : 1.46f)), 10f, trackWalls: true);
            lower.Launch(push * tuning.Push(rifle ? 3.3f : 2.55f)
                + Battlefield.Up * tuning.VerticalSpeed(tuning.Extreme ? 0.62f : 0.11f),
                tuning.AngularSpeed(roll * (tuning.Extreme ? 1.25f : 0.82f)), 8f, trackWalls: true);
            upper.IgnoreContact(lower);
            split.HideOriginals();
            log($"waist split started rim={bestPerimeter:F2} point={Battlefield.Format(wound)} upper={upper.Diagnostics} lower={lower.Diagnostics} extreme={tuning.Extreme}");
            started = true;
            return split;
        }
        finally
        {
            Object.Destroy(baked);
            if (!started)
            {
                if (split != null) split.Dispose();
                else
                {
                    upper.Dispose();
                    lower?.Dispose();
                }
            }
        }
    }

    private bool AddSurface(SkinnedMeshRenderer source, Mesh baked, WaistGeometry.Cut cut, int[][] triangles, int[][] stumps,
        DetachedLimb piece, MaterialPropertyBlock properties)
    {
        if (!Array.Exists(triangles, section => section.Length != 0))
            return false;
        Matrix4x4 transform = piece.Body.transform.worldToLocalMatrix * source.transform.localToWorldMatrix;
        Matrix4x4 normalTransform = transform.inverse.transpose;
        var positions = Copy(baked.vertices, Vector3.LerpUnclamped);
        for (int i = 0; i < positions.Length; i++)
            positions[i] = transform.MultiplyPoint3x4(positions[i]);
        var normals = Copy(baked.normals, Vector3.LerpUnclamped);
        for (int i = 0; i < normals.Length; i++)
            normals[i] = normalTransform.MultiplyVector(normals[i]).normalized;
        var tangents = Copy(baked.tangents, Vector4.LerpUnclamped);
        for (int i = 0; i < tangents.Length; i++)
        {
            var value = tangents[i];
            var axis = transform.MultiplyVector(new Vector3(value.x, value.y, value.z)).normalized;
            tangents[i] = new Vector4(axis.x, axis.y, axis.z, value.w);
        }

        var fragment = new Mesh { name = "REDLINE Waist Surface", indexFormat = baked.indexFormat };
        if (positions.Length > ushort.MaxValue)
            fragment.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        piece.Meshes.Add(fragment);
        fragment.vertices = positions;
        fragment.normals = normals;
        fragment.tangents = tangents;
        fragment.uv = Copy(baked.uv, Vector2.LerpUnclamped);
        fragment.uv2 = Copy(baked.uv2, Vector2.LerpUnclamped);
        fragment.colors32 = Copy(baked.colors32, Color32.LerpUnclamped);
        fragment.subMeshCount = triangles.Length;
        var sections = new Il2CppStructArray<int>[triangles.Length];
        for (int i = 0; i < sections.Length; i++)
        {
            sections[i] = triangles[i];
            fragment.SetTriangles(sections[i], i, false, 0);
        }
        fragment.RecalculateBounds();
        bool baseMesh = !SkinCut.IsEffectsPass(source);
        if (baseMesh)
            piece.AddSurface(positions, sections);

        var surface = new GameObject("REDLINE Waist Skin") { layer = source.gameObject.layer };
        surface.transform.SetParent(piece.Body.transform, false);
        SetRenderer(surface, source, fragment, properties);
        if (baseMesh)
            shotSurfaces.Add((fragment, surface.transform));
        AddCap(stumps);
        return AddCap(cut.Rims);

        bool AddCap(int[][] rims)
        {
            var cap = CutCap.Create(surface.transform, surface.layer, positions, rims);
            if (cap == null)
                return false;
            piece.Meshes.Add(cap.Mesh);
            if (baseMesh)
                shotSurfaces.Add((cap.Mesh, surface.transform));
            return true;
        }

        Il2CppStructArray<T> Copy<T>(Il2CppStructArray<T> values, Func<T, T, float, T> interpolate) where T : unmanaged
        {
            if (values.Length == 0)
                return values;
            var result = new Il2CppStructArray<T>(values.Length + cut.Intersections.Length);
            for (int i = 0; i < values.Length; i++)
                result[i] = values[i];
            for (int i = 0; i < cut.Intersections.Length; i++)
            {
                var edge = cut.Intersections[i];
                result[values.Length + i] = interpolate(values[edge.A], values[edge.B], edge.Fraction);
            }
            return result;
        }
    }

    private void AddRigidAccessories(EnemyController target, Rig rig, Vector3 waist, Vector3 up)
    {
        if (!target.animator)
            return;
        foreach (var source in target.animator.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (!source || source.forceRenderingOff || source.name.StartsWith("REDLINE", StringComparison.Ordinal))
                continue;
            if (SkinCut.IsStockGlitchQuad(source))
                continue;
            if (!source.transform.IsChildOf(rig.Hips)
                || (target.squibs && source.transform.IsChildOf(target.squibs.transform))
                || (target.flares && source.transform.IsChildOf(target.flares.transform)))
                continue;
            var filter = source.GetComponent<MeshFilter>();
            if (!filter || !filter.sharedMesh)
                continue;
            originals.Add(source);
            if (!source.enabled || !source.gameObject.activeInHierarchy)
                continue;
            bool above = source.transform.IsChildOf(rig.Chest) || Vector3.Dot(source.bounds.center - waist, up) >= 0;
            SkinCut.CopyAccessory(source, (above ? upper : lower).Body.transform);
        }
    }

    private static void SetRenderer(GameObject surface, Renderer source, Mesh mesh, MaterialPropertyBlock properties)
    {
        surface.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = surface.AddComponent<MeshRenderer>();
        SkinCut.CopyRenderSettings(source, renderer);
        renderer.SetPropertyBlock(properties);
    }

    private static Point ToPoint(Vector3 v) => new(v.x, v.y, v.z);

    private void HideOriginals()
    {
        if (!originalsHidden)
            foreach (var skin in cutDisplays)
                skin.SetSplitHidden(true);
        originalsHidden = true;
        foreach (var renderer in originals)
            if (renderer && !renderer.forceRenderingOff)
                renderer.forceRenderingOff = true;
    }

    public Vector3 Travel => Battlefield.OnPlane(upper.Body.transform.position - origin);
    public Vector3 StompPoint => upper.Body.transform.position;

    public void AddShotSurfaces(ShotHitboxes.Geometry body)
    {
        foreach (var (mesh, frame) in shotSurfaces)
            body.Add(mesh, frame);
    }

    public void Stomp(Vector3 direction, Vector3 point)
    {
        upper.Kick(direction * 1.1f + Battlefield.Down * 0.85f, point);
        lower.Kick(direction * 0.45f, lower.Body.transform.position);
    }

    public Vector3 SpillPoint
    {
        get
        {
            var a = upper.Body.transform.TransformPoint(upperRim);
            var b = lower.Body.transform.TransformPoint(lowerRim);
            return Battlefield.Height(a) < Battlefield.Height(b) ? a : b;
        }
    }

    public void Tick(float dt, Footstep foot, BloodEffects blood, float amount, float floor, int mask)
    {
        HideOriginals();
        upper.Nudge(foot);
        lower.Nudge(foot);
        upper.Tick(dt);
        lower.Tick(dt);
        Emit(upper, upperFlow, upperRim, upperNormal);
        Emit(lower, lowerFlow, lowerRim, lowerNormal);

        void Emit(DetachedLimb piece, StumpBleed flow, Vector3 localPoint, Vector3 localNormal)
        {
            float pressure = flow.Pulse(Time.time, true, 0);
            if (pressure <= 0 || amount <= 0)
                return;
            var pose = piece.Body.transform;
            var point = pose.TransformPoint(localPoint);
            var outward = pose.TransformDirection(localNormal);
            var velocity = Vector3.ClampMagnitude(piece.PointVelocity(point), 12f);
            if (blood.Drip(point, outward, velocity, Battlefield.FloorBelow(point, floor), mask, amount, pressure))
                emissions++;
        }
    }

    public string Diagnostics => $"waist split released emissions={emissions}";

    public void Dispose()
    {
        upper.Dispose();
        lower.Dispose();
        if (originalsHidden)
        {
            foreach (var renderer in originals)
                if (renderer) renderer.forceRenderingOff = false;
            foreach (var skin in cutDisplays)
                skin.SetSplitHidden(false);
        }
        originals.Clear();
    }
}
