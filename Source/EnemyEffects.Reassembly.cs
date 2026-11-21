using System.Collections.Generic;
using UnityEngine;

namespace Redline;

internal sealed partial class EnemyEffects
{
    private readonly List<SeveredPart> severedParts = new(4);
    private readonly List<LimbReturn> returningParts = new(4);
    private bool awaitingCorpseRevival;
    public bool ForcingCorpseRevival { get; private set; }

    private bool ReassemblyEnabled => settings.Dismember.Value && settings.Reassemble.Value;

    public static string RecoveryOwner(EnemyController target)
    {
        var id = target.GetComponent<UniqueId>();
        return id ? id.id : null;
    }

    private void RememberSeveredPart(SeveredPart severed)
    {
        severedParts.Add(severed);
        severed.Limb.RecoveryOwner = RecoveryOwner(Target);
        severed.Limb.Injury = severed;
    }

    public bool RestoreRetainedInjuries(IEnumerable<SeveredPart> injuries, bool loadedAsCorpse)
    {
        if (!settings.Dismember.Value || missing != 0)
            return false;
        foreach (var severed in injuries)
        {
            if (Missing(severed.Part) || !rig.CutBone(severed.Part))
                continue;
            ScanSkins();
            if (!skins.Exists(skin => skin.HasPart(severed.Part) && skin.CanDisplay && !SkinCut.IsEffectsPass(skin.Source)))
                continue;
            severedParts.Add(severed);
            missing |= 1 << (int)severed.Part;
            HideAccessories(severed.Part, rig.CutBone(severed.Part));
        }
        if (missing == 0)
            return false;
        ForceAnimation();
        foreach (var skin in skins)
            skin.ShowMissingParts(missing);
        downed = IsCorpse(Target);
        awaitingCorpseRevival |= loadedAsCorpse;
        log($"revival injuries recovered on room load missing=0x{missing:X}");
        return true;
    }

    public bool PrepareRevivalReset()
    {
        ObserveCorpse();
        return awaitingCorpseRevival || severedParts.Count != 0 && NativeDowned;
    }

    // Loaded corpses can already have revival HP. Critical actors retain a living state.
    public static bool IsCorpse(EnemyController target) =>
        target.state == EnemyController.enemystate.dead;

    public void ObserveCorpse()
    {
        if (awaitingCorpseRevival || !IsCorpse(Target))
            return;
        awaitingCorpseRevival = true;
        CancelLimbReturns(settleInPlace: true);
    }

    public static bool CanForceRevival(EnemyController target) =>
        target && target.isActiveAndEnabled && target.animator && target.Preset
        && target.hitbox && target.agent && IsCorpse(target);

    public bool HasReachableLimb()
    {
        if (!ReassemblyEnabled || severedParts.Count == 0)
            return false;
        var position = Target.transform.position;
        float surface = Battlefield.FloorBelow(position, Battlefield.Height(position));
        foreach (var severed in severedParts)
            if (CanReachSocket(severed, rig.CutBone(severed.Part), surface))
                return true;
        return false;
    }

    public bool ForceRevival()
    {
        if (!CanForceRevival(Target))
            return false;
        ObserveCorpse();
        var nativeCorpse = ragdoll == null && waistSplit == null && crawlDeathPose == null ? rig.Capture() : null;
        ForcingCorpseRevival = true;
        try { Target.ResetEnemy(); }
        finally { ForcingCorpseRevival = false; }
        if (IsCorpse(Target) || Target.hitbox.HP <= 0)
            return false;

        // The debug reset overrides terminal deaths only for the selected corpse.
        fatalCut = MajorCut.None;
        crawlKilled = enteredCrawl = false;
        forcedRevivalPose = nativeCorpse;
        downed = true; // Release the corpse pose and ragdoll through the normal revival tick.
        Target.permadeath = false;
        Target.StopAllCoroutines();
        Target.animator.ResetTrigger(Target.anim_Fall);
        Target.animator.ResetTrigger(Target.anim_LoadDead);
        Target.queuedForRespawn = false;
        if (Target.stompInter)
        {
            Target.stompInter.triggered = false;
            Target.stompInter.gameObject.SetActive(false);
        }
        if (Target.downedHitbox)
            Target.downedHitbox.SetActive(false);
        ClearBurnEffect();
        var manager = Target.GetComponentInParent<EnemyManager>();
        if (manager && manager.enemies != null && manager.dead?.Remove(Target) == true)
        {
            if (!manager.enemies.Contains(Target))
                manager.enemies.Add(Target);
            manager.cleared = false;
        }
        log($"revival test forced from corpse hp={Target.hitbox.HP}");
        return true;
    }

    private void ClearBurnEffect()
    {
        Target.currentBurn = 0;
        var burn = Target.BurnEffect;
        if (!burn)
            return;
        var overlay = burn._renderer;
        if (!burn.burning && !(overlay && overlay.enabled))
            return;
        burn.StopAllCoroutines();
        burn.burning = false;
        if (burn.sfx)
            burn.sfx.Stop();
        if (overlay)
        {
            overlay.enabled = false;
            foreach (var material in overlay.materials)
                if (material && material.HasProperty("_Amount"))
                    material.SetFloat("_Amount", 0f);
        }
    }

    private bool CanReachSocket(SeveredPart severed, Transform bone, float surface) =>
        bone && physics.CanRecall(severed.Limb) && Mathf.Abs(severed.Limb.SupportFloor - surface) <= 1.1f
        && LimbReturn.HasPath(severed.Limb.Visual.transform.position, severed.Socket(bone), surface);

    private void ObserveRevival(bool alive)
    {
        ObserveCorpse();
        if (!alive || Target.queuedForRespawn || NativeDowned)
        {
            CancelLimbReturns(settleInPlace: true);
            return;
        }
        if (!ReassemblyEnabled || Target.permadeath)
        {
            awaitingCorpseRevival = false;
            CancelLimbReturns(settleInPlace: true);
            return;
        }
        if (!awaitingCorpseRevival)
            return;
        awaitingCorpseRevival = false;
        if (severedParts.Count == 0)
            return;
        float now = Time.time;
        floor = FindFloor();
        foreach (var severed in severedParts)
        {
            var bone = rig.CutBone(severed.Part);
            if (!CanReachSocket(severed, bone, floor))
            {
                log($"revival limb unreachable part={severed.Part}");
                continue;
            }
            physics.HoldForReturn(severed.Limb);
            returningParts.Add(new LimbReturn(severed, bone, floor, now + returningParts.Count * 0.10f));
        }
        log($"revival reassembly started limbs={returningParts.Count} missing=0x{missing:X}");
    }

    private void AdvanceLimbReturns(CutSounds sounds)
    {
        for (int i = returningParts.Count - 1; i >= 0; i--)
        {
            var motion = returningParts[i];
            switch (motion.Advance(Time.time, floor))
            {
                case ReturnPhase.Pulling:
                    continue;
                case ReturnPhase.Joined:
                    RestorePart(motion.Severed.Part);
                    sounds.PlayReassembly(motion.Socket);
                    log($"revival limb reattached part={motion.Severed.Part} missing=0x{missing:X}");
                    break;
                case ReturnPhase.Blocked:
                    motion.Severed.Limb.CancelReturn(settleInPlace: true);
                    log($"revival limb return interrupted part={motion.Severed.Part}");
                    break;
            }
            returningParts.RemoveAt(i);
        }
    }

    public void CancelLimbReturns(bool settleInPlace = false)
    {
        foreach (var motion in returningParts)
            motion.Severed.Limb.CancelReturn(settleInPlace);
        returningParts.Clear();
    }
}
