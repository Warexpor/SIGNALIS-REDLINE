using System;
using System.Collections.Generic;
using UnityEngine;
using Point = System.Numerics.Vector3;
using UV = System.Numerics.Vector2;
using Object = UnityEngine.Object;

namespace Redline;

internal sealed class ShotHitboxes(Func<EnemyController, EnemyEffects> tracked)
{
    // Layer 12 contains the joint and downed boxes. Layer 18 contains the root target capsule.
    private const int CombatLayers = (1 << 12) | (1 << 18);
    private readonly Dictionary<int, Geometry> geometry = new();
    private EnemyController[] candidates;

    public TraceResult Trace(Vector3 origin, Vector3 direction, float range, int mask)
    {
        if ((mask & CombatLayers) == 0 || range <= 0 || direction.sqrMagnitude < 0.000001f)
            return null;
        candidates ??= Object.FindObjectsOfType<EnemyController>();
        var ray = new Ray(origin, direction);
        bool hitTriggers = Physics.queriesHitTriggers;
        TraceResult result = null;
        foreach (var target in candidates)
        {
            // Keep the radio encounter's native ray routing between the real body and projections.
            if (!target || !target.isActiveAndEnabled || !target.hitbox || !EnemyEffects.IsStandardReplika(target))
                continue;
            var effects = tracked(target);
            Vector3 center = effects?.ShotCenter ?? target.transform.position + Battlefield.Up * 4.5f;
            float along = Mathf.Clamp(Vector3.Dot(center - origin, ray.direction), 0, range);
            if ((center - ray.GetPoint(along)).sqrMagnitude > 12f * 12f)
                continue;

            int id = target.GetInstanceID();
            if (!geometry.TryGetValue(id, out var body))
            {
                body = new Geometry();
                if (effects == null)
                    AddNativeSurfaces(target, body);
                else
                    effects.AddShotSurfaces(body);
                if (body.HasSurfaces)
                    body.SetColliders(target);
                geometry.Add(id, body);
            }
            bool nativeCollider = body.TryCollider(mask, hitTriggers, out int collider);
            bool prone = IsProne(target);
            if (!body.HasSurfaces || (!nativeCollider && !prone))
                continue;
            result ??= new TraceResult();
            bool hitsSurface = body.TryHit(ray, range, collider, out var hit);
            if (hitsSurface && prone)
                result.ProneHits.Add((target, hit));
            if (!nativeCollider)
                continue;
            if (!hitsSurface && target.state != EnemyController.enemystate.dead && target.hitbox.HP > 0
                && (effects?.HasGroundAimPose == true
                    || target.staggerType is EnemyController.hurtState.critical or EnemyController.hurtState.fire))
            {
                // Lock-on can continue aiming above a prone model. Retain the native hitbox if the exact ray misses.
                body.AddReplacedColliders(result.DownedFallback, mask, hitTriggers);
                continue;
            }
            body.AddReplacedColliders(result.StockColliders, mask, hitTriggers);
            if (hitsSurface)
                result.Hits.Add(hit);
        }
        return result;
    }

    public static bool IsProne(EnemyController target) =>
        target.staggerType != EnemyController.hurtState.fire &&
        (target.state == EnemyController.enemystate.dead || target.staggerType == EnemyController.hurtState.critical);

    public static void AddNativeSurfaces(EnemyController target, Geometry body, Func<SkinnedMeshRenderer, bool> addExisting = null)
    {
        Mesh buffer = null;
        try
        {
            foreach (var source in target.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                if (!source || !source.enabled || !source.sharedMesh || SkinCut.IsEffectsPass(source))
                    continue;
                if (addExisting?.Invoke(source) == true || source.forceRenderingOff)
                    continue;
                buffer ??= new Mesh { name = "REDLINE Shot Pose" };
                source.BakeMesh(buffer);
                body.Add(buffer, source.transform);
            }
        }
        finally { if (buffer) Object.Destroy(buffer); }
    }

    internal sealed class TraceResult
    {
        public readonly HashSet<int> StockColliders = new();
        public readonly HashSet<int> DownedFallback = new();
        public readonly List<RaycastHit> Hits = new();
        public readonly List<(EnemyController Target, RaycastHit Hit)> ProneHits = new();
    }

