using System;
using System.Collections.Generic;
using UnhollowerBaseLib;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace Redline;

internal sealed class BloodEffects(RedlineSettings settings, CutSounds sounds, Action<string> log)
{
    private const int MaxDrops = 384;
    private const int MaxFreshStains = 920;
    private const int StainBatchSize = 112;
    private const int PoolCoreSides = 11;
    private const float SurfaceOffset = 0.12f;
    private const float WallOffset = 0.055f;
    private const float StainDryTime = 1.1f;
    private const float PoolDryTime = 28f;
    private const float DropGravity = EffectsPhysics.Gravity * 0.76f;
    private readonly BloodLighting lighting = BloodLighting.Room;
    private readonly List<Drop> drops = new(MaxDrops);
    private readonly List<Splat> stains = new(256);
    private readonly Dictionary<string, LevelBlood> visitedLevels = new();
    private readonly Queue<StainBatch> pendingBatches = new();
    private string levelName = "(starting)";
    private LevelBlood level = new();
    private List<Splat> pools => level.GrowingPools;
    private readonly List<Vector3> points = new(3000);
    private readonly List<Color32> colors = new(3000);
    private readonly List<Vector2> textureCoordinates = new(3000);
    private readonly List<int> indices = new(4500);
    private Vector3[] vertexStaging;
    private Vector2[] uvStaging;
    private Color32[] colorStaging;
    private int[] indexStaging;
    private Il2CppStructArray<Vector3> meshVertices;
    private Il2CppStructArray<Vector2> meshUv;
    private Il2CppStructArray<Color32> meshColors;
    private Il2CppStructArray<int> meshIndices;
    private GameObject root;
    private Mesh stainMesh;
    private Mesh poolMesh;
    private Mesh airborneMesh;
    private Material material;
    private float nextLog;
    private int floorHits;
    private int wallHits;
    private int merged;
    private int woundDrops;
    private bool shaderMissing;
    private float dropTime;

    public void Prepare() => EnsureMesh();

