using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Redline;

internal sealed class EnemyEffects
{
    private readonly Rig rig;
    private readonly EffectsPhysics physics;
    private readonly SkinMap map;
    private readonly Action<string> log;
    private readonly List<SkinCut> skins = new();
    private readonly List<(Renderer Renderer, Part Part)> hidden = new();
    private readonly List<SkinnedMeshRenderer> ragdollVisibility = new();
    private readonly Animator animator;
    private readonly AnimatorCullingMode originalCulling;
    private bool forcedAnimation;
    private int missing;
    private int impacts;
    private bool downed;
    private bool skinScanned;
    private float phase;
    private float blend;
    private float floor;
    private float nextBake;
    private float nextTrail;
    private float nextBleed;
    private float bleedUntil;
    private Vector3 previous;
    private Vector3 lastTrail;
    private Part bleedingPart;
    private PendingFall? pendingFall;
    private float poolScale;
    private Vector3 poolPosition;
    private BodyRagdoll ragdoll;

    public EnemyEffects(EnemyController target, EffectsPhysics physics, SkinMap map, Action<string> log)
    {
        Target = target;
        this.physics = physics;
        this.map = map;
        this.log = log;
        animator = target.animator;
        rig = new Rig(animator);
        if (animator) originalCulling = animator.cullingMode;
        previous = target.transform.position;
        floor = FindFloor();
    }

    public EnemyController Target { get; }
    public bool IsCrawling => (Missing(Part.LeftLeg) || Missing(Part.RightLeg)) && rig.CanCrawl;
    public static bool IsSupported(EnemyController target) => target.Preset &&
        target.Preset.Type is AnEnemy.AnEnemyType.EULR or AnEnemy.AnEnemyType.STAR
            or AnEnemy.AnEnemyType.ARAR or AnEnemy.AnEnemyType.STCR;
    public Rig.Snapshot CapturePose() => rig.Capture();
    private bool Missing(Part part) => (missing & (1 << (int)part)) != 0;

    public void Impact(Vector3 direction, bool knockdown, bool heavy, bool preview, Rig.Snapshot pose,
        bool mayCut, bool mayRagdoll, BloodEffects blood, float amount)
    {
        impacts++;
        floor = FindFloor();
        int walls = WallMask;
        float remaining = Target.hitbox ? (float)Target.hitbox.HP / Mathf.Max(1, Target.Preset.HP) : 1;
        if (knockdown && pose != null)
            rig.Apply(pose);
        Vector3 center = rig.Chest ? rig.Chest.position : Target.transform.position + Battlefield.Up * 4.8f;
        blood.Spurt(center + Battlefield.Up * Random.Range(-0.58f, 0.58f), direction, floor, walls,
            amount, knockdown ? 45 : heavy ? 38 : 23, knockdown || heavy ? 1.25f : 0.9f);
        log($"Impact {Target.Preset.Type}: HP fraction {remaining:F2}, downed={knockdown}, heavy={heavy}");

        // Preserve special encounters and nonstandard bodies. Ordinary humanoids use the same AI and hitbox.
        bool humanoid = IsSupported(Target);
        float chance = heavy ? 0.70f : 0.40f;
        bool severe = remaining < (heavy ? 0.81f : 0.61f) || impacts > 1;
        if (mayCut && humanoid && (preview || knockdown || severe && Random.value < chance))
        {
            // A live target keeps at least one arm. Put most sublethal damage into the knee so it can crawl.
            Part[] candidates = preview || !knockdown
                ? new[] { Part.LeftLeg, Part.RightLeg }
                : new[] { Part.LeftLeg, Part.RightLeg, Part.LeftArm, Part.RightArm, Part.Head };
            Shuffle(candidates);
            foreach (Part cut in candidates)
            {
                if (cut == Part.Head && !heavy || Missing(cut))
                    continue;
                if (Sever(cut, direction, out Vector3 wound))
                {
                    blood.Spurt(wound, direction, floor, walls, amount, heavy ? 64 : 46, heavy ? 1.65f : 1.25f);
                    bleedingPart = cut;
                    bleedUntil = Time.time + 18;
                    nextBleed = Time.time + 0.13f;
                    break;
                }
            }
        }

        if (!preview && !downed && (knockdown || humanoid))
            pendingFall = new PendingFall(direction, pose, heavy, Time.time + 1f);
        if (knockdown && !downed)
            BeginFall(mayRagdoll, amount, pose != null);
    }

