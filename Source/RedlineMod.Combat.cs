using System;
using FMOD.Studio;
using FMODUnity;
using UnityEngine;

namespace Redline;

public sealed partial class RedlineMod
{
    private const string ShotgunRackMarker = "REDLINE/Shotgun/ReloadRack";
    private static ItemPickup ammoPickup;
    private CombatEncounters combatEncounters;
    private ElsterInjuries elsterInjuries;
    private WeaponAudio weaponAudio;
    private RuntimeAnimatorController shotgunRackController;
    private RuntimeAnimatorController smgReloadController;
    private bool smgReloadMarkersReady;

    private void InstallCombatHooks()
    {
        combatEncounters = new CombatEncounters(settings, Log);
        elsterInjuries = new ElsterInjuries(settings, skinMap, physics, blood, sounds, Log);
        weaponAudio = new WeaponAudio(settings, Log);
        Hook(typeof(ItemPickup), nameof(ItemPickup.pickUp), nameof(AmmoPickupBegin), null, nameof(AmmoPickupEnd));
        Hook(typeof(DynamicDifficulty), nameof(DynamicDifficulty.calculateCount), null, nameof(AmmoCount), null,
            typeof(AnItem), typeof(int));
        Hook(typeof(PlayerState), nameof(PlayerState.damagePlayer), nameof(PlayerDamageBegin), nameof(PlayerDamageEnd), null,
            typeof(int), typeof(Vector2));
        Hook(typeof(PlayerState), nameof(PlayerState.healElster), null, nameof(PlayerRepaired));
        Hook(typeof(SaveManager), nameof(SaveManager.Load), null, nameof(CombatSaveLoaded));
        Hook(typeof(ThirdPersonCharacter), nameof(ThirdPersonCharacter.Move), nameof(InjuredMove), null, null, typeof(Vector3));
        Hook(typeof(ThirdPersonCharacter), nameof(ThirdPersonCharacter.MoveTank), nameof(InjuredTank), null, null,
            typeof(float), typeof(float));
        Hook(typeof(ThirdPersonCharacter), nameof(ThirdPersonCharacter.MoveOverride), nameof(InjuredStrafe), null, null,
            typeof(Vector3), typeof(float), typeof(float), typeof(float));
        Hook(typeof(PlayerAttack), nameof(PlayerAttack.ReadyForAttack), nameof(HandlingBegin), null, nameof(HandlingEnd));
        Hook(typeof(RuntimeManager), nameof(RuntimeManager.PlayOneShot), nameof(StockGunshot), null, null,
            typeof(string), typeof(Vector3));
        Hook(typeof(RuntimeManager), nameof(RuntimeManager.CreateInstance), null, nameof(StockBurst), null, typeof(string));
        Hook(typeof(StudioEventEmitter), nameof(StudioEventEmitter.Play), nameof(StockPumpEmitter), null, null);
    }

    private static void AmmoPickupBegin(ItemPickup __instance, out ItemPickup __state)
    {
        __state = ammoPickup;
        ammoPickup = null;
        if (!__instance || __instance.firstObserved || __instance.count <= 0 || !__instance._item
            || !IsAmmo(__instance._item._item) || active.settings.AmmoBonus.Value <= 0)
            return;
        try
        {
            var id = __instance.GetComponent<UniqueId>();
            if (id && !string.IsNullOrEmpty(id.id) && !CombatWorld.Networked
                && SProgress.GetInt("count: " + id.id, -1) < 0)
                ammoPickup = __instance;
        }
        catch (Exception ex) { active.WarnOnce("ammo pickup", ex); }
    }

    private static Exception AmmoPickupEnd(ItemPickup __state, Exception __exception)
    {
        ammoPickup = __state;
        return __exception;
    }

    private static void AmmoCount(AnItem __0, ref int __result)
    {
        if (!ammoPickup || __0 != ammoPickup._item)
            return;
        // The next native calculation only formats a log message. The first result is assigned to the pickup.
        var pickup = ammoPickup;
        ammoPickup = null;
        if (__result <= 0)
            return;
        int before = __result;
        float bonus = RedlineSettings.Factor(active.settings.AmmoBonus);
        __result += Mathf.CeilToInt(__result * bonus);
        if (__result != before)
            active.Log($"ammo pickup item={pickup._item._item} native={before} boosted={__result}");
    }