    public void Spurt(Vector3 origin, Vector3 direction, float floor, int wallMask, float scale, int count, float force,
        bool ballistic = false)
    {
        scale = Mathf.Min(scale, 2.5f) * RedlineSettings.Factor(settings.Spray);
        if (scale <= 0)
            return;
        if (!EnsureMesh())
            return;
        if (Battlefield.Height(origin) < floor + SurfaceOffset)
            origin = Battlefield.AtHeight(origin, floor + SurfaceOffset);
        ballistic &= direction.sqrMagnitude > 0.01f;
        var side = Vector3.zero;
        var rise = Vector3.zero;
        float skim = 0f;
        if (ballistic)
        {
            direction = direction.normalized;
            side = Vector3.Cross(direction, Battlefield.Up).normalized;
            if (side == Vector3.zero)
                side = Vector3.right;
            rise = Vector3.Cross(side, direction);
            skim = 1f - Mathf.InverseLerp(SurfaceOffset, 1.4f, Battlefield.Height(origin) - floor);
        }
        int amount = Mathf.RoundToInt(count * scale);
        int emitted = Mathf.Min(amount, MaxDrops - drops.Count);
        int surfaceMask = wallMask | Battlefield.RoomSolids | Battlefield.WalkableSurface;
        RaycastHit wall = default;
        bool nearWall = !ballistic && settings.WallSplatter.Value && emitted >= 5
            && FindWall(origin, direction, floor, wallMask | Battlefield.RoomSolids, out wall);
        lighting.Refresh(Time.time);
        var tint = lighting.SurfaceTint(origin, Battlefield.Up);
        if (count > 3)
            log($"blood burst origin={Battlefield.Format(origin)} floor={floor:F2} count={emitted}/{amount} strength={force:F2} ballistic={ballistic} wallFan={nearWall}");
        for (int i = 0; i < emitted; i++)
        {
            bool jet = i % 3 == 0;
            Vector3 velocity;
            if (ballistic)
            {
                float speed = jet ? Random.Range(15f, 23f) : Random.Range(9f, 15f);
                var scatter = Random.insideUnitCircle * (jet ? 1.8f : 3.65f);
                float lift = Mathf.Lerp(0.45f, 3.0f, skim);
                // Spread around the ray. Close-to-floor hits carry a low spray along the surface.
                velocity = (direction * speed + side * scatter.x + rise * (lift + scatter.y * 0.65f)) * force;
            }
            else
            {
                float momentum = jet ? Random.Range(6.5f, 10.5f) : Random.Range(2.1f, 6.2f);
                var scatter = Random.insideUnitCircle * (jet ? 1.8f : 4.65f);
                velocity = (direction * momentum + new Vector3(scatter.x, scatter.y, 0)
                    + Battlefield.Up * Random.Range(-1.2f, jet ? 3.0f : 4.6f)) * force;
            }
            float radius = Random.Range(0.053f, jet ? 0.12f : 0.18f) * Mathf.Lerp(0.65f, 1f, force);
            var start = origin + Random.insideUnitSphere * 0.10f;
            if (nearWall && i % 5 == 2)
            {
                var spread = Vector3.ProjectOnPlane(Random.insideUnitSphere, wall.normal) * 1.05f;
                var target = wall.point + spread - wall.normal * 0.15f;
                target = Battlefield.AtHeight(target, Mathf.Max(floor + 0.20f, Battlefield.Height(target)));
                float flight = Mathf.Clamp((target - start).magnitude / (24f * force), 0.12f, 0.42f);
                velocity = (target - start) / flight + Battlefield.Up * (DropGravity * flight * 0.5f);
            }
            drops.Add(new Drop(start, velocity, floor, surfaceMask,
                radius, Random.Range(0.13f, 0.82f), Time.time + 1.6f, false, tint));
        }
    }

    private static bool FindWall(Vector3 origin, Vector3 direction, float floor, int mask, out RaycastHit wall)
    {
        wall = default;
        Vector3 heading = Battlefield.OnPlane(direction).normalized;
        if (heading.sqrMagnitude < 0.5f)
            return false;
        var side = new Vector3(-heading.y, heading.x, 0);
        var probe = Battlefield.AtHeight(origin, Mathf.Max(floor + 0.85f, Battlefield.Height(origin)));
        bool found = false;
        float reach = 9.5f;
        for (int i = -1; i <= 1; i++)
        {
            var ray = (heading + side * (i * 1.65f)).normalized;
            if (!Physics.Raycast(probe, ray, out var hit, reach, mask, QueryTriggerInteraction.Ignore)
                || Mathf.Abs(Vector3.Dot(hit.normal, Battlefield.Up)) > 0.55f)
                continue;
            wall = hit;
            reach = hit.distance;
            found = true;
        }
        return found;
    }

    public void Pool(Vector3 origin, float floor, float scale)
    {
        float sizeMultiplier = RedlineSettings.Factor(settings.Pools);
        if (scale <= 0 || sizeMultiplier <= 0 || !EnsureMesh())
            return;
        lighting.Refresh(Time.time);
        origin = Battlefield.AtHeight(origin, floor + SurfaceOffset);
        float size = Mathf.Sqrt(Mathf.Clamp(scale, 0.08f, 2.5f)) * sizeMultiplier;
        Vector2 heading = Random.insideUnitCircle.normalized;
        Vector3 spread = new Vector3(heading.x, heading.y, 0);
        AddStain(pools, origin, Battlefield.Up, spread, 1.66f * size, 1.36f, PoolCoreSides, 9f, 0.51f);
        for (int i = 0; i < 3; i++)
        {
            var offset = Random.insideUnitCircle * (1.8f * size);
            var depth = origin + Battlefield.Up * (0.012f * (i + 1));
            AddStain(pools, depth, Battlefield.Up, spread, Random.Range(0.42f, 0.66f) * size,
                Random.Range(1.15f, 1.60f), 8, Random.Range(7.5f, 9.5f), Random.Range(0.47f, 0.72f),
                offset: new Vector3(offset.x, offset.y, 0), delay: 0.65f + i * 0.48f);
        }
    }