    public void RestorePose() => rig.RestorePose();

    public void Tick(bool mayCrawl, bool mayRagdoll, BloodEffects blood, float amount)
    {
        var position = Target.transform.position;
        Vector3 travel = Battlefield.OnPlane(position - previous);
        previous = position;
        bool killed = Target.state == EnemyController.enemystate.dead;
        bool alive = !killed && Target.hitbox && Target.hitbox.HP > 0;
        if (pendingFall is PendingFall pending)
        {
            if (pending.Expires < Time.time)
                pendingFall = null;
            else if (killed && !downed)
                BeginFall(mayRagdoll, amount, false);
        }
        if (alive && downed)
        {
            EndRagdoll();
            RestoreUpperBody();
            downed = false;
            poolScale = 0;
        }

        if (ragdoll != null && downed)
            ragdoll.Pose(rig);

        if (poolScale > 0 && (ragdoll == null || ragdoll.Landed))
        {
            Vector3 spill = ragdoll != null && rig.Hips && rig.Chest ?
                Vector3.Lerp(rig.Hips.position, rig.Chest.position, 0.48f) : poolPosition;
            blood.Pool(spill, Battlefield.FloorBelow(spill, floor), poolScale);
            poolScale = 0;
        }

        bool crawling = alive && IsCrawling && mayCrawl;
        blend = Mathf.MoveTowards(blend, crawling ? 1 : 0, Time.deltaTime * 4.0f);
        if (crawling || alive && blend > 0)
        {
            float movement = Mathf.Clamp01(travel.magnitude / Mathf.Max(0.001f, Time.deltaTime) * 0.18f);
            if (travel.sqrMagnitude < 25)
                phase = Mathf.Repeat(phase + travel.magnitude * 1.3f + Time.deltaTime * 0.75f, (float)Math.PI * 2);
            if (Mathf.Abs(Battlefield.Height(position) - floor) > 1.3f)
                floor = FindFloor();
            rig.Crawl(Target.transform, floor, phase, movement, blend, Missing(Part.LeftLeg), Missing(Part.RightLeg),
                Missing(Part.LeftArm), Missing(Part.RightArm));

            if (Time.time < bleedUntil && Time.time >= nextTrail && (position - lastTrail).sqrMagnitude > 0.75f)
            {
                nextTrail = Time.time + 0.14f;
                lastTrail = position;
                Vector3 drag = position - Battlefield.OnPlane(Target.transform.forward).normalized * 1.05f
                    + Random.insideUnitSphere * 0.38f;
                blood.Trail(drag, Battlefield.FloorBelow(drag, floor), amount);
            }
        }

        if (Missing(bleedingPart) && Time.time < bleedUntil && Time.time >= nextBleed)
        {
            var wound = rig.CutBone(bleedingPart);
            if (wound)
                blood.Spurt(wound.position, -Battlefield.OnPlane(Target.transform.forward) * 0.25f,
                    floor, WallMask, amount * 0.42f, 4, 0.35f);
            nextBleed = Time.time + Random.Range(0.30f, 0.50f);
        }

        if (Time.time >= nextBake)
        {
            nextBake = Time.time + (alive ? 0.033f : 0.12f);
            RefreshSkins();
        }
    }

    public void BeforeRender()
    {
        if (ragdoll == null || !Target || !Target.isActiveAndEnabled
            || Target.state != EnemyController.enemystate.dead)
            return;
        ragdoll.Pose(rig);
        RefreshSkins();
    }

