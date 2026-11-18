using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace Redline;

internal sealed class BloodEffects
{
    private const int MaxDrops = 280;
    private const float SurfaceOffset = 0.065f;
    private static readonly Color32 Wet = new(87, 8, 17, 255);
    private static readonly Color32 Dark = new(33, 2, 8, 255);
    private readonly List<Drop> drops = new(MaxDrops);
    private readonly List<Splat> stains = new(420);
    private readonly List<Splat> pools = new(90);
    private readonly List<Vector3> points = new(3000);
    private readonly List<Color32> colors = new(3000);
    private readonly List<int> indices = new(4500);
    private GameObject root;
    private Mesh mesh;
    private Material material;

    public void Spurt(Vector3 origin, Vector3 direction, float floor, int wallMask, float scale, int count, float force)
    {
        if (scale <= 0)
            return;
        if (!EnsureMesh())
            return;
        if (Battlefield.Height(origin) < floor + SurfaceOffset)
            origin = Battlefield.AtHeight(origin, floor + SurfaceOffset);
        int amount = Mathf.RoundToInt(count * Mathf.Clamp(scale, 0, 2.5f));
        for (int i = 0; i < amount && drops.Count < MaxDrops; i++)
        {
            var spread = Random.insideUnitCircle * Random.Range(2.2f, 5.6f);
            var velocity = (direction * Random.Range(4.4f, 12.5f) + new Vector3(spread.x, spread.y, 0)
                + Battlefield.Up * Random.Range(-1.3f, 5.3f)) * force;
            float radius = Random.Range(0.055f, 0.22f) * Mathf.Lerp(0.54f, 1f, force);
            drops.Add(new Drop(origin + Random.insideUnitSphere * 0.16f, velocity, floor,
                wallMask | Battlefield.WalkableSurface, radius, Random.Range(0.13f, 0.82f), Time.time + 3.5f));
        }
    }

    public void Pool(Vector3 origin, float floor, float scale)
    {
        if (scale <= 0 || !EnsureMesh())
            return;
        origin = Battlefield.AtHeight(origin, floor + SurfaceOffset);
        float size = Mathf.Sqrt(Mathf.Clamp(scale, 0.08f, 2.5f));
        Vector3 spread = Random.onUnitSphere;
        AddStain(pools, origin, Battlefield.Up, spread, 1.7f * size, 1.48f, 12, 3.2f, 0.60f);
        for (int i = 0; i < 4; i++)
        {
            var offset = Random.insideUnitCircle * 1.8f * size;
            var edge = origin + new Vector3(offset.x, offset.y, -0.003f * (i + 1));
            AddStain(pools, edge, Battlefield.Up, spread, Random.Range(0.45f, 0.93f) * size,
                Random.Range(1.1f, 1.8f), 8, 1.7f, Random.Range(0.44f, 0.76f));
        }
    }

    public void Trail(Vector3 position, float floor, float scale)
    {
        if (scale <= 0 || !EnsureMesh())
            return;
        position = Battlefield.AtHeight(position, floor + SurfaceOffset);
        AddStain(stains, position, Battlefield.Up, Random.onUnitSphere, Random.Range(0.26f, 0.61f) * Mathf.Sqrt(scale),
            1.7f, 7, 0.4f, Random.Range(0.44f, 0.84f));
    }

    public void Tick()
    {
        if (!root || !mesh)
            return;
        float dt = Mathf.Min(Time.deltaTime, 0.045f);
        float now = Time.time;
        bool hadDrops = drops.Count != 0;
        for (int i = drops.Count - 1; i >= 0; i--)
        {
            var drop = drops[i];
            if (drop.Expires <= now)
            {
                RemoveDrop(i);
                continue;
            }
            drop.Velocity += Battlefield.Down * (10f * dt);
            Vector3 move = drop.Velocity * dt;
            Vector3 next = drop.Position + move;
            float nextHeight = Battlefield.Height(next);
            float distance = move.magnitude;
            if (distance > 0.001f &&
                Physics.Raycast(drop.Position, move / distance, out RaycastHit hit, distance,
                    drop.WallMask, QueryTriggerInteraction.Ignore))
            {
                Splash(drop, hit.point, hit.normal);
                RemoveDrop(i);
            }
            else if (nextHeight <= drop.Floor)
            {
                float before = Battlefield.Height(drop.Position);
                float crossing = Mathf.Clamp01((before - drop.Floor) / Mathf.Max(0.001f, before - nextHeight));
                var landing = Battlefield.AtHeight(drop.Position + move * crossing, drop.Floor);
                Splash(drop, landing, Battlefield.Up);
                RemoveDrop(i);
            }
            else
            {
                drop.Position = next;
                drops[i] = drop;
            }
        }

        int removed = stains.RemoveAll(stain => now - stain.Start > 100f);
        removed += pools.RemoveAll(stain => now - stain.Start > 125f);
        if (!hadDrops && now < nextMeshRefresh && removed == 0 && !stainsDirty)
            return;
        nextMeshRefresh = float.PositiveInfinity;
        stainsDirty = false;
        points.Clear();
        colors.Clear();
        indices.Clear();
        DrawStains(stains, now, 100f);
        DrawStains(pools, now, 125f);
        var camera = Camera.main;
        Vector3 normal = camera ? -camera.transform.forward : Battlefield.Up;
        foreach (var drop in drops)
            AddPolygon(drop.Position, normal, drop.Velocity, drop.Size, 1.9f,
                drop.Tone, drop.Tone * 6, 4, 1);

        mesh.Clear();
        if (points.Count == 0)
            return;
        mesh.vertices = points.ToArray();
        mesh.colors32 = colors.ToArray();
        mesh.triangles = indices.ToArray();
        mesh.RecalculateBounds();
    }