    public bool Drip(Vector3 origin, Vector3 outward, Vector3 motion, float floor, int wallMask,
        float scale, float pressure, ref bool playedWoundSound)
    {
        scale *= RedlineSettings.Factor(settings.WoundFlow);
        if (scale <= 0 || drops.Count == MaxDrops || Random.value > Mathf.Clamp01(scale * 1.2f) || !EnsureMesh())
            return false;
        origin += outward * 0.045f;
        if (Battlefield.Height(origin) < floor + 0.08f)
            origin = Battlefield.AtHeight(origin, floor + 0.08f);
        lighting.Refresh(Time.time);
        var tint = lighting.SurfaceTint(origin, Battlefield.Up);

        int count = Mathf.Clamp(Mathf.RoundToInt((1 + pressure * 3.2f) * scale), 1, 7);
        for (int i = 0; i < count && drops.Count < MaxDrops; i++)
        {
            var velocity = motion * 0.40f + outward * Random.Range(0.36f, 3.1f) * pressure
                + Battlefield.Down * Random.Range(0.35f, 1.2f) + Random.insideUnitSphere * 0.30f;
            float size = Random.Range(0.055f, 0.12f) * Mathf.Lerp(0.70f, 1.15f, pressure);
            drops.Add(new Drop(origin + Random.insideUnitSphere * 0.047f, velocity, floor,
                wallMask | Battlefield.RoomSolids | Battlefield.WalkableSurface, size, Random.Range(0.08f, 0.36f), Time.time + 0.9f, true, tint));
            woundDrops++;
        }
        if (!playedWoundSound)
            playedWoundSound = sounds.PlayBleed(origin, pressure);
        return true;
    }

    public void Tick()
    {
        if (!root || !stainMesh || !airborneMesh)
            return;
        float now = Time.time;
        bool hadDrops = drops.Count != 0;
        dropTime = hadDrops ? dropTime + Mathf.Min(Time.deltaTime, 0.045f) : 0;
        if (dropTime >= 1f / 60f)
        {
            TickDrops(dropTime, now);
            dropTime = 0;
            DrawAirborne();
        }

        if (now >= nextSettle)
            UpdateSettledStains(now);
        UploadSettledStains(now);
        if (pools.Count != 0 && now >= nextPoolLighting)
            RelightPools(now);
        if (now >= nextStainUpload && (stainsDirty || now >= nextMeshRefresh))
        {
            nextStainUpload = now + 0.040f;
            stainsDirty = false;
            ClearBatch();
            nextMeshRefresh = DrawStains(stains, now, true);
            Upload(stainMesh);
        }
        if (now >= nextPoolUpload && (poolsDirty || now >= nextPoolRefresh))
        {
            nextPoolUpload = now + 0.075f;
            poolsDirty = false;
            ClearBatch();
            nextPoolRefresh = DrawStains(pools, now, true);
            Upload(poolMesh);
        }
        if (floorHits + wallHits + merged + woundDrops > 0 && now >= nextLog)
        {
            log($"blood contacts floor={floorHits} walls={wallHits} merged={merged} woundDrops={woundDrops} drops={drops.Count} freshStains={stains.Count} retainedMarks={level.MarkCount} growingPools={pools.Count}");
            floorHits = wallHits = merged = woundDrops = 0;
            nextLog = now + 1f;
        }
    }

