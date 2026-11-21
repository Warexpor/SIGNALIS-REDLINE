using System;
using UnityEngine;

namespace Redline;

internal sealed partial class EnemyEffects
{
    private static readonly int[] getUpStates =
    {
        Animator.StringToHash("Base Layer.Fall.Get Up 0"),
        Animator.StringToHash("Base Layer.Fall.Get Up"),
        Animator.StringToHash("Base Layer.Get Up")
    };

    private RevivalStage revivalStage;
    private Rig.Snapshot forcedRevivalPose;
    private Rig.Snapshot revivalPose;
    private float revivalPhaseStarted;
    private float revivalBlendTime;
    private int revivalGetUp;
    private bool priorAgentCanMove;
    private Rig.Snapshot knockdownPose;
    private Vector3 knockdownRoot;
    private float knockdownCrawlStarted;
    private bool holdingKnockdownCrawl;

    public bool HoldRevivalAi => revivalStage != RevivalStage.None && !IsCorpse(Target) && !NativeDowned
        && Target.hitbox && Target.hitbox.HP > 0;

    private void PrepareKnockdownCrawl(bool alive, bool mayCrawl, Vector3 position)
    {
        holdingKnockdownCrawl = false;
        if (!alive || downed || revivalStage != RevivalStage.None || !mayCrawl || !CanCrawlWithInjury
            || !rig.CanRagdoll || !animator || !animator.runtimeAnimatorController)
        {
            knockdownPose = null;
            return;
        }
        if (!NativeDowned && knockdownPose == null)
            return;

        bool gettingUp = Array.IndexOf(getUpStates, animator.GetCurrentAnimatorStateInfo(0).fullPathHash) >= 0
            || animator.IsInTransition(0)
                && Array.IndexOf(getUpStates, animator.GetNextAnimatorStateInfo(0).fullPathHash) >= 0;
        bool knockedDown = NativeDowned && animator.GetBool(Target.staggerType == EnemyController.hurtState.fire
            ? Target.anim_Fire : Target.anim_Critical);
        if (knockedDown && !gettingUp)
        {
            knockdownPose = rig.Capture(knockdownPose);
            knockdownRoot = position;
            knockdownCrawlStarted = -1f;
        }
        else if (knockdownPose != null && (NativeDowned || gettingUp))
        {
            // Native recovery owns the timer, events and stomp window. Mask only its rendered get-up.
            holdingKnockdownCrawl = true;
            rig.Apply(knockdownPose with { HipsPosition = knockdownPose.HipsPosition + position - knockdownRoot });
        }
    }

    private void BlendKnockdownCrawl(Vector3 position)
    {
        if (knockdownCrawlStarted < 0f)
        {
            knockdownCrawlStarted = Time.time;
            log($"knockdown recovery into crawl missing=0x{missing:X}");
        }
        float progress = Mathf.Clamp01((Time.time - knockdownCrawlStarted) / 0.38f);
        if (progress >= 1f)
        {
            knockdownPose = null;
            return;
        }
        var pose = knockdownPose with { HipsPosition = knockdownPose.HipsPosition + position - knockdownRoot };
        rig.BlendFrom(pose, Mathf.SmoothStep(0f, 1f, progress));
    }

    private bool BeginBodyRevival()
    {
        if (NativeDowned || !animator || !rig.CanRagdoll)
            return false;
        var pose = forcedRevivalPose;
        forcedRevivalPose = null;
        if (waistSplit != null)
            pose = waistSplit.BeginRevival(floor);
        else if (ragdoll != null || crawlDeathPose != null)
        {
            try
            {
                if (ragdoll != null)
                {
                    rig.Apply(ragdoll.Snapshot);
                    ragdoll.Pose();
                    if (corpseRoot)
                        rig.Hips.position += corpseRoot.transform.position;
                }
                else ApplyCrawlDeathPose();
                pose = rig.Capture();
            }
            finally { rig.RestorePose(); }
        }
        if (pose == null)
            return false;

        revivalPose = pose;
        revivalStage = RevivalStage.Reassemble;
        revivalPhaseStarted = Time.time;
        CancelSwipe();
        ForceAnimation();
        HoldNativeAnimation();
        if (waistSplit == null)
            EndRagdoll();
        crawlDeathPose = null;
        if (Target.agent)
        {
            priorAgentCanMove = Target.agent.canMove;
            Target.agent.canMove = false;
            AnchorRevival(pose.HipsPosition);
        }
        previous = Target.transform.position;
        log($"revival pose held hips={Battlefield.Format(pose.HipsPosition)} split={waistSplit != null}");
        return true;
    }

