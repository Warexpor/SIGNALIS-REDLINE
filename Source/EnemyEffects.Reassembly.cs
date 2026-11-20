using System.Collections.Generic;
using UnityEngine;

namespace Redline;

internal sealed partial class EnemyEffects
{
    private readonly List<SeveredPart> severedParts = new(4);
    private readonly List<LimbReturn> returningParts = new(4);
    private bool awaitingRevival;

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

    public bool RestoreRetainedInjuries(IEnumerable<SeveredPart> injuries)
    {
        if (!ReassemblyEnabled || missing != 0)
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
        downed = awaitingRevival = true;
        log($"revival injuries recovered on room load missing=0x{missing:X}");
        return true;
    }

    public bool PrepareRevivalReset()
    {
        if (!ReassemblyEnabled || severedParts.Count == 0 || Target.permadeath
            || (!awaitingRevival && !downed && !NativeDowned && !Target.queuedForRespawn
                && Target.state != EnemyController.enemystate.dead))
            return false;
        awaitingRevival = true;
        return true;
    }

    public static bool CanForceRevival(EnemyController target)
    {
        if (!target || !target.isActiveAndEnabled || !target.hitbox || !target.animator || !target.Preset
            || target.permadeath || target.staggerType == EnemyController.hurtState.fire || !IsSupported(target))
            return false;
        // Critical consumes a revive and restores HP while the actor is still on the floor.
        if (target.staggerType == EnemyController.hurtState.critical && target.animator.GetBool(target.anim_Critical))
            return true;
        return target.state == EnemyController.enemystate.dead && target.hitbox.HP <= 0
            && (target.queuedForRespawn || target.revives > 0);
    }

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
        PrepareRevivalReset();
        // Cancel the in-progress Critical coroutine before performing its get-up cleanup.
        Target.StopAllCoroutines();
        Target.queuedForRespawn = false;
        if (Target.stompInter)
        {
            Target.stompInter.triggered = false;
            Target.stompInter.gameObject.SetActive(false);
        }
        if (Target.downedHitbox)
            Target.downedHitbox.SetActive(false);
        Target.ResetEnemy();
        if (Target.state == EnemyController.enemystate.dead || Target.hitbox.HP <= 0)
            return false;
        log($"revival test forced hp={Target.hitbox.HP}");
        return true;
    }

    private bool CanReachSocket(SeveredPart severed, Transform bone, float surface) =>
        bone && physics.CanRecall(severed.Limb) && Mathf.Abs(severed.Limb.SupportFloor - surface) <= 1.1f
        && LimbReturn.HasPath(severed.Limb.Visual.transform.position, severed.Socket(bone), surface);

    private void ObserveRevival(bool alive)
    {
        if (severedParts.Count == 0)
            return;
        if (!ReassemblyEnabled || Target.permadeath || Target.staggerType == EnemyController.hurtState.fire)
        {
            awaitingRevival = false;
            CancelLimbReturns(settleInPlace: true);
            return;
        }

        // The Critical coroutine clears this animation flag when the get-up starts.
        // Its hurt state stays critical until the stock get-up delay expires.
        bool criticalFall = Target.staggerType == EnemyController.hurtState.critical && animator
            && animator.GetBool(Target.anim_Critical);
        if (!alive || Target.queuedForRespawn || criticalFall)
        {
            awaitingRevival = true;
            CancelLimbReturns(settleInPlace: true);
            return;
        }
        if (!awaitingRevival)
            return;
        awaitingRevival = false;
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