    private void TickDrops(float dt, float now)
    {
        lighting.Refresh(now);
        var gravity = Battlefield.Down * DropGravity;
        for (int i = drops.Count - 1; i >= 0; i--)
        {
            var drop = drops[i];
            if (drop.Expires <= now)
            {
                RemoveDrop(i);
                continue;
            }
            Vector3 move = (drop.Velocity + gravity * (dt * 0.5f)) * dt;
            drop.Velocity += gravity * dt;
            Vector3 next = drop.Position + move;
            float nextHeight = Battlefield.Height(next);
            float distance = move.magnitude;
            if (distance > 0.001f &&
                Physics.Raycast(drop.Position, move / distance, out RaycastHit hit, distance,
                    drop.WallMask, QueryTriggerInteraction.Ignore))
            {
                float alignment = Vector3.Dot(hit.normal, Battlefield.Up);
                if (alignment > 0.65f) floorHits++;
                else if (alignment > -0.65f) wallHits++;
                Splash(drop, hit.point, hit.normal);
                RemoveDrop(i);
            }
            else if (nextHeight <= drop.Floor)
            {
                float before = Battlefield.Height(drop.Position);
                float crossing = Mathf.Clamp01((before - drop.Floor) / Mathf.Max(0.001f, before - nextHeight));
                var landing = drop.Position + move * crossing;
                landing = Battlefield.AtHeight(landing, Battlefield.FloorBelow(landing, drop.Floor));
                floorHits++;
                Splash(drop, landing, Battlefield.Up);
                RemoveDrop(i);
            }
            else
            {
                drop.Position = next;
                drops[i] = drop;
            }
        }

    }

    private void UpdateSettledStains(float now)
    {
        nextSettle = now + 0.42f;
        if (Settle(stains, now - StainDryTime))
        {
            stainsDirty = true;
            nextStainUpload = now;
        }
        if (Settle(pools, now - PoolDryTime))
        {
            poolsDirty = true;
            nextPoolUpload = now;
        }
    }

    private bool Settle(List<Splat> layer, float before)
    {
        int kept = 0;
        int count = layer.Count;
        for (int i = 0; i < count; i++)
        {
            var stain = layer[i];
            if (stain.Start <= before)
                Retain(stain);
            else
                layer[kept++] = stain;
        }
        if (kept == count)
            return false;
        layer.RemoveRange(kept, count - kept);
        return true;
    }

    private static (int X, int Y, int Z) BatchCell(Vector3 position) =>
        (Mathf.FloorToInt(position.x / 32f), Mathf.FloorToInt(position.y / 32f), Mathf.FloorToInt(position.z / 12f));

    private void Retain(Splat mark)
    {
        var cell = BatchCell(mark.Position);
        if (!level.RecentBatches.TryGetValue(cell, out var batch) || batch.Stains.Count == StainBatchSize)
        {
            // Keep each mesh local so distant rooms can be culled independently.
            batch = new StainBatch();
            level.RecentBatches[cell] = batch;
            level.Batches.Add(batch);
        }
        batch.Stains.Add(mark);
        level.MarkCount++;
        QueueBatch(batch);
    }

    private void QueueBatch(StainBatch batch)
    {
        if (batch.Queued)
            return;
        batch.Queued = true;
        pendingBatches.Enqueue(batch);
    }

    private void UploadSettledStains(float now)
    {
        if (pendingBatches.Count == 0)
            return;
        var batch = pendingBatches.Dequeue();
        batch.Queued = false;
        if (!batch.Mesh)
            batch.Mesh = CreateBatch("REDLINE Settled Blood", out batch.Owner);
        ClearBatch();
        DrawStains(batch.Stains, now, false);
        Upload(batch.Mesh);
    }