    private void BeginFall(bool enabled, float amount, bool poseApplied)
    {
        var pending = pendingFall.Value;
        pendingFall = null;
        downed = true;
        poolScale = amount * (pending.Heavy ? 1.30f : 1.05f);
        poolPosition = Target.transform.position + pending.Direction * 0.8f;
        if (!enabled || !IsSupported(Target))
            return;
        if (!poseApplied && pending.Pose != null)
            rig.Apply(pending.Pose);
        try
        {
            ragdoll = BodyRagdoll.Start(rig, physics, pending.Direction, floor, missing);
            if (ragdoll == null)
                return;
            foreach (var renderer in Target.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!renderer || renderer.updateWhenOffscreen)
                    continue;
                renderer.updateWhenOffscreen = true;
                ragdollVisibility.Add(renderer);
            }
            log($"Ragdoll took over downed pose for {Target.Preset.Type}");
        }
        finally
        {
            if (!poseApplied && pending.Pose != null)
                rig.RestorePose();
        }
    }

    private void RefreshSkins()
    {
        foreach (var skin in skins)
            skin.Refresh();
    }

    private int WallMask
    {
        get
        {
            const int actors = (1 << 11) | (1 << 12) | (1 << 13) | (1 << 17) | (1 << 18);
            int mask = Target.character ? Target.character.WallMask.value : 0;
            return (mask | Target.stoppingMask.value) & ~actors;
        }
    }

    private float FindFloor()
    {
        Vector3 position = Target.transform.position;
        return Battlefield.FloorBelow(position, Battlefield.Height(position));
    }

    private bool Sever(Part part, Vector3 direction, out Vector3 wound)
    {
        wound = default;
        Transform bone = rig.CutBone(part);
        if (!bone || !animator)
            return false;
        if (!skinScanned)
        {
            skinScanned = true;
            foreach (var renderer in Target.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!renderer || !renderer.sharedMesh || renderer.bones.Length == 0)
                    continue;
                var labels = map.Find(renderer, rig);
                if (labels != null)
                    skins.Add(new SkinCut(renderer, labels));
                else
                    log($"No cut binding for {renderer.sharedMesh.name} ({renderer.sharedMesh.vertexCount}, {renderer.sharedMesh.subMeshCount})");
            }
        }
        if (!skins.Exists(skin => skin.HasPart(part)))
            return false;

        Transform tip = rig.Tip(part);
        wound = bone.position;
        Vector3 end = tip ? tip.position : wound + Battlefield.Up * 0.92f;
        if (!forcedAnimation)
        {
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            forcedAnimation = true;
        }
        float width = part == Part.Head ? 0.54f : Mathf.Clamp(Vector3.Distance(wound, end) * 0.16f, 0.27f, 0.52f);
        var loose = physics.Limb(wound, end, floor, direction, width);
        missing |= 1 << (int)part;
        foreach (var skin in skins)
            skin.Sever(part, missing, loose);

        // Rigid props parented to an arm or head follow it through vanilla animations. Hide them at the cut.
        foreach (var accessory in bone.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (!accessory || accessory.forceRenderingOff || accessory.name.StartsWith("REDLINE", StringComparison.Ordinal))
                continue;
            accessory.forceRenderingOff = true;
            hidden.Add((accessory, part));
        }

        log($"Severed {part} on {Target.Preset.Type}: {skins.Count} skinned surfaces");
        return true;
    }

    private static void Shuffle(Part[] parts)
    {
        for (int i = parts.Length - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (parts[i], parts[j]) = (parts[j], parts[i]);
        }
    }

    private void RestoreUpperBody()
    {
        const int legs = (1 << (int)Part.LeftLeg) | (1 << (int)Part.RightLeg);
        if ((missing & ~legs) == 0)
            return;
        foreach (var skin in skins)
            skin.RestoreParts(missing & legs);
        missing &= legs;
        RestoreAccessories(missing);
    }

    private void RestoreAccessories(int stillMissing)
    {
        for (int i = hidden.Count - 1; i >= 0; i--)
        {
            var (renderer, part) = hidden[i];
            if ((stillMissing & (1 << (int)part)) != 0)
                continue;
            if (renderer) renderer.forceRenderingOff = false;
            hidden.RemoveAt(i);
        }
    }

    public void Suspend()
    {
        RestorePose();
        ragdoll?.Settle();
    }

    private void EndRagdoll()
    {
        ragdoll?.Dispose();
        ragdoll = null;
        foreach (var renderer in ragdollVisibility)
            if (renderer) renderer.updateWhenOffscreen = false;
        ragdollVisibility.Clear();
    }

    public void Dispose()
    {
        RestorePose();
        EndRagdoll();
        foreach (var skin in skins) skin.Dispose();
        RestoreAccessories(0);
        if (animator && forcedAnimation) animator.cullingMode = originalCulling;
    }

    private readonly record struct PendingFall(Vector3 Direction, Rig.Snapshot Pose, bool Heavy, float Expires);
}