    private static bool IsAmmo(Items.itemlist item) => item is Items.itemlist.PistolAmmo or Items.itemlist.SmgAmmo
        or Items.itemlist.ShotgunAmmo or Items.itemlist.RevolverAmmo or Items.itemlist.RifleAmmo
        or Items.itemlist.FlakGunAmmo or Items.itemlist.FlareGunAmmo;

    private static void PlayerDamageBegin(out int __state) => __state = PlayerState.hp;

    private static void PlayerDamageEnd(int __state, Vector2 __1)
    {
        try { active.arianeFlash?.Hit(__state, active.ArianeFlashSuppressed); }
        catch (Exception ex) { active.WarnOnce("Ariane flash damage", ex); }
        try { active.elsterInjuries.Hit(__state, __1); }
        catch (Exception ex) { active.WarnOnce("Elster trauma", ex); }
    }

    private static void PlayerRepaired()
    {
        try { active.elsterInjuries.OfferRepair(); }
        catch (Exception ex) { active.WarnOnce("Elster repair", ex); }
    }

    private static void CombatSaveLoaded()
    {
        active.arianeFlash?.Reset();
        active.elsterInjuries.SceneChanged(reset: true);
        // Rooms and their enemies may survive a native reload. Their own Load handlers restore their save state.
        active.combatEncounters.SceneChanged();
        active.weaponAudio.ClearRoom();
    }

    private static bool InjuredPlayer(ThirdPersonCharacter character) =>
        active.elsterInjuries.HasLegInjury && CombatWorld.Playing && PlayerState.pcs
        && PlayerState.pcs.character == character && !PlayerState.pcs.traversing;

    private static void InjuredMove(ThirdPersonCharacter __instance, ref Vector3 __0)
    {
        if (InjuredPlayer(__instance))
            __0 *= 0.76f;
    }

    private static void InjuredTank(ThirdPersonCharacter __instance, ref float __0)
    {
        if (InjuredPlayer(__instance))
            __0 *= 0.76f;
    }

    private static void InjuredStrafe(ThirdPersonCharacter __instance, ref float __1)
    {
        if (PlayerState.aiming && InjuredPlayer(__instance))
            __1 *= 0.76f;
    }

    private static void HandlingBegin(PlayerAttack __instance, out HandlingState __state)
    {
        __state = default;
        if (!active.elsterInjuries.HasArmInjury || __instance != PlayerAttack.instance || __instance.melee)
            return;
        var weapon = __instance.weapon;
        if (!weapon)
            return;
        __state = new HandlingState(weapon, weapon.Cooldown);
        weapon.Cooldown = __state.Cooldown * 1.25f;
    }

    private static Exception HandlingEnd(HandlingState __state, Exception __exception)
    {
        if (__state.Weapon)
            __state.Weapon.Cooldown = __state.Cooldown;
        return __exception;
    }

    private static bool StockGunshot(string __0, Vector3 __1)
    {
        if (__0 == ShotgunRackMarker)
        {
            try { active.weaponAudio.PlayReloadRack(); }
            catch (Exception ex) { active.WarnOnce("shotgun reload rack", ex); }
            return false;
        }
        if (__0 != null && __0.StartsWith(WeaponAudio.SmgReloadMarkerPrefix, StringComparison.Ordinal))
        {
            try
            {
                if (int.TryParse(__0.Substring(WeaponAudio.SmgReloadMarkerPrefix.Length), out int beat))
                    active.weaponAudio.PlaySmgReloadMarker(beat, __1);
            }
            catch (Exception ex) { active.WarnOnce("SMG reload marker", ex); }
            return false;
        }
        if (!active.weaponAudio.Replaced(__0))
        {
            try
            {
                if (__0 == WeaponAudio.SmgReloadEvent)
                {
                    bool ready = false;
                    try { ready = active.PrepareSmgReloadMarkers(); }
                    catch (Exception ex) { active.WarnOnce("SMG reload markers", ex); }
                    if (active.weaponAudio.BeginSmgReload(ready))
                        return false;
                }
                return !active.weaponAudio.PlayDryClick(__0, __1) && !active.weaponAudio.PlayHandling(__0);
            }
            catch (Exception ex) { active.WarnOnce("weapon handling", ex); return true; }
        }
        active.Log($"stock gunshot replaced event={__0}");
        return false;
    }