    private void DrawAirborne()
    {
        ClearBatch();
        var camera = PlayerState.cameraActive;
        if (!camera) camera = Camera.main;
        var normal = camera ? -camera.transform.forward : Battlefield.Up;
        foreach (var drop in drops)
        {
            var travel = Vector3.ProjectOnPlane(drop.Velocity, normal);
            float stretch = Mathf.Clamp(1f + travel.magnitude * 0.10f, 1f, 2.5f);
            float size = drop.Size * (drop.FromWound ? 0.73f : 0.92f);
            AddPolygon(drop.Position, normal, travel, size, stretch, drop.Tone, 0.25f, 5,
                Mathf.Clamp01((drop.Expires - Time.time) * 8f) * 0.88f, new Shade(drop.Tint, drop.Tint, drop.Tint), BloodSurface.Drop);
        }
        Upload(airborneMesh);
    }

    private void ClearBatch()
    {
        points.Clear();
        textureCoordinates.Clear();
        colors.Clear();
        indices.Clear();
    }

    private void Upload(Mesh mesh)
    {
        mesh.Clear();
        if (points.Count == 0)
            return;
        MeshArrays.Write(points, ref vertexStaging, ref meshVertices);
        MeshArrays.Write(colors, ref colorStaging, ref meshColors);
        MeshArrays.Write(textureCoordinates, ref uvStaging, ref meshUv);
        MeshArrays.Write(indices, ref indexStaging, ref meshIndices);
        mesh.SetVertices(meshVertices, 0, points.Count);
        mesh.SetColors(meshColors, 0, colors.Count);
        mesh.SetUVs(0, meshUv, 0, textureCoordinates.Count);
        mesh.SetIndices(meshIndices, 0, indices.Count, MeshTopology.Triangles, 0, true, 0);
    }

    private float DrawStains(List<Splat> layer, float now, bool animate)
    {
        float refresh = float.PositiveInfinity;
        foreach (var stain in layer)
        {
            float age = now - stain.Start;
            if (animate && age < 0)
            {
                refresh = Mathf.Min(refresh, stain.Start);
                continue;
            }
            bool pool = stain.Surface == BloodSurface.Pool;
            float spread = animate ? Mathf.Clamp01(age / stain.SpreadTime) : 1f;
            float grow = pool ? BloodTexture.PoolRadius(spread) : 1f - 0.52f * (1f - spread) * (1f - spread);
            float dryTime = pool ? PoolDryTime : StainDryTime;
            if (animate)
            {
                float change = spread < 1f ? now + (pool ? 0.075f : 0.04f) : float.PositiveInfinity;
                if (age < dryTime) change = Mathf.Min(change, Mathf.Min(stain.Start + dryTime, now + 1.5f));
                refresh = Mathf.Min(refresh, change);
            }
            float dried = animate ? Mathf.Clamp01(age / dryTime) : 1f;
            float tone = Mathf.Min(1f, stain.Tone + 0.22f * dried);
            var position = pool ? stain.Position + stain.Offset * grow : stain.Position;
            float alpha = pool && animate ? 0.95f * Mathf.Clamp01(age / 0.9f) : 0.95f;
            AddPolygon(position, stain.Normal, stain.Direction, stain.Size * grow, stain.Stretch, tone,
                stain.Seed, stain.Sides, alpha, stain.Lighting, stain.Surface);
        }
        return refresh;
    }

    private float nextMeshRefresh;
    private float nextStainUpload;
    private float nextPoolRefresh;
    private float nextPoolUpload;
    private float nextPoolLighting;
    private int poolLightIndex;
    private float nextSettle;
    private bool stainsDirty;
    private bool poolsDirty;

    public void SetLevel(string name, bool reset)
    {
        if (reset)
            Clear();
        else
        {
            foreach (var stain in stains)
                Retain(stain);
            if (level.MarkCount != 0 || pools.Count != 0)
            {
                visitedLevels[levelName] = level;
                log($"blood saved level={levelName} marks={level.MarkCount} growingPoolMarks={pools.Count}");
            }
            ReleaseView();
        }
        levelName = name;
        if (visitedLevels.TryGetValue(name, out var saved))
        {
            visitedLevels.Remove(name);
            level = saved;
            log($"blood restored level={name} marks={level.MarkCount} growingPoolMarks={pools.Count}");
        }
    }