    private void AnchorRevival(Vector3 hips)
    {
        var position = Target.transform.position;
        var travel = Battlefield.OnPlane(hips - position);
        if (travel.sqrMagnitude < 0.08f)
            return;
        float surface = Battlefield.FloorBelow(hips, floor);
        if (Mathf.Abs(surface - floor) > 1.1f)
            return;
        var allowed = DebrisContact.LimitTravel(position, travel, floor, 0.55f);
        if (allowed.sqrMagnitude < 0.08f)
            return;
        var destination = position + allowed;
        float newFloor = Battlefield.FloorBelow(destination, floor);
        Target.agent.Teleport(destination + Battlefield.Up * (newFloor - floor), true);
        floor = FindFloor();
    }

    private bool PrepareRevivalFrame(bool mayCrawl)
    {
        if (revivalStage != RevivalStage.Reassemble)
            return true;
        float elapsed = Time.time - revivalPhaseStarted;
        if (waistSplit != null)
        {
            if (!waistSplit.AdvanceRevival(elapsed))
            {
                crawlCenter = waistSplit.StompPoint;
                UpdateGroundAim();
                return false;
            }
            ClearWaistSplit();
        }
        if (awaitingCorpseRevival || returningParts.Count != 0 || elapsed < 0.14f)
        {
            rig.Apply(revivalPose);
            return true;
        }

        revivalGetUp = 0;
        if (!(mayCrawl && IsCrawling) && animator.runtimeAnimatorController)
        {
            foreach (int state in getUpStates)
                if (animator.HasState(0, state))
                {
                    revivalGetUp = state;
                    break;
                }
        }
        revivalStage = RevivalStage.Align;
        revivalPhaseStarted = Time.time;
        revivalBlendTime = revivalGetUp != 0 ? 0.52f : 0.95f;
        ReleaseUnusedAnimation();
        if (revivalGetUp != 0)
        {
            animator.ResetTrigger(Target.anim_Fall);
            animator.ResetTrigger(Target.anim_LoadDead);
            animator.SetBool(Target.anim_Dead, false);
            animator.SetBool(Target.anim_Critical, false);
            animator.Play(revivalGetUp, 0, 0f);
        }
        log($"revival motion started getUpState={revivalGetUp} crawl={mayCrawl && IsCrawling}");
        return true;
    }

    private void AdvanceBodyRevival()
    {
        float elapsed = Time.time - revivalPhaseStarted;
        if (revivalStage == RevivalStage.Align)
        {
            float weight = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / revivalBlendTime));
            if (weight < 1f)
            {
                rig.BlendFrom(revivalPose, weight);
                return;
            }
            revivalPose = null;
            if (revivalGetUp != 0 && animator.GetCurrentAnimatorStateInfo(0).fullPathHash == revivalGetUp)
            {
                revivalStage = RevivalStage.GetUp;
                return;
            }
            EndBodyRevival();
        }
        else if (revivalStage == RevivalStage.GetUp
            && (elapsed >= 6f || animator.GetCurrentAnimatorStateInfo(0).fullPathHash != revivalGetUp))
            EndBodyRevival();
    }

    private void EndBodyRevival(bool interrupted = false)
    {
        if (revivalStage == RevivalStage.None)
            return;
        revivalStage = RevivalStage.None;
        revivalPose = null;
        revivalGetUp = 0;
        if (Target && Target.agent)
            Target.agent.canMove = priorAgentCanMove;
        ReleaseGroundAim();
        RestoreLiveHitbox();
        if (waistSplit != null)
        {
            if (Target && IsCorpse(Target))
            {
                waistSplit.CancelRevival();
                fatalCut = MajorCut.Waist;
                downed = true;
                PreventRespawn(Target);
            }
            else ClearWaistSplit();
        }
        ReleaseUnusedAnimation();
        if (animator && forcedAnimation && missing == 0 && ragdoll == null && waistSplit == null && crawlDeathPose == null)
        {
            animator.cullingMode = originalCulling;
            forcedAnimation = false;
        }
        log($"revival motion ended interrupted={interrupted}");
    }

    private void RestoreLiveHitbox()
    {
        if (!Target || IsCorpse(Target) || !Target.hitbox || Target.hitbox.HP <= 0)
            return;
        var standing = Target._collider;
        if (standing && !standing.enabled)
        {
            standing.enabled = true;
            log("revival hitbox restored");
        }
    }

    private enum RevivalStage { None, Reassemble, Align, GetUp }
}