    private static void StockBurst(string __0, EventInstance __result)
    {
        if (active.weaponAudio.Replaced(__0))
            active.Log($"stock burst muted event={__0} result={__result.setVolume(0f)}");
        else if (WeaponAudio.IsShotgunPump(__0))
        {
            try
            {
                if (active.weaponAudio.PlayHandling(__0))
                    active.Log($"stock pump instance muted event={__0} result={__result.setVolume(0f)}");
            }
            catch (Exception ex) { active.WarnOnce("shotgun pump event instance", ex); }
        }
    }

    private static bool StockPumpEmitter(StudioEventEmitter __instance)
    {
        try
        {
            if (!__instance || !active.weaponAudio.PlayPumpEmitter(__instance.Event))
                return true;
            active.Log("stock shotgun pump emitter suppressed");
            return false;
        }
        catch (Exception ex) { active.WarnOnce("shotgun pump emitter", ex); return true; }
    }

    private void PrepareShotgunRack(PlayerAttack player)
    {
        if (!settings.WeaponHandling.Value)
            return;
        var weapon = player.weapon;
        if (!weapon || !weapon.parentItem || weapon.parentItem._item != Items.itemlist.Shotgun)
            return;
        var animator = player.animator;
        var controller = animator ? animator.runtimeAnimatorController : null;
        if (!controller || controller == shotgunRackController)
            return;
        shotgunRackController = controller;

        foreach (var clip in controller.animationClips)
        {
            if (!clip || clip.name != "Elster_Aim_Shotgun_Reload_Outro")
                continue;
            AnimationEvent pump = null;
            foreach (var marker in clip.events)
            {
                if (marker.functionName == nameof(CombatSfxManager.PlaySFX) && marker.stringParameter == ShotgunRackMarker)
                    return;
                if (marker.functionName == nameof(CombatSfxManager.ShotgunPumpSFX))
                    pump = marker;
            }
            if (pump == null)
                return;
            // Keep the stock marker and use the existing sound entry point at the same frame.
            clip.AddEvent(new AnimationEvent
            {
                functionName = nameof(CombatSfxManager.PlaySFX),
                stringParameter = ShotgunRackMarker,
                time = pump.time,
                messageOptions = SendMessageOptions.DontRequireReceiver
            });
            Log($"shotgun reload rack marker time={pump.time:F3}");
            return;
        }
    }

    private bool PrepareSmgReloadMarkers()
    {
        if (!settings.WeaponHandling.Value || CombatWorld.Networked)
            return false;
        var player = PlayerAttack.instance;
        var animator = player ? player.animator : null;
        var controller = animator ? animator.runtimeAnimatorController : null;
        if (!controller)
            return false;
        if (controller == smgReloadController)
            return smgReloadMarkersReady;
        smgReloadController = controller;
        smgReloadMarkersReady = false;

        int prepared = 0;
        foreach (var clip in controller.animationClips)
        {
            if (!clip || clip.name is not ("Elster_Aim_SMG_Reload" or "Elster_Aim_SMG_Reload2"))
                continue;
            var events = clip.events;
            for (int index = 0; index < WeaponAudio.SmgReloadMarkerCount; index++)
            {
                float time = WeaponAudio.SmgReloadMarkerTime(index);
                if (time >= clip.length)
                    return false;
                string marker = WeaponAudio.SmgReloadMarkerPrefix + index;
                bool present = false;
                foreach (var existing in events)
                    if (existing.functionName == nameof(CombatSfxManager.PlaySFX) && existing.stringParameter == marker)
                    {
                        present = true;
                        break;
                    }
                if (!present)
                    clip.AddEvent(new AnimationEvent
                    {
                        functionName = nameof(CombatSfxManager.PlaySFX),
                        stringParameter = marker,
                        time = time,
                        messageOptions = SendMessageOptions.DontRequireReceiver
                    });
            }
            prepared++;
        }
        smgReloadMarkersReady = prepared > 0;
        Log($"SMG reload animation clips prepared={prepared}");
        return smgReloadMarkersReady;
    }

    private readonly record struct HandlingState(AnWeapon Weapon, float Cooldown);
}