    public void Clear()
    {
        log($"blood clear levels={visitedLevels.Count} drops={drops.Count} marks={stains.Count + level.MarkCount} growingPoolMarks={pools.Count}");
        visitedLevels.Clear();
        ReleaseView();
    }

    private void ReleaseView()
    {
        if (root) root.SetActive(false);
        drops.Clear();
        stains.Clear();
        foreach (var batch in level.Batches)
            batch.ReleaseView();
        level = new LevelBlood();
        pendingBatches.Clear();
        lighting.Clear();
        stainsDirty = poolsDirty = false;
        floorHits = wallHits = merged = woundDrops = 0;
        nextLog = nextMeshRefresh = nextStainUpload = nextSettle = 0;
        nextPoolRefresh = nextPoolUpload = dropTime = 0;
        nextPoolLighting = 0;
        poolLightIndex = 0;
        if (root) Object.Destroy(root);
        if (stainMesh) Object.Destroy(stainMesh);
        if (poolMesh) Object.Destroy(poolMesh);
        if (airborneMesh) Object.Destroy(airborneMesh);
        if (material) Object.Destroy(material);
        root = null;
        stainMesh = poolMesh = airborneMesh = null;
        material = null;
    }

    private bool EnsureMesh()
    {
        if (root)
            return true;
        material = BloodMaterial.CreateSurface();
        if (!material)
        {
            if (!shaderMissing) log("blood renderer unavailable: no compatible shader");
            shaderMissing = true;
            return false;
        }
        log($"blood renderer shader={material.shader.name} texture=surface-atlas lighting=shadow-aware-room-colors");
        root = new GameObject("REDLINE Blood");
        stainMesh = CreateBatch("REDLINE Stains", out _);
        poolMesh = CreateBatch("REDLINE Pools", out _);
        airborneMesh = CreateBatch("REDLINE Spray", out _);
        foreach (var batch in level.Batches)
            QueueBatch(batch);
        poolsDirty = pools.Count != 0;
        return true;
    }

    private Mesh CreateBatch(string name, out GameObject go)
    {
        go = new GameObject(name);
        go.transform.SetParent(root.transform, false);
        var mesh = new Mesh { name = name };
        mesh.MarkDynamic();
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return mesh;
    }

    private void RemoveDrop(int index)
    {
        drops[index] = drops[drops.Count - 1];
        drops.RemoveAt(drops.Count - 1);
    }

    private void Splash(Drop drop, Vector3 point, Vector3 normal)
    {
        float alignment = Vector3.Dot(normal, Battlefield.Up);
        bool wall = Mathf.Abs(alignment) < 0.65f;
        if (wall && !settings.WallSplatter.Value)
            return;
        var tangent = Vector3.ProjectOnPlane(drop.Velocity, normal);
        var position = point + normal * ((wall ? WallOffset : SurfaceOffset) + 0.02f * drop.Tone);
        if (drop.FromWound && alignment > 0.9f && FeedPool(position, drop.Size))
            return;
        if (wall)
            tangent += Vector3.ProjectOnPlane(Battlefield.Down, normal) * 1.6f;

        float stretch = Mathf.Clamp(1.05f + tangent.magnitude * 0.105f, 1.1f, 2.15f);
        float size = drop.Size * (drop.FromWound ? 1.58f : 3.15f);
        AddStain(stains, position, normal, tangent, size, stretch, 7, 0.23f, drop.Tone, drop.FromWound);
    }

    private bool FeedPool(Vector3 position, float size)
    {
        for (int i = pools.Count - 1; i >= 0; i--)
        {
            var pool = pools[i];
            if (pool.Sides != PoolCoreSides || (pool.Position - position).sqrMagnitude > 0.48f)
                continue;
            float growth = Mathf.Min(size * 0.16f, Mathf.Max(0, 2.70f - pool.Size));
            if (growth > 0)
            {
                pools[i] = pool with { Size = pool.Size + growth };
                poolsDirty = true;
            }
            merged++;
            return true;
        }
        return false;
    }

