using System;
using System.Collections.Generic;
using UnityEngine;

namespace Redline;

internal sealed class ElsterInjuries(RedlineSettings settings, SkinMap map, EffectsPhysics physics,
    BloodEffects blood, CutSounds sounds, Action<string> log)
{
    [Flags]
    private enum Trauma { None = 0, Leg = 1, Arm = 2 }
    private const string SaveKey = "redline.elster.injuries";
    private const Part SupportArm = Part.LeftArm;
    private const int MissingArm = 1 << (int)SupportArm;
    private readonly List<SkinCut> skins = new();
    private AlternatePlayerController actor;
    private Animator animator;
    private Rig rig;
    private Trauma trauma;
    private int armStress;
    private bool needsLoad = true;
    private bool newSever;
    private bool gatedLogged;
    private bool skinPrepared;
    private float repairUntil;
    private float noticeUntil;
    private float nextBleed;
    private float bleedingUntil;
    private bool playedWoundSound;
    private float scanAt;
    private string notice;
    private Vector3 impactDirection;
    private GUIStyle statusStyle;

    public bool HasArmInjury => Enabled && (trauma & Trauma.Arm) != 0 && !CombatWorld.Networked;
    public bool HasLegInjury => Enabled && (trauma & Trauma.Leg) != 0 && !CombatWorld.Networked;
    private bool Enabled => settings.ElsterTrauma.Value && !PlayerState.gameOver;

    public void Hit(int before, Vector2 direction)
    {
        if (!CombatWorld.Playing || !Enabled || CombatWorld.Networked)
        {
            if (!gatedLogged)
            {
                gatedLogged = true;
                log($"elster trauma gated injuries={settings.ElsterTrauma.Value} gameOver={PlayerState.gameOver} {CombatWorld.DescribePlaying()} {CombatWorld.Describe()}");
            }
            return;
        }
        int hp = PlayerState.hp;
        int lost = before - hp;
        if (lost <= 0 || hp <= 0)
            return;
        LoadIfNeeded();
        var previous = trauma;
        armStress = Math.Min(100, armStress + lost);
        if (lost >= 10 && hp <= 74)
            trauma |= Trauma.Leg;
        // Arm tear disabled for now. Re-enable with the load mask below.
        // if (hp <= 48 && (lost >= 15 || armStress >= 40))
        //     trauma |= Trauma.Arm;
        if (previous == trauma)
            return;

        newSever = (previous & Trauma.Arm) == 0 && (trauma & Trauma.Arm) != 0;
        impactDirection = new Vector3(direction.x, direction.y, 0f).normalized;
        if (newSever)
        {
            bleedingUntil = Time.time + 16f;
            nextBleed = Time.time;
            playedWoundSound = false;
        }
        Commit(newSever ? "LEFT ARM LOST" : "LEG ACTUATOR DAMAGED");
        log($"elster trauma hp={before}->{hp} state={trauma}");
    }

    public void OfferRepair()
    {
        if (!Enabled || CombatWorld.Networked)
            return;
        LoadIfNeeded();
        armStress = 0;
        repairUntil = Time.time + 2f;
        Repair();
    }

    private void Repair()
    {
        if (repairUntil <= Time.time)
            return;
        var repaired = trauma;
        if (PlayerState.hp >= 40)
            repaired &= ~Trauma.Leg;
        if (PlayerState.hp >= 60)
            repaired &= ~Trauma.Arm;
        if (repaired == trauma)
            return;
        trauma = repaired;
        if ((trauma & Trauma.Arm) == 0)
        {
            newSever = false;
            bleedingUntil = 0;
            ReleaseSkins();
        }
        Commit(trauma == Trauma.None ? "LOCOMOTION AND ARM SYSTEMS READY" : "LEG ACTUATOR REPAIRED");
    }

    private void Commit(string message)
    {
        SProgress.SetInt(SaveKey, (int)trauma);
        notice = message;
        noticeUntil = Time.time + 3.7f;
    }

    private void LoadIfNeeded()
    {
        if (!needsLoad || !SProgress.progress)
            return;
        trauma = (Trauma)(SProgress.GetInt(SaveKey, 0) & (int)Trauma.Leg); // Arm masked out while disabled.
        needsLoad = false;
    }

    public void BeginFrame() => rig?.RestorePose();

    public void Tick()
    {
        var player = PlayerState.pcs;
        if (actor != player)
        {
            ResetVisuals();
            actor = player;
        }
        if (!Enabled || CombatWorld.Networked)
        {
            if (skins.Count != 0) ReleaseSkins();
            return;
        }
        if (!CombatWorld.Playing || !player || !player.isActiveAndEnabled || player.traversing)
        {
            // Stock renderers are needed in authored poses and cinematics.
            if (PlayerState.cutscene && skins.Count != 0) ReleaseSkins();
            return;
        }
        LoadIfNeeded();
        Repair();
        if (trauma == Trauma.None)
            return;
        var current = player.playerAttack ? player.playerAttack.animator : null;
        if (animator != current)
        {
            ReleaseSkins();
            animator = current;
            rig = current ? new Rig(current) : null;
        }
        if (rig == null)
            return;
        if (HasLegInjury && player.input.sqrMagnitude > 0.09f && !PlayerState.hurt)
        {
            float stride = Mathf.Sin(Time.time * 9.5f);
            Bend(rig.LeftThigh, Quaternion.Euler(4f + stride * 8f, 0f, 0f));
            Bend(rig.LeftShin, Quaternion.Euler(-5f - Mathf.Max(0f, stride) * 10f, 0f, 0f));
        }
        if (!HasArmInjury || !rig.LeftArm)
            return;
        if (!skinPrepared && Time.time >= scanAt)
        {
            scanAt = Time.time + 0.75f;
            PrepareSkins(player);
        }
        if (newSever && skins.Count != 0)
            Sever();
        foreach (var skin in skins)
            skin.Refresh(followMaterials: true);
        Bleed();
    }

    private void Bend(Transform bone, Quaternion rotation)
    {
        if (!bone)
            return;
        rig.Remember(bone);
        bone.localRotation *= rotation;
    }

    private void PrepareSkins(AlternatePlayerController player)
    {
        string examined = "";
        foreach (var renderer in player.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var mesh = renderer ? renderer.sharedMesh : null;
            if (!mesh || mesh.name is not ("Body" or "Body_Suit" or "BodyCrippled"
                or "BodyUncrippled_Armless" or "Chest_Armor") || renderer.bones.Length == 0)
                continue;
            var binding = map.Find(renderer, rig);
            examined += $"{mesh.name}:{mesh.vertexCount}/{mesh.subMeshCount} bones={renderer.bones.Length} binding={binding != null} arm={binding != null && Array.IndexOf(binding.Labels, (byte)SupportArm) >= 0}; ";
            if (binding == null || Array.IndexOf(binding.Labels, (byte)SupportArm) < 0)
                continue;
            var skin = new SkinCut(renderer, binding);
            if (!skin.HasPart(SupportArm))
            {
                skin.Dispose();
                continue;
            }
            skins.Add(skin);
            if (!newSever || !skin.CanDisplay)
                skin.ShowMissingParts(MissingArm);
        }
        skinPrepared = skins.Count != 0;
        log($"elster arm visual bindings={skins.Count}");
        if (skins.Count == 0)
            log($"elster arm no binding {examined}");
    }

    private void Sever()
    {
        if (!skins.Exists(skin => skin.CanDisplay))
            return;
        newSever = false;
        var point = rig.LeftArm.position;
        var tip = rig.LeftHand ? rig.LeftHand.position : point + Battlefield.Down;
        float floor = Floor();
        var limb = physics.Limb(point, tip, floor, Mathf.Clamp(Vector3.Distance(point, tip) * 0.15f, 0.27f, 0.52f));
        try
        {
            foreach (var skin in skins)
                skin.Sever(SupportArm, MissingArm, limb);
            if (limb.Meshes.Count > 0)
                limb.Launch(impactDirection, fling: true);
            else
                physics.ConsumeLimb(limb);
        }
        catch
        {
            physics.ConsumeLimb(limb);
            throw;
        }
        blood.Spurt(point, (tip - point).normalized, floor, Battlefield.RoomSolids, settings.BloodDensity, 12, 0.85f);
        sounds.Play(point, stomp: false);
        log($"elster left arm severed at {Battlefield.Format(point)}");
    }

    private void Bleed()
    {
        float now = Time.time;
        if (now < nextBleed || now >= bleedingUntil || settings.BloodDensity <= 0)
            return;
        nextBleed = now + 0.42f;
        var point = rig.LeftArm.position;
        var outward = rig.LeftHand ? (rig.LeftHand.position - point).normalized : Battlefield.Down;
        var motion = actor._rigidbody ? actor._rigidbody.velocity : Vector3.zero;
        blood.Drip(point, outward, motion, Floor(), Battlefield.RoomSolids, settings.BloodDensity,
            Mathf.Clamp01((bleedingUntil - now) / 12f), ref playedWoundSound);
    }

    private float Floor()
    {
        var origin = actor.transform.position;
        return Battlefield.FloorBelow(origin, Battlefield.Height(origin));
    }

    public void Draw()
    {
        if (Event.current.type != EventType.Repaint || !Enabled || !CombatWorld.Playing || CombatWorld.Networked
            || (trauma == Trauma.None && Time.time >= noticeUntil))
            return;
        statusStyle ??= new GUIStyle { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        float scale = Mathf.Clamp(Screen.height / 900f, 0.7f, 2f);
        statusStyle.fontSize = Mathf.RoundToInt(13f * scale);
        bool message = Time.time < noticeUntil;
        string text = message ? notice
            : HasArmInjury && HasLegInjury ? "LEFT ARM LOST   /   LEG DAMAGED"
            : HasArmInjury ? "LEFT ARM LOST" : "LEG DAMAGED";
        statusStyle.normal.textColor = trauma == Trauma.None ? new Color(0.74f, 0.85f, 0.80f) : new Color(1f, 0.58f, 0.48f);
        float width = Mathf.Min(Screen.width - 32f, 284f * scale);
        var box = new Rect(16f * scale, Screen.height - 59f * scale, width, 28f * scale);
        GUI.DrawTexture(box, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0,
            new Color(0.08f, 0.045f, 0.05f, 0.87f), 0, 0);
        box.x += 10f * scale;
        GUI.Label(box, text, statusStyle);
    }

    public void SceneChanged(bool reset)
    {
        ResetVisuals();
        if (!reset)
            return;
        needsLoad = true;
        armStress = 0;
        trauma = Trauma.None;
        noticeUntil = 0;
        repairUntil = 0;
        newSever = false;
        bleedingUntil = 0;
    }

    private void ResetVisuals()
    {
        BeginFrame();
        ReleaseSkins();
        animator = null;
        rig = null;
    }

    private void ReleaseSkins()
    {
        foreach (var skin in skins) skin.Dispose();
        skins.Clear();
        skinPrepared = false;
        scanAt = 0;
    }
}