    internal sealed class Geometry
    {
        private readonly List<Surface> surfaces = new();
        private readonly List<NativeCollider> colliders = new();
        public bool HasSurfaces => surfaces.Count != 0;

        public void Add(Mesh mesh, Transform frame)
        {
            if (!mesh || !frame || mesh.vertexCount == 0)
                return;
            int[] triangles = null;
            MeshArrays.Read(mesh.triangles, ref triangles);
            if (triangles.Length == 0)
                return;
            Vector3[] local = null;
            MeshArrays.Read(mesh.vertices, ref local);
            var world = frame.localToWorldMatrix;
            var vertices = new Point[local.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                var p = local[i];
                vertices[i] = new Point(world.m00 * p.x + world.m01 * p.y + world.m02 * p.z + world.m03,
                    world.m10 * p.x + world.m11 * p.y + world.m12 * p.z + world.m13,
                    world.m20 * p.x + world.m21 * p.y + world.m22 * p.z + world.m23);
            }
            var min = vertices[triangles[0]];
            var max = min;
            foreach (int i in triangles)
            {
                min = Point.Min(min, vertices[i]);
                max = Point.Max(max, vertices[i]);
            }
            var bounds = new Bounds(ToUnity((min + max) * 0.5f), ToUnity(max - min) + Vector3.one * 0.002f);
            surfaces.Add(new Surface(vertices, triangles, bounds));
        }

        public void SetColliders(EnemyController target)
        {
            var downed = target.downedHitbox;
            foreach (var collider in target.GetComponentsInChildren<Collider>(false))
            {
                if (!collider || !collider.enabled || collider.GetComponentInParent<EnemyController>() != target)
                    continue;
                var obj = collider.gameObject;
                int bit = 1 << obj.layer;
                if ((bit & CombatLayers) == 0)
                    continue;
                int priority = downed && obj == downed ? 3 : collider == target._collider ? 1 : 2;
                colliders.Add(new NativeCollider(collider.GetInstanceID(), bit, collider.isTrigger, priority));
            }
            colliders.Sort((first, second) => second.Priority.CompareTo(first.Priority));
        }

        public bool TryCollider(int mask, bool hitTriggers, out int collider)
        {
            foreach (var native in colliders)
            {
                if (!native.Included(mask, hitTriggers))
                    continue;
                collider = native.Id;
                return true;
            }
            collider = 0;
            return false;
        }

        public void AddReplacedColliders(HashSet<int> replaced, int mask, bool hitTriggers)
        {
            foreach (var collider in colliders)
                if (collider.Included(mask, hitTriggers))
                    replaced.Add(collider.Id);
        }

        public bool TryHit(Ray ray, float nearest, int collider, out RaycastHit hit)
        {
            hit = default;
            int face = -1;
            Point normal = default;
            UV uv = default;
            var origin = Rig.ToPoint(ray.origin);
            var direction = Rig.ToPoint(ray.direction);
            foreach (var surface in surfaces)
            {
                if (!surface.Bounds.IntersectRay(ray, out float entry) || entry > nearest)
                    continue;
                if (MeshRay.Trace(surface.Vertices, surface.Triangles, origin, direction, ref nearest,
                    out var hitNormal, out int hitFace, out var hitUv))
                    (normal, face, uv) = (hitNormal, hitFace, hitUv);
            }
            if (face < 0)
                return false;
            hit = new RaycastHit
            {
                m_Point = ray.GetPoint(nearest), m_Normal = ToUnity(normal), m_Distance = nearest,
                m_Collider = collider, m_FaceID = (uint)face, m_UV = new Vector2(uv.X, uv.Y)
            };
            return true;
        }

        private static Vector3 ToUnity(Point value) => new(value.X, value.Y, value.Z);
        private sealed record Surface(Point[] Vertices, int[] Triangles, Bounds Bounds);
        private readonly record struct NativeCollider(int Id, int Layer, bool Trigger, int Priority)
        {
            public bool Included(int mask, bool hitTriggers) => (Layer & mask) != 0 && (!Trigger || hitTriggers);
        }
    }
}