    private void AddStain(List<Splat> layer, Vector3 pos, Vector3 normal, Vector3 direction,
        float size, float stretch, int sides, float spreadTime, float tone, bool fromWound = false,
        Vector3 offset = default, float delay = 0)
    {
        if (layer == stains && (MergeStain(stains, pos, normal, size, fromWound, 36, ref stainsDirty)
            || MergeSettled(pos, normal, size, fromWound)))
            return;
        if (layer == stains && layer.Count == MaxFreshStains)
        {
            Retain(layer[0]);
            layer.RemoveAt(0);
        }
        bool pool = layer == pools;
        var surface = pool ? BloodSurface.Pool : fromWound ? BloodSurface.Drop : BloodSurface.Impact;
        var shade = SampleShade(pos + offset, normal, direction, size * stretch, pool && sides == PoolCoreSides);
        layer.Add(new Splat(pos, offset, normal, direction, size, stretch, tone, Random.Range(0, 6.28f), sides,
            Time.time + delay, spreadTime, shade, surface));
        if (pool) poolsDirty = true;
        else stainsDirty = true;
    }

    private Shade SampleShade(Vector3 position, Vector3 normal, Vector3 direction, float width, bool poolCore)
    {
        var center = lighting.SurfaceTint(position, normal);
        if (!poolCore)
            return new Shade(center, center, center);
        Vector3 extent = Tangent(direction, normal) * (width * 0.76f);
        return new Shade(center, lighting.SurfaceTint(position - extent, normal), lighting.SurfaceTint(position + extent, normal));
    }

    private void RelightPools(float now)
    {
        nextPoolLighting = now + 0.15f;
        lighting.Refresh(now);
        // Keep existing pools responsive to room lights without scanning every pool in a busy frame.
        for (int count = Math.Min(8, pools.Count); count > 0; count--)
        {
            poolLightIndex %= pools.Count;
            int index = poolLightIndex++;
            var pool = pools[index];
            var shade = SampleShade(pool.Position + pool.Offset, pool.Normal, pool.Direction,
                pool.Size * pool.Stretch, pool.Sides == PoolCoreSides);
            if (shade == pool.Lighting)
                continue;
            pools[index] = pool with { Lighting = shade };
            poolsDirty = true;
        }
    }

    private bool MergeSettled(Vector3 pos, Vector3 normal, float size, bool fromWound)
    {
        if (!level.RecentBatches.TryGetValue(BatchCell(pos), out var batch))
            return false;
        bool dirty = false;
        bool didMerge = MergeStain(batch.Stains, pos, normal, size, fromWound, 36, ref dirty);
        if (dirty)
            QueueBatch(batch);
        return didMerge;
    }

    private bool MergeStain(List<Splat> layer, Vector3 pos, Vector3 normal, float size, bool fromWound, int limit, ref bool dirty)
    {
        for (int i = layer.Count - 1, start = Math.Max(0, layer.Count - limit); i >= start; i--)
        {
            var neighbor = layer[i];
            if (neighbor.Surface == BloodSurface.Pool)
                continue;
            float dx = neighbor.Position.x - pos.x;
            float dy = neighbor.Position.y - pos.y;
            float dz = neighbor.Position.z - pos.z;
            if (dx * dx + dy * dy + dz * dz >= 0.016f || Vector3.Dot(normal, neighbor.Normal) <= 0.9f)
                continue;
            float growth = Mathf.Min(size * (fromWound ? 0.14f : 0.10f), Mathf.Max(0, 0.43f - neighbor.Size));
            layer[i] = neighbor with { Size = neighbor.Size + growth };
            dirty |= growth > 0;
            merged++;
            return true;
        }
        return false;
    }