    private void DrawStains(List<Splat> layer, float now, float lifetime)
    {
        foreach (var stain in layer)
        {
            float age = now - stain.Start;
            float spread = Mathf.Clamp01(age / stain.SpreadTime);
            float grow = Mathf.Lerp(0.48f, 1f, 1f - (1f - spread) * (1f - spread));
            float change = spread < 1f ? now + 0.06f :
                age < lifetime - 8f ? stain.Start + lifetime - 8f : now + 0.14f;
            nextMeshRefresh = Mathf.Min(nextMeshRefresh, change);
            AddPolygon(stain.Position, stain.Normal, stain.Direction, stain.Size * grow, stain.Stretch, stain.Tone,
                stain.Seed, stain.Sides, Mathf.Clamp01((lifetime - age) / 8f));
        }
    }

    private float nextMeshRefresh;
    private bool stainsDirty;

    public void Clear()
    {
        drops.Clear();
        stains.Clear();
        pools.Clear();
        stainsDirty = false;
        nextMeshRefresh = 0;
        if (root) Object.Destroy(root);
        if (mesh) Object.Destroy(mesh);
        if (material) Object.Destroy(material);
        root = null;
        mesh = null;
        material = null;
    }

    private bool EnsureMesh()
    {
        if (root)
            return true;
        var shader = Shader.Find("Sprites/Default");
        if (!shader)
            shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
        if (!shader)
            return false;
        material = new Material(shader) { name = "REDLINE Blood" };
        material.mainTexture = Texture2D.whiteTexture;
        root = new GameObject("REDLINE Blood");
        mesh = new Mesh { name = "REDLINE Blood Batch" };
        mesh.MarkDynamic();
        root.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = root.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return true;
    }

    private void RemoveDrop(int index)
    {
        drops[index] = drops[drops.Count - 1];
        drops.RemoveAt(drops.Count - 1);
    }

    private void Splash(Drop drop, Vector3 point, Vector3 normal)
    {
        var tangent = Vector3.ProjectOnPlane(drop.Velocity, normal);
        var position = point + normal * (SurfaceOffset + 0.02f * drop.Tone);
        float stretch = Mathf.Clamp(1.14f + tangent.magnitude * 0.11f, 1.2f, 2.65f);
        AddStain(stains, position, normal, tangent, drop.Size * 2.5f, stretch, 7, 0.36f, drop.Tone);
        if (drop.Size >= 0.16f && tangent.sqrMagnitude > 16f)
        {
            var drift = tangent.normalized * drop.Size * Random.Range(3.6f, 6.3f);
            AddStain(stains, position + drift, normal, tangent, drop.Size * 0.80f, stretch,
                5, 0.27f, drop.Tone);
        }
    }

    private void AddStain(List<Splat> layer, Vector3 pos, Vector3 normal, Vector3 direction,
        float size, float stretch, int sides, float spreadTime, float tone)
    {
        if (layer.Count == layer.Capacity)
            layer.RemoveAt(0);
        layer.Add(new Splat(pos, normal, direction, size, stretch, tone, Random.Range(0, 6.28f), sides,
            Time.time, spreadTime));
        stainsDirty = true;
    }

    private void AddPolygon(Vector3 pos, Vector3 normal, Vector3 direction, float size, float stretch, float tone,
        float seed, int sides, float alpha)
    {
        Vector3 right = Vector3.ProjectOnPlane(direction, normal);
        if (right.sqrMagnitude < 0.001f)
            right = Vector3.Cross(normal, Mathf.Abs(normal.x) < 0.7f ? Vector3.right : Vector3.up);
        right.Normalize();
        Vector3 up = Vector3.Cross(normal, right);
        int first = points.Count;
        var tint = Color32.Lerp(Wet, Dark, tone);
        tint.a = (byte)(255 * alpha);
        points.Add(pos);
        colors.Add(tint);
        for (int j = 0; j < sides; j++)
        {
            float angle = seed + j * ((float)Math.PI * 2 / sides);
            float edge = size * (0.80f + 0.28f * Mathf.Sin(j * 2.4f + seed * 9));
            points.Add(pos + (right * Mathf.Cos(angle) * stretch + up * Mathf.Sin(angle)) * edge);
            colors.Add(tint);
        }
        for (int j = 0; j < sides; j++)
        {
            indices.Add(first);
            indices.Add(first + 1 + j);
            indices.Add(first + 1 + (j + 1) % sides);
        }
    }

    private record struct Drop(Vector3 Position, Vector3 Velocity, float Floor, int WallMask,
        float Size, float Tone, float Expires);
    private readonly record struct Splat(Vector3 Position, Vector3 Normal, Vector3 Direction, float Size,
        float Stretch, float Tone, float Seed, int Sides, float Start, float SpreadTime);
}