    private void AddPolygon(Vector3 pos, Vector3 normal, Vector3 direction, float size, float stretch, float tone,
        float seed, int sides, float alpha, Shade shade, BloodSurface surface)
    {
        Vector3 right = Tangent(direction, normal);
        Vector3 up = Vector3.Cross(normal, right);
        int first = points.Count;
        int variants = surface == BloodSurface.Impact ? BloodTexture.ImpactVariants : BloodTexture.Variants;
        int variant = (int)(seed * 27f) % variants;
        float dry = Mathf.Lerp(1f, 0.55f, tone);
        byte opacity = (byte)(255f * alpha);
        var tint = Color(shade.Center);
        points.Add(pos);
        colors.Add(tint);
        textureCoordinates.Add(BloodMaterial.SurfaceUv(0.5f, 0.5f, variant, surface));
        for (int j = 0; j < sides; j++)
        {
            float angle = seed + j * ((float)Math.PI * 2 / sides);
            float edge = size * (0.88f + 0.12f * (float)Math.Sin(j * 2.4f + seed * 9));
            float x = (float)Math.Cos(angle);
            float y = (float)Math.Sin(angle);
            float width = x * stretch * edge;
            float height = y * edge;
            points.Add(new Vector3(pos.x + right.x * width + up.x * height,
                pos.y + right.y * width + up.y * height, pos.z + right.z * width + up.z * height));
            if (surface == BloodSurface.Pool)
            {
                var side = x < 0 ? shade.Left : shade.Right;
                float mix = Math.Abs(x);
                var center = shade.Center;
                colors.Add(new Color32((byte)((center.r + (side.r - center.r) * mix) * dry),
                    (byte)((center.g + (side.g - center.g) * mix) * dry),
                    (byte)((center.b + (side.b - center.b) * mix) * dry), opacity));
            }
            else colors.Add(tint);
            textureCoordinates.Add(BloodMaterial.SurfaceUv(0.5f + 0.5f * x, 0.5f + 0.5f * y, variant, surface));
        }
        for (int j = 0; j < sides; j++)
        {
            indices.Add(first);
            indices.Add(first + 1 + j);
            indices.Add(first + 1 + (j + 1) % sides);
        }

        Color32 Color(Color32 color) => new((byte)(color.r * dry), (byte)(color.g * dry),
            (byte)(color.b * dry), opacity);
    }

    private static Vector3 Tangent(Vector3 direction, Vector3 normal)
    {
        Vector3 right = Vector3.ProjectOnPlane(direction, normal);
        if (right.sqrMagnitude < 0.001f)
            right = Vector3.Cross(normal, Mathf.Abs(normal.x) < 0.7f ? Vector3.right : Vector3.up);
        return right.normalized;
    }

    private record struct Drop(Vector3 Position, Vector3 Velocity, float Floor, int WallMask,
        float Size, float Tone, float Expires, bool FromWound, Color32 Tint);

    private sealed class LevelBlood
    {
        public readonly List<Splat> GrowingPools = new();
        public readonly List<StainBatch> Batches = new();
        public readonly Dictionary<(int X, int Y, int Z), StainBatch> RecentBatches = new();
        public int MarkCount;
    }

    private sealed class StainBatch
    {
        public Mesh Mesh;
        public GameObject Owner;
        public readonly List<Splat> Stains = new(StainBatchSize);
        public bool Queued;

        public void ReleaseView()
        {
            if (Owner) Object.Destroy(Owner);
            if (Mesh) Object.Destroy(Mesh);
            Owner = null;
            Mesh = null;
            Queued = false;
        }
    }

    private readonly record struct Shade(Color32 Center, Color32 Left, Color32 Right);
    private readonly record struct Splat(Vector3 Position, Vector3 Offset, Vector3 Normal, Vector3 Direction, float Size,
        float Stretch, float Tone, float Seed, int Sides, float Start, float SpreadTime, Shade Lighting, BloodSurface Surface);
}
