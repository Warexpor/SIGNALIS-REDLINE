using System;
using System.Collections.Generic;
using System.IO;
using FMODUnity;
using HarmonyLib;
using MelonLoader;
using UnhollowerBaseLib;
using UnityEngine;

[assembly: MelonInfo(typeof(Redline.RedlineMod), "REDLINE", Redline.RedlineMod.Version, "Redline")]
[assembly: MelonGame("rose-engine", "SIGNALIS")]

namespace Redline;

public sealed partial class RedlineMod : MelonMod
{
    internal const string Version = "0.3.0";
    private static RedlineMod active;
    private static readonly System.Random overkillRandom = new();
    private readonly Dictionary<int, EnemyEffects> enemies = new();
    private readonly List<int> expired = new();
    private readonly Queue<Impact> impacts = new();
    private readonly Queue<StompHit> stomps = new();
    private readonly HashSet<int> proneSquibs = new();
    private readonly List<(EnemyController Target, string Owner, bool Corpse)> loadedEnemies = new();
    private BloodEffects blood;
    private ElsterBlood elsterBlood;
    private CutSounds sounds;
    private EffectsPhysics physics;
    private SkinMap skinMap;
    private RedlinePanel panel;
    private RedlineSettings settings;
    private StreamWriter verbose;
    private bool loggingRequested;
    private bool logCreated;
    private string scene = "(starting)";
    private int shotNumber;
    private int stompFrame = -1;
    private int proneSquibFrame = -1;
    private float nextTimingLog;
    private int lastMeasuredFrame = -1;
    private double lastLateUpdateMs;
    private float nextLogFlush;
    private bool bloodPrepared;
    private PlayerAim playerAim;
    private static SquibManager suppressedSquibs;
    private static Shot currentShot;

    public override void OnInitializeMelon()
    {
        active = this;
        settings = new RedlineSettings();
        UpdateDiagnosticLog();
        Log($"startup v{Version} gravity={Battlefield.Format(Physics.gravity)} blood={settings.BloodDensity} ragdoll={settings.Ragdoll.Value} extremeRagdoll={settings.ExtremeRagdoll.Value} dismember={settings.Dismember.Value} crawl={settings.Crawl.Value} waistChance={settings.WaistChance.Value} reassembly={settings.Reassemble.Value}");
        sounds = new CutSounds(settings, () => weaponAudio?.GetGoreOutput() ?? default, Log);
        blood = new BloodEffects(settings, sounds, Log);
        elsterBlood = new ElsterBlood(settings, Log);
        physics = new EffectsPhysics(settings, Log);
        panel = new RedlinePanel(settings);
        try { skinMap = new SkinMap(Path.Combine("UserData", "RedlineSkinMap.gz")); }
        catch (Exception error)
        {
            WarnOnce("Skin map", error);
            skinMap = new SkinMap("");
        }
        Hook(typeof(PlayerAttack), "RaycastShot", nameof(ShotBegin), null, nameof(ShotEnd));
        Hook(typeof(Physics), "Raycast", null, nameof(ShotRaycast), null,
            typeof(Vector3), typeof(Vector3), typeof(RaycastHit).MakeByRefType(), typeof(float), typeof(int));
        Hook(typeof(Physics), "RaycastAll", null, nameof(ShotRaycastAll), null,
            typeof(Vector3), typeof(Vector3), typeof(float), typeof(int));
        Hook(typeof(EnemyController), "TakeDamage", nameof(DamageBegin), nameof(DamageEnd), nameof(DamageFinish),
            typeof(float), typeof(float), typeof(float), typeof(bool));
        Hook(typeof(PlayerAim), "Targeting", nameof(AimTargetingBegin), nameof(AimTargetingEnd));
        Hook(typeof(EnemyController), "Kill", null, nameof(EnemyKilled));
        Hook(typeof(EnemyController), "KillSilent", null, nameof(EnemyKilled));
        Hook(typeof(EnemyController), "Update", nameof(EnemyUpdateBegin), nameof(EnemyUpdateEnd), nameof(EnemyUpdateCleanup));
        Hook(typeof(EnemyController._Attack_d__172), "MoveNext", nameof(AttackStep), null);
        Hook(typeof(EnemyController), "Hit", nameof(NativeMeleeHit), null);
        Hook(typeof(PlayerState), "Stomp", null, nameof(StompSignal));
        Hook(typeof(EnemyController), "PlayPitchedSFX", null, nameof(StompConfirmed), null, typeof(StudioEventEmitter));
        Hook(typeof(EnemyController), "Movement", nameof(MoveBegin), nameof(MoveEnd), nameof(MoveCleanup));
        Hook(typeof(EnemyController), "ResetEnemy", nameof(ResetEnemy), null);
        Hook(typeof(EnemyController), "Load", null, nameof(EnemyLoaded));
        Hook(typeof(SquibManager), "TriggerRandomSquib", nameof(StockSquib), null);
        Hook(typeof(PauseMenu), "Update", nameof(AllowMenuInput), null);
        Hook(typeof(NewMainMenu), "Update", nameof(AllowMenuInput), null);
        Hook(typeof(Rewired.Integration.UnityUI.RewiredStandaloneInputModule), "Process", nameof(AllowMenuInput), null);
        Hook(typeof(UnityEngine.EventSystems.StandaloneInputModule), "Process", nameof(AllowMenuInput), null);
        InstallCombatHooks();
        InstallArianeFlash();
        Log($"skin map loaded entries={skinMap.Count}");
        LoggerInstance.Msg($"REDLINE Combat Overhaul loaded with {skinMap.Count} local skin bindings.");
    }

    private void Hook(Type type, string name, string prefix, string postfix, string finalizer = null,
        params Type[] signature)
    {
        try
        {
            var target = AccessTools.DeclaredMethod(type, name, signature);
            if (target == null)
                throw new MissingMethodException(type.FullName, name);
            HarmonyInstance.Patch(target, Handler(prefix), Handler(postfix), finalizer: Handler(finalizer));
            LoggerInstance.Msg($"Hooked {type.Name}.{name}");
            Log($"hook installed {type.Name}.{name}");
        }
        catch (Exception exception)
        {
            WarnOnce($"hook {type.Name}.{name}", exception);
        }
    }

    private static HarmonyMethod Handler(string name) => name == null ? null :
        new HarmonyMethod(AccessTools.DeclaredMethod(typeof(RedlineMod), name))
        {
            priority = name == nameof(DamageBegin) ? HarmonyLib.Priority.Last : HarmonyLib.Priority.Normal
        };

    private static bool AllowMenuInput() => active?.panel?.CapturesInput != true && !Input.GetKeyDown(KeyCode.F9);

    private static void ShotBegin(PlayerAttack __instance, out Shot __state)
    {
        __state = currentShot;
        currentShot = null;
        try
        {
            var weapon = __instance.weapon;
            if (__instance.melee || !weapon)
                return;
            var item = weapon.parentItem;
            bool heavy = item && item._item is Items.itemlist.Shotgun or Items.itemlist.FlakGun
                or Items.itemlist.Rifle or Items.itemlist.Revolver;
            bool spread = item && item._item is Items.itemlist.Shotgun or Items.itemlist.FlakGun;
            bool rifle = item && item._item == Items.itemlist.Rifle;
            bool flare = item && item._item == Items.itemlist.FlareGun;
            var muzzle = PlayerAttack.muzzle;
            var actor = __instance.transform;
            var shot = new Shot(++active.shotNumber, heavy, spread, muzzle ? muzzle.position : actor.position,
                actor.forward, rifle, flare) { Weapon = weapon, PreviousAmmo = weapon.magAmmo };
            currentShot = shot;
            active.Log($"shot id={shot.Id} weapon={(item ? item._item.ToString() : weapon.name)} heavy={heavy} origin={Battlefield.Format(shot.Origin)} facing={Battlefield.Format(shot.Direction)}");
        }
        catch (Exception ex)
        {
            active.WarnOnce("shot", ex);
        }
    }

    private static Exception ShotEnd(Shot __state, Exception __exception)
    {
        if (currentShot is Shot shot)
        {
            if (__exception == null && shot.Weapon && shot.Weapon.magAmmo < shot.PreviousAmmo)
            {
                try { active.weaponAudio.Play(shot.Weapon, shot.Origin); }
                catch (Exception ex) { active.WarnOnce("gunshot playback", ex); }
                try { active.QueueProneImpacts(shot); }
                catch (Exception ex) { active.WarnOnce("prone shot impact", ex); }
            }
            if (active.verbose != null)
            {
                double elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - shot.Started)
                    * (1000d / System.Diagnostics.Stopwatch.Frequency);
                if (elapsed >= 4)
                    active.Log($"shot timing id={shot.Id} elapsed={elapsed:F1}ms");
            }
        }
        currentShot = __state;
        return __exception;
    }

    private static void ShotRaycast(Vector3 __0, Vector3 __1, ref RaycastHit __2, float __3, int __4, ref bool __result)
    {
        var shot = currentShot;
        if (shot == null || shot.IsFlare)
            return;
        bool downedFallback = false;
        try
        {
            var trace = shot.Hitboxes.Trace(__0, __1, __3, __4);
            if (trace != null)
            {
                RaycastHit selected = __2;
                bool found = __result;
                if (found && trace.StockColliders.Contains(selected.m_Collider))
                {
                    found = false;
                    var others = Physics.RaycastAll(__0, __1, __3, __4, QueryTriggerInteraction.UseGlobal);
                    foreach (var hit in others)
                    {
                        if (trace.StockColliders.Contains(hit.m_Collider) || (found && selected.m_Distance <= hit.m_Distance))
                            continue;
                        selected = hit;
                        found = true;
                    }
                }
                foreach (var hit in trace.Hits)
                {
                    if (found && selected.m_Distance <= hit.m_Distance)
                        continue;
                    selected = hit;
                    found = true;
                }
                if (__result != found || (found && (selected.m_Collider != __2.m_Collider || selected.m_Distance != __2.m_Distance)))
                    active.Log($"mesh ray shot={shot.Id} stock={__result} hit={found} distance={(found ? selected.m_Distance : -1f):F2}");
                __2 = found ? selected : default;
                __result = found;
                downedFallback = found && trace.DownedFallback.Contains(selected.m_Collider);
                CaptureProneContacts(shot, trace, __0, __1, found ? selected.m_Distance : __3, __4);
            }
        }
        catch (Exception ex) { active.WarnOnce("mesh ray", ex); }
        if (__result)
            CaptureContact(shot, __2, __1, downedFallback);
    }

    private static void ShotRaycastAll(Vector3 __0, Vector3 __1, float __2, int __3, ref Il2CppStructArray<RaycastHit> __result)
    {
        var shot = currentShot;
        if (shot == null || shot.IsFlare || __result == null)
            return;
        ShotHitboxes.TraceResult trace = null;
        try
        {
            trace = shot.Hitboxes.Trace(__0, __1, __2, __3);
            if (trace != null && (trace.StockColliders.Count != 0 || trace.Hits.Count != 0))
            {
                int stockCount = __result.Length;
                foreach (var hit in __result)
                    if (!trace.StockColliders.Contains(hit.m_Collider))
                        trace.Hits.Add(hit);
                trace.Hits.Sort((a, b) => a.m_Distance.CompareTo(b.m_Distance));
                __result = trace.Hits.ToArray();
                active.Log($"mesh ray all shot={shot.Id} stock={stockCount} hit={__result.Length}");
            }
            if (trace?.ProneHits.Count > 0)
            {
                float stopAt = __2;
                foreach (var hit in __result)
                {
                    var collider = hit.collider;
                    if (collider && !collider.GetComponentInParent<EnemyController>())
                        stopAt = Mathf.Min(stopAt, hit.distance);
                }
                CaptureProneContacts(shot, trace, __0, __1, stopAt, __3);
            }
        }
        catch (Exception ex) { active.WarnOnce("mesh ray all", ex); }
        foreach (var hit in __result)
            CaptureContact(shot, hit, __1, trace?.DownedFallback.Contains(hit.m_Collider) == true);
    }

    private static void CaptureContact(Shot shot, RaycastHit hit, Vector3 direction, bool downedFallback)
    {
        try
        {
            var collider = hit.collider;
            var owner = collider ? collider.GetComponentInParent<EnemyController>() : null;
            if (!owner || !owner.hitbox || owner.state == EnemyController.enemystate.dead)
                return;
            int id = owner.GetInstanceID();
            active.enemies.TryGetValue(id, out var tracked);
            if (!shot.Contacts.TryGetValue(id, out var contacts))
            {
                var pose = tracked?.CapturePose();
                shot.Contacts.Add(id, contacts = new ShotContacts(owner.hitbox.HP, pose));
            }
            if (downedFallback)
            {
                active.Log($"{(tracked?.HasCrawlPose == true ? "crawler" : "downed")} hitbox fallback shot={shot.Id} enemy={id} distance={hit.distance:F2}");
                return;
            }
            AddImpactRay(contacts.Rays, hit.point, direction);
        }
        catch (Exception ex) { active.WarnOnce("capture native ray contact", ex); }
    }

    private static void CaptureProneContacts(Shot shot, ShotHitboxes.TraceResult trace, Vector3 origin, Vector3 direction,
        float stopAt, int mask)
    {
        if (trace.ProneHits.Count == 0)
            return;
        // Some native bullet rays look only for enemies. Also check any room geometry missing from that mask.
        int roomMask = Battlefield.RoomSolids & ~mask;
        if (roomMask != 0 && Physics.Raycast(origin, direction, out var wall, stopAt, roomMask, QueryTriggerInteraction.Ignore))
            stopAt = wall.distance;
        foreach (var (target, hit) in trace.ProneHits)
        {
            if (hit.m_Distance > stopAt + 0.001f)
                continue;
            int id = target.GetInstanceID();
            var contacts = shot.ProneContacts ??= new Dictionary<int, ProneContact>();
            if (!contacts.TryGetValue(id, out var contact))
                contacts.Add(id, contact = new ProneContact(target));
            AddImpactRay(contact.Rays, hit.m_Point, direction);
        }
    }

    private static void AddImpactRay(List<ImpactCuts.Ray> rays, Vector3 point, Vector3 direction)
    {
        var ray = new ImpactCuts.Ray(Rig.ToPoint(point), Rig.ToPoint(direction.normalized));
        if (rays.Count < 12 && !rays.Exists(previous =>
            System.Numerics.Vector3.DistanceSquared(previous.Point, ray.Point) < 0.01f))
            rays.Add(ray);
    }

    private void QueueProneImpacts(Shot shot)
    {
        if (shot.ProneContacts == null || (!settings.Dismember.Value && settings.BloodDensity <= 0))
            return;
        if (proneSquibFrame != Time.frameCount)
        {
            proneSquibFrame = Time.frameCount;
            proneSquibs.Clear();
        }
        foreach (var contact in shot.ProneContacts.Values)
        {
            var target = contact.Target;
            if (!target || !target.hitbox || !ShotHitboxes.IsProne(target)
                || shot.DamagedProneTargets?.Contains(target.GetInstanceID()) == true)
                continue;
            bool dead = target.state == EnemyController.enemystate.dead;
            impacts.Enqueue(new Impact(target, shot, false, false, dead && IsFinalDeath(target),
                false, null, shot.Weapon.Damage, contact.Rays.ToArray(), ProneHit: true));
            proneSquibs.Add(target.GetInstanceID());
            Log($"prone shot impact shot={shot.Id} enemy={target.GetInstanceID()} rays={contact.Rays.Count} dead={dead}");
        }
    }

    private static Rig.Snapshot CapturePreHitPose(EnemyController enemy)
    {
        try
        {
            if ((active.settings.Ragdoll.Value || active.settings.Dismember.Value) && EnemyEffects.IsSupported(enemy))
                return active.GetEnemy(enemy).CapturePose();
        }
        catch (Exception ex) { active.WarnOnce("pre-hit pose", ex); }
        return null;
    }

    private static void DamageBegin(EnemyController __instance, float __0, ref float __1, ref float __2,
        out DamageSnapshot __state)
    {
        __state = new DamageSnapshot(false, null, null, null, 0, 0, false, suppressedSquibs, null, false);
        var shot = currentShot;
        if (!__instance || !__instance.hitbox || !__instance.Preset
            || (shot == null && !EnemyEffects.IsStandardReplika(__instance))
            || __instance.state == EnemyController.enemystate.dead)
            return;
        int id = __instance.GetInstanceID();
        // The flare gun has its own shot type. The delayed Thermite Flare calls TakeDamage with 100% fire chance.
        if (shot?.IsFlare == true || __0 >= 100f)
        {
            active.Log($"fire damage native shot={shot?.Id ?? 0} enemy={id} fireChance={__0:F1}");
            return;
        }
        if (shot != null && active.elsterInjuries.HasArmInjury)
        {
            __1 *= 0.70f;
            __2 *= 0.80f;
        }
        ShotContacts contacts = null;
        if (shot != null && shot.Contacts.TryGetValue(id, out contacts))
            shot.Contacts.Remove(id);
        Rig.Snapshot pose = contacts?.Pose ?? CapturePreHitPose(__instance);
        if (shot != null && active.settings.BloodDensity > 0)
            suppressedSquibs = __instance.squibs;
        int incomingHp = __instance.hitbox.HP;
        float before = contacts?.Health ?? __instance.oldHP;
        bool critical = __instance.staggerType == EnemyController.hurtState.critical;
        int? savedRevives = null;
        bool crawler = active.enemies.TryGetValue(id, out var tracked) && tracked.EnteredCrawl;
        if (crawler)
        {
            // Native damage restores HP and starts Critical when a lethal hit can consume a revive.
            savedRevives = __instance.revives;
            __instance.revives = 0;
            __1 = 0;
        }
        else if (shot != null && active.settings.Ragdoll.Value && active.settings.HeavyOverkill.Value
            && pose != null && active.GetEnemy(__instance).CanRagdoll
            && __instance.staggerType is not (EnemyController.hurtState.critical or EnemyController.hurtState.fire)
            && InjuryRules.CanOverkill(shot.IsHeavy, before, incomingHp, __instance.Preset.HP))
        {
            int fullHealth = __instance.Preset.HP;
            int revives = __instance.revives;
            bool canRecover = revives > 0 || __instance.queuedForRespawn;
            float chance = canRecover ? InjuryRules.OverkillChance(before, incomingHp, fullHealth, shot.Rifle) : 1f;
            double roll = canRecover ? shot.OverkillRoll(id) : 0d;
            bool bypass = roll < chance;
            active.Log($"overkill decision shot={shot.Id} enemy={id} damage={before - incomingHp:F1} hp={before:F1}/{fullHealth} revives={revives} chance={chance:F3} roll={roll:F3} bypass={bypass}");
            if (bypass)
            {
                savedRevives = revives;
                // The native lethal branch checks the per-enemy revive count before starting Critical().
                __instance.revives = 0;
            }
        }
        __state = new DamageSnapshot(true, shot, pose, contacts, incomingHp, before, critical, __state.PreviousSquibs, savedRevives, crawler);
        active.Log($"damage begin shot={shot?.Id ?? 0} enemy={id} state={__instance.state} beforeHp={before:F1} incomingHp={incomingHp} critical={critical} rays={contacts?.Rays.Count ?? 0} poseSource={(contacts?.Pose != null ? "ray" : pose != null ? "damage" : "none")}");
    }

    private static void DamageEnd(EnemyController __instance, DamageSnapshot __state, bool __runOriginal)
    {
        if (!__runOriginal || !__instance)
            return;
        EnemyKilled(__instance);
        if (!__state.Watched || !__instance.hitbox)
            return;
        if (__instance.state == EnemyController.enemystate.dead && __instance.hitbox.HP <= 0)
        {
            if (__state.Crawler)
                active.GetEnemy(__instance).PrepareCrawlDeath(__state.Pose);
            else if (__state.SavedRevives.HasValue)
            {
                EnemyEffects.PreventRespawn(__instance);
                active.Log($"overkill confirmed shot={__state.Shot.Id} enemy={__instance.GetInstanceID()}");
            }
        }
        bool downed = __instance.state == EnemyController.enemystate.dead || __instance.hitbox.HP <= 0;
        bool critical = !downed && !__state.WasCritical && __instance.staggerType == EnemyController.hurtState.critical;
        active.Log($"damage end shot={__state.Shot?.Id ?? 0} enemy={__instance.GetInstanceID()} state={__instance.state} hp={__instance.hitbox.HP} critical={critical} revives={__instance.revives} queued={__instance.queuedForRespawn} permanent={__instance.permadeath}");
        float damage = InjuryRules.DeliveredDamage(__state.PriorHealth, __state.IncomingHealth, __instance.hitbox.HP);
        if (!downed && !critical && (__state.Shot == null || damage <= 0))
            return;
        var rays = __state.Contacts?.Rays.ToArray() ?? Array.Empty<ImpactCuts.Ray>();
        var shot = __state.Shot ?? UnarmedImpact(__instance);
        bool finalDeath = __state.Shot != null && downed && IsFinalDeath(__instance);
        active.impacts.Enqueue(new Impact(__instance, shot, downed, critical, finalDeath, false, __state.Pose, damage, rays));
        if (__state.Shot != null && __state.WasCritical)
            (shot.DamagedProneTargets ??= new HashSet<int>()).Add(__instance.GetInstanceID());
    }

    private static bool IsFinalDeath(EnemyController target) => target.hitbox && InjuryRules.IsFinalDeath(
        target.state == EnemyController.enemystate.dead, target.hitbox.HP, target.revives,
        target.queuedForRespawn, target.permadeath);

    private static EnemyController AimOwner(Transform target) =>
        target ? target.GetComponentInParent<EnemyController>() : null;

    private static void AimTargetingBegin(PlayerAim __instance)
    {
        active.playerAim = __instance;
        var owner = AimOwner(__instance.target);
        if (!owner)
            return;
        if (owner.state == EnemyController.enemystate.dead)
            ClearAim(__instance);
        else if (active.enemies.TryGetValue(owner.GetInstanceID(), out var enemy))
            enemy.UpdateGroundAim();
    }

    private static void AimTargetingEnd(PlayerAim __instance)
    {
        var target = __instance.target;
        var owner = AimOwner(target);
        var box = __instance.crosshair;
        if (owner && box && box.gameObject.activeSelf
            && active.enemies.TryGetValue(owner.GetInstanceID(), out var enemy) && enemy.HasGroundAimPose)
        {
            enemy.UpdateGroundAim();
            box.position = target.position;
        }
    }

    private static void EnemyKilled(EnemyController __instance)
    {
        if (!__instance || __instance.state != EnemyController.enemystate.dead)
            return;
        var aim = active.playerAim;
        if (aim && AimOwner(aim.target) == __instance)
        {
            ClearAim(aim);
            active.Log($"aim lock cleared enemy={__instance.GetInstanceID()} dead=True");
        }
    }

    private static void ClearAim(PlayerAim aim)
    {
        aim.target = null;
        PlayerAim.TimeOnTarget = 0f;
        if (aim.crosshair)
            aim.crosshair.gameObject.SetActive(false);
        if (aim.crosshairBlocked)
            aim.crosshairBlocked.SetActive(false);
    }

    private static bool EnemyUpdateBegin(EnemyController __instance, out UpdateSnapshot __state)
    {
        __state = default;
        bool injuredAttack = UsesInjuredAttack(__instance, out var enemy);
        // Native Update can revive a queued corpse before LateUpdate observes it.
        enemy?.ObserveCorpse();
        if (enemy?.HoldRevivalAi == true)
            return false;
        if (!injuredAttack || !__instance.Preset)
            return true;
        // Presets are shared. Scope the range to this actor's synchronous Update, including its AI state check.
        var preset = __instance.Preset;
        __state = new UpdateSnapshot(preset, preset.attackRange, preset.stoppingDistance);
        preset.attackRange = enemy.SwipeRange(active.settings.Crawl.Value) + 0.35f;
        preset.stoppingDistance = Mathf.Min(preset.stoppingDistance, 2.25f);
        return true;
    }

    private static void EnemyUpdateEnd(UpdateSnapshot __state) => RestoreAttackRange(__state);

    private static void RestoreAttackRange(UpdateSnapshot state)
    {
        if (!state.Preset)
            return;
        state.Preset.attackRange = state.Range;
        state.Preset.stoppingDistance = state.StoppingDistance;
    }

    private static Exception EnemyUpdateCleanup(UpdateSnapshot __state, Exception __exception)
    {
        if (__exception != null)
            RestoreAttackRange(__state);
        return __exception;
    }

    private static bool UsesInjuredAttack(EnemyController target, out EnemyEffects effects)
    {
        effects = null;
        return target && active.enemies.TryGetValue(target.GetInstanceID(), out effects)
            && effects.UsesInjuredAttack(active.settings.Crawl.Value);
    }

    private static bool AttackStep(EnemyController._Attack_d__172 __instance, ref bool __result)
    {
        var target = __instance.__4__this;
        if (!UsesInjuredAttack(target, out var enemy))
            return true;
        try
        {
            if (__instance.__1__state == 0)
                enemy.TryStartSwipe(active.settings.Crawl.Value);
            target.EndHit();
            target.attacking = false;
            target.cancelCharge = true;
            if (target.state == EnemyController.enemystate.attack)
                target.state = EnemyController.enemystate.pursuit;
            __instance.__1__state = -1;
        }
        catch (Exception ex) { active.WarnOnce("injured attack", ex); }
        __result = false;
        return false;
    }

    private static bool NativeMeleeHit(EnemyController __instance) => !UsesInjuredAttack(__instance, out _);

    private static Shot UnarmedImpact(EnemyController target)
    {
        var player = PlayerAttack.instance;
        return new Shot(0, false, false, player ? player.transform.position : target.transform.position,
            player ? player.transform.forward : -target.transform.forward);
    }

    private static void StompSignal() => active.stompFrame = Time.frameCount;

    private static void StompConfirmed(EnemyController __instance, StudioEventEmitter __0, bool __runOriginal)
    {
        // Fire and Critical call the player stomp, then play the confirmed enemy's stomp emitter.
        if (!__runOriginal || active.stompFrame != Time.frameCount || !__0 || __0 != __instance.StompedSFX)
            return;
        active.stompFrame = -1;
        if (!EnemyEffects.IsSupported(__instance))
            return;
        try
        {
            var enemy = active.GetEnemy(__instance);
            var pose = CapturePreHitPose(__instance);
            active.stomps.Enqueue(new StompHit(enemy, pose));
            active.Log($"native stomp confirmed enemy={__instance.GetInstanceID()} hp={__instance.hitbox.HP}");
        }
        catch (Exception ex) { active.WarnOnce("native stomp", ex); }
    }

    private static Exception DamageFinish(EnemyController __instance, DamageSnapshot __state, Exception __exception)
    {
        if (__state.SavedRevives is int revives && __instance
            && (__instance.state != EnemyController.enemystate.dead || !__instance.hitbox || __instance.hitbox.HP > 0))
            __instance.revives = revives;
        if (__state.Shot != null)
            suppressedSquibs = __state.PreviousSquibs;
        if (__exception != null && __state.Shot != null)
            active.Log($"damage threw shot={__state.Shot.Id} {__exception}");
        return __exception;
    }

    private static bool StockSquib(SquibManager __instance)
    {
        if (!__instance || active == null || active.settings.BloodDensity <= 0 || currentShot?.IsFlare == true)
            return true;
        if (__instance == suppressedSquibs && currentShot is Shot shot)
        {
            active.Log($"stock squib suppressed shot={shot.Id} emitter={__instance.GetInstanceID()}");
            return false;
        }
        try
        {
            var owner = __instance.GetComponentInParent<EnemyController>();
            if (!owner || owner.squibs != __instance || !EnemyEffects.IsStandardReplika(owner)
                || owner.staggerType == EnemyController.hurtState.fire)
                return true;
            int ownerId = owner.GetInstanceID();
            if (currentShot?.ProneContacts?.ContainsKey(ownerId) == true
                || (active.proneSquibFrame == Time.frameCount && active.proneSquibs.Contains(ownerId)
                    && ShotHitboxes.IsProne(owner)))
                return false;
            active.GetEnemy(owner).EmitReplacementSquib(active.blood, active.settings.BloodDensity);
            active.Log($"stock squib replaced outside shot enemy={ownerId} emitter={__instance.GetInstanceID()} state={owner.state}");
            return false;
        }
        catch (Exception ex)
        {
            active.WarnOnce("stock squib replacement", ex);
            return true;
        }
    }

    private static void MoveBegin(EnemyController __instance, out MoveSnapshot? __state)
    {
        __state = null;
        if (!__instance || !active.enemies.TryGetValue(__instance.GetInstanceID(), out var enemy)
            || __instance.state == EnemyController.enemystate.dead)
            return;
        float scale = enemy.MovementScale(active.settings.Crawl.Value);
        bool injuredAttack = enemy.UsesInjuredAttack(active.settings.Crawl.Value);
        if (scale == 1f && !injuredAttack)
            return;
        __state = new MoveSnapshot(__instance.speedMod, __instance.stoppingDistance);
        __instance.speedMod *= scale;
        if (injuredAttack && __instance.state == EnemyController.enemystate.pursuit)
            __instance.stoppingDistance = Mathf.Min(__instance.stoppingDistance, 2.25f);
    }

    private static void MoveEnd(EnemyController __instance, MoveSnapshot? __state)
    {
        if (__state.HasValue && __instance)
        {
            __instance.speedMod = __state.Value.Speed;
            __instance.stoppingDistance = __state.Value.StoppingDistance;
        }
    }

    private static Exception MoveCleanup(EnemyController __instance, MoveSnapshot? __state, Exception __exception)
    {
        if (__exception != null)
            MoveEnd(__instance, __state);
        return __exception;
    }

    private static bool ResetEnemy(EnemyController __instance)
    {
        if (__instance && active.enemies.TryGetValue(__instance.GetInstanceID(), out var enemy))
        {
            if (enemy.ForcingCorpseRevival)
                return true;
            if (enemy.KeepFatalCutDead())
            {
                active.Log($"enemy revival reset blocked id={__instance.GetInstanceID()}");
                return false;
            }
            if (enemy.PrepareRevivalReset())
                return true;
            active.Log($"enemy reset id={__instance.GetInstanceID()}");
            enemy.Dispose();
            active.enemies.Remove(__instance.GetInstanceID());
        }
        return true;
    }

    private static void EnemyLoaded(EnemyController __instance, bool __runOriginal)
    {
        if (!__runOriginal || !__instance || !active.settings.Dismember.Value || __instance.permadeath)
            return;
        try
        {
            if (EnemyEffects.IsSupported(__instance) && EnemyEffects.RecoveryOwner(__instance) is { Length: > 0 } owner)
                active.loadedEnemies.Add((__instance, owner, EnemyEffects.IsCorpse(__instance)));
        }
        catch (Exception ex) { active.WarnOnce("revival load", ex); }
    }

    private void RecoverLoadedInjuries()
    {
        // Load can run during room activation, before the retained debris has changed visibility.
        foreach (var (target, owner, corpse) in loadedEnemies)
        {
            if (!target || !target.isActiveAndEnabled || target.permadeath)
                continue;
            try
            {
                var injuries = new List<SeveredPart>(physics.FindRetainedInjuries(owner));
                if (injuries.Count != 0)
                    GetEnemy(target).RestoreRetainedInjuries(injuries, corpse);
            }
            catch (Exception ex)
            {
                WarnOnce("recover retained injuries", ex);
                int id = target.GetInstanceID();
                if (enemies.TryGetValue(id, out var enemy))
                {
                    enemy.Dispose();
                    enemies.Remove(id);
                }
            }
        }
        loadedEnemies.Clear();
    }

    public override void OnLateUpdate()
    {
        try { arianeFlash?.Tick(ArianeFlashSuppressed); }
        catch (Exception ex)
        {
            arianeFlash.Reset();
            WarnOnce("Ariane overlay", ex);
        }
        if (Time.deltaTime <= 0)
            return;
        bool measure = verbose != null;
        long started = measure ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var player = PlayerAttack.instance;
        if (player && !PlayerState.noplayer)
        {
            try { weaponAudio?.Warmup(); }
            catch (Exception ex) { WarnOnce("gunshot preload", ex); }
            try { sounds?.Warmup(); }
            catch (Exception ex) { WarnOnce("gore audio preload", ex); }
            try { PrepareShotgunRack(player); }
            catch (Exception ex) { WarnOnce("shotgun rack marker", ex); }
        }
        if (!bloodPrepared && settings.BloodDensity > 0 && PlayerAttack.instance)
        {
            bloodPrepared = true;
            try { blood.Prepare(); }
            catch (Exception ex) { WarnOnce("blood preparation", ex); }
        }
        long afterWarmup = measure ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (settings.ScenePreview.Value && !panel.CapturesInput && Input.GetKeyDown(KeyCode.F8))
        {
            try { PreviewImpact(); }
            catch (Exception ex) { WarnOnce("scene preview", ex); }
        }

        if (loadedEnemies.Count != 0)
            RecoverLoadedInjuries();
        while (impacts.Count != 0)
        {
            Impact impact = impacts.Dequeue();
            try { ApplyImpact(impact); }
            catch (Exception ex) { WarnOnce("impact", ex); }
        }
        while (stomps.Count != 0)
        {
            var stomp = stomps.Dequeue();
            try
            {
                if (stomp.Enemy.Target && stomp.Enemy.Target.isActiveAndEnabled)
                    stomp.Enemy.Stomp(stomp.Pose, settings.Dismember.Value, settings.Ragdoll.Value,
                        blood, settings.BloodDensity, sounds);
            }
            catch (Exception ex) { WarnOnce("stomp impact", ex); }
            finally
            {
                stomp.Enemy.RestorePose();
                stomp.Enemy.ReleaseUnusedAnimation();
            }
        }
        long afterImpacts = measure ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        try { elsterInjuries?.Tick(); }
        catch (Exception ex) { WarnOnce("Elster injuries", ex); }
        try { combatEncounters?.Tick(); }
        catch (Exception ex) { WarnOnce("reinforcements", ex); }
        long afterCombat = measure ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        try { physics.Tick(); }
        catch (Exception ex) { WarnOnce("physics", ex); }
        long afterPhysics = measure ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

        if (settings.RevivalTest.Value && !panel.CapturesInput && Input.GetKeyDown(KeyCode.F10))
        {
            try { PreviewRevival(); }
            catch (Exception ex) { WarnOnce("revival test", ex); }
        }
        expired.Clear();
        foreach (var pair in enemies)
        {
            var enemy = pair.Value;
            try
            {
                if (!enemy.Target)
                    expired.Add(pair.Key);
                else if (enemy.Target.isActiveAndEnabled)
                    enemy.Tick(settings.Crawl.Value, settings.Dismember.Value, settings.Ragdoll.Value, blood, settings.BloodDensity, sounds);
                else
                    enemy.Suspend();
            }
            catch (Exception ex)
            {
                WarnOnce("pose", ex);
                expired.Add(pair.Key);
            }
        }
        foreach (int id in expired)
        {
            enemies[id].Dispose();
            enemies.Remove(id);
        }
        long afterPoses = measure ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        try { blood.Tick(); }
        catch (Exception ex) { WarnOnce("blood", ex); }
        try { elsterBlood.Tick(settings.BloodDensity); }
        catch (Exception ex) { WarnOnce("elster blood", ex); }
        if (measure)
        {
            long finished = System.Diagnostics.Stopwatch.GetTimestamp();
            double ms = 1000d / System.Diagnostics.Stopwatch.Frequency;
            double modMs = (finished - started) * ms;
            float frameMs = Time.unscaledDeltaTime * 1000f;
            int frame = Time.frameCount;
            if (modMs >= 18 || (modMs >= 5 || frameMs >= 34) && Time.unscaledTime >= nextTimingLog)
            {
                nextTimingLog = Time.unscaledTime + 0.65f;
                double previousModMs = lastMeasuredFrame == frame - 1 ? lastLateUpdateMs : 0;
                Log($"frame timing previous={frameMs:F1}ms previousLateUpdate={previousModMs:F1}ms lateUpdate={modMs:F1}ms " +
                    $"warmup={(afterWarmup - started) * ms:F1}ms impacts={(afterImpacts - afterWarmup) * ms:F1}ms " +
                    $"combat={(afterCombat - afterImpacts) * ms:F1}ms physics={(afterPhysics - afterCombat) * ms:F1}ms " +
                    $"poses={(afterPoses - afterPhysics) * ms:F1}ms blood={(finished - afterPoses) * ms:F1}ms");
            }
            lastMeasuredFrame = frame;
            lastLateUpdateMs = modMs;
        }
    }

    public override void OnUpdate()
    {
        try { panel?.Update(scene); }
        catch (Exception ex) { ReportPanelError(ex); }
        if (settings != null)
            UpdateDiagnosticLog();
        try { UpdateArianePreview(); }
        catch (Exception ex) { WarnOnce("Ariane F1 input", ex); }
        if (verbose != null && Time.unscaledTime >= nextLogFlush)
        {
            nextLogFlush = Time.unscaledTime + 0.75f;
            try { verbose.Flush(); }
            catch (IOException ex) { DisableLog(ex); }
        }
        elsterInjuries?.BeginFrame();
        foreach (var enemy in enemies.Values)
            enemy.RestorePose();
    }

    public override void OnGUI()
    {
        try
        {
            if (panel?.IsOpen == true)
                panel.Draw();
            else
                elsterInjuries?.Draw();
        }
        catch (Exception ex) { ReportPanelError(ex); }
    }

    private void ReportPanelError(Exception error)
    {
        try { panel?.Close(); }
        catch (Exception closeError) { WarnOnce("settings close", closeError); }
        WarnOnce("settings panel", error);
    }

    public override void OnDeinitializeMelon()
    {
        try { panel?.Close(); }
        catch (Exception ex) { WarnOnce("settings shutdown", ex); }
        Log("shutdown");
        try { arianeFlash?.Dispose(); }
        catch (Exception ex) { WarnOnce("Ariane overlay shutdown", ex); }
        elsterInjuries?.SceneChanged(reset: true);
        combatEncounters?.Dispose();
        weaponAudio?.Dispose();
        foreach (var enemy in enemies.Values)
            enemy.Dispose();
        enemies.Clear();
        blood?.Clear();
        elsterBlood?.Clear();
        CutCap.ReleaseMaterial();
        BloodMaterial.Dispose();
        sounds?.Dispose();
        physics?.ClearLimbs();
        verbose?.Dispose();
        verbose = null;
    }

    private void ApplyImpact(Impact impact)
    {
        var target = impact.Target;
        if (!target || !target.isActiveAndEnabled || (impact.ProneHit && !ShotHitboxes.IsProne(target)))
        {
            Log($"impact discarded shot={impact.Shot.Id} target no longer eligible");
            if (target && enemies.TryGetValue(target.GetInstanceID(), out var tracked))
                tracked.ReleaseUnusedAnimation();
            return;
        }

        var enemy = GetEnemy(target);
        Vector3? point = null;
        Vector3 shotDirection = Battlefield.OnPlane(target.transform.position - impact.Shot.Origin).normalized;
        if (impact.Rays.Length > 0)
        {
            var ray = impact.Rays[impact.Rays.Length - 1];
            point = new Vector3(ray.Point.X, ray.Point.Y, ray.Point.Z);
            shotDirection = new Vector3(ray.Direction.X, ray.Direction.Y, ray.Direction.Z).normalized;
        }
        if (shotDirection.sqrMagnitude < 0.01f)
            shotDirection = impact.Shot.Direction.normalized;
        var dir = Battlefield.OnPlane(shotDirection).normalized;
        if (dir.sqrMagnitude < 0.01f)
            dir = Battlefield.OnPlane(impact.Shot.Direction).normalized;

        try
        {
            bool finalDeath = impact.FinalDeath && IsFinalDeath(target);
            var sprayOrigin = finalDeath ? point ?? enemy.ShotCenter : Vector3.zero;
            enemy.Impact(impact.Shot.Id, dir, shotDirection, point, impact.Rays, impact.Damage, impact.Downed, impact.Critical, finalDeath, impact.Shot.IsHeavy,
                impact.Shot.Spread, impact.Shot.Rifle, impact.Preview, impact.Pose, settings.Dismember.Value, settings.Ragdoll.Value, settings.Crawl.Value,
                RedlineSettings.Read(settings.WaistChance, 1f), blood, settings.BloodDensity, sounds, impact.ProneHit,
                impact.Shot.CanCutBothLegs);
            if (finalDeath && impact.Shot.Id > 0 && !impact.Preview && !impact.ProneHit)
                elsterBlood.Queue(sprayOrigin, impact.Shot.IsHeavy, false, settings.BloodDensity);
        }
        finally
        {
            enemy.RestorePose();
            enemy.ReleaseUnusedAnimation();
        }
    }

    private EnemyEffects GetEnemy(EnemyController target)
    {
        int id = target.GetInstanceID();
        if (!enemies.TryGetValue(id, out var enemy))
            enemies.Add(id, enemy = new EnemyEffects(target, physics, skinMap, elsterBlood, settings, Log));
        return enemy;
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        if (sceneName == "REDLINE Effects")
            return;
        try { panel?.Close(resume: false); }
        catch (Exception ex) { WarnOnce("settings scene change", ex); }
        scene = sceneName;
        weaponAudio?.ClearRoom();
        Log($"scene loaded index={buildIndex} actors={enemies.Count} discardedImpacts={impacts.Count}");
        impacts.Clear();
        stomps.Clear();
        foreach (var enemy in enemies.Values)
        {
            enemy.CancelLimbReturns();
            enemy.ClearWaistSplit();
            enemy.ReleaseUnusedAnimation();
        }
        bool resetEffects = sceneName is "StartupPC" or "MainMenu";
        try { arianeFlash?.SceneChanged(); }
        catch (Exception ex) { WarnOnce("Ariane overlay scene change", ex); }
        elsterInjuries?.SceneChanged(resetEffects);
        combatEncounters?.SceneChanged();
        if (resetEffects)
            loadedEnemies.Clear();
        blood?.SetLevel(sceneName, resetEffects);
        elsterBlood?.SceneChanged(resetEffects);
        bloodPrepared = false;
        physics?.SetLevel(sceneName);
    }

    private void PreviewImpact()
    {
        var player = PlayerAttack.instance;
        if (!player)
            return;
        EnemyController nearest = null;
        float best = 18 * 18;
        foreach (var obj in UnityEngine.Object.FindObjectsOfType<EnemyController>())
        {
            if (!obj || !obj.isActiveAndEnabled || !EnemyEffects.IsSupported(obj) || obj.state == EnemyController.enemystate.dead)
                continue;
            float distance = (obj.transform.position - player.transform.position).sqrMagnitude;
            if (distance >= best)
                continue;
            nearest = obj;
            best = distance;
        }
        if (!nearest)
            return;
        var shot = new Shot(++shotNumber, true, false, player.transform.position, player.transform.forward);
        impacts.Enqueue(new Impact(nearest, shot, false, false, false, true, null, 0, Array.Empty<ImpactCuts.Ray>()));
    }

    private void PreviewRevival()
    {
        Log("revival test requested");
        var player = PlayerState.player;
        // The menu flag can stay set after gameplay resumes. The live state and pause flags gate F10.
        if (!player || PlayerState.gameState != PlayerState.gameStates.play || PlayerState.noplayer
            || PlayerState.paused || PlayerState.pauseMenu
            || PlayerState.eventScreen || PlayerState.cutscene || PlayerState.gameOver)
        {
            Log($"revival test skipped player={!!player} gameState={PlayerState.gameState} noplayer={PlayerState.noplayer} paused={PlayerState.paused} pauseMenu={PlayerState.pauseMenu} menu={PlayerState.menu} eventScreen={PlayerState.eventScreen} cutscene={PlayerState.cutscene} gameOver={PlayerState.gameOver}");
            return;
        }

        var origin = player.transform.position;
        EnemyController nearest = null;
        float closest = float.PositiveInfinity;
        bool preferReassembly = false;
        var room = PlayerState.currentRoom;
        var candidates = room ? room.GetComponentsInChildren<EnemyController>(false)
            : UnityEngine.Object.FindObjectsOfType<EnemyController>();
        foreach (var target in candidates)
        {
            if (!EnemyEffects.CanForceRevival(target))
                continue;
            bool reassembly = enemies.TryGetValue(target.GetInstanceID(), out var enemy) && enemy.HasReachableLimb();
            float distance = ((enemy?.ShotCenter ?? target.transform.position) - origin).sqrMagnitude;
            if (preferReassembly && !reassembly || reassembly == preferReassembly && distance >= closest)
                continue;
            nearest = target;
            closest = distance;
            preferReassembly = reassembly;
        }
        if (nearest)
        {
            Log($"revival test selected enemy={nearest.GetInstanceID()} type={nearest.Preset.Type} permanent={nearest.permadeath}");
            if (!GetEnemy(nearest).ForceRevival())
                Log($"revival test reset did not revive enemy={nearest.GetInstanceID()}");
        }
        else
            Log($"revival test found no eligible corpse room={(room ? room.roomName : "(none)")} candidates={candidates.Length}");
    }

    private readonly HashSet<string> warnings = new();

    private void WarnOnce(string feature, Exception error)
    {
        Log($"error feature={feature} {error}");
        if (warnings.Add(feature))
            LoggerInstance.Warning($"{feature}: {error}");
    }

    private void UpdateDiagnosticLog()
    {
        if (loggingRequested == settings.DiagnosticLog.Value)
            return;
        loggingRequested = settings.DiagnosticLog.Value;
        if (loggingRequested)
        {
            try
            {
                verbose = new StreamWriter(Path.Combine("UserData", "Redline.verbose.log"), logCreated);
                logCreated = true;
            }
            catch (Exception ex) { LoggerInstance.Warning($"Verbose log unavailable: {ex}"); }
            return;
        }
        var writer = verbose;
        verbose = null;
        try { writer?.Dispose(); }
        catch (IOException ex) { LoggerInstance.Warning($"Verbose log close failed: {ex.Message}"); }
    }

    private void Log(string message)
    {
        if (verbose == null)
            return;
        try
        {
            verbose.WriteLine($"{DateTime.UtcNow:O} frame={Time.frameCount} game={Time.time:F3} scene={scene} {message}");
        }
        catch (IOException error)
        {
            DisableLog(error);
        }
    }

    private void DisableLog(IOException error)
    {
        var failed = verbose;
        verbose = null;
        try { failed?.Dispose(); }
        catch (IOException) { }
        LoggerInstance.Warning($"Verbose log write failed: {error.Message}");
    }

    private sealed record Shot(int Id, bool IsHeavy, bool Spread, Vector3 Origin, Vector3 Direction, bool Rifle = false, bool IsFlare = false)
    {
        public AnWeapon Weapon { get; init; }
        public bool CanCutBothLegs => Weapon && Weapon.parentItem &&
            Weapon.parentItem._item is Items.itemlist.SMG or Items.itemlist.Shotgun;
        public int PreviousAmmo { get; init; }
        public readonly long Started = System.Diagnostics.Stopwatch.GetTimestamp();
        public readonly Dictionary<int, ShotContacts> Contacts = new();
        public Dictionary<int, ProneContact> ProneContacts;
        public HashSet<int> DamagedProneTargets;
        public readonly ShotHitboxes Hitboxes = new(target => active.enemies.TryGetValue(target.GetInstanceID(), out var enemy) ? enemy : null);
        private Dictionary<int, double> overkillRolls;

        public double OverkillRoll(int enemy)
        {
            overkillRolls ??= new Dictionary<int, double>();
            if (!overkillRolls.TryGetValue(enemy, out double roll))
                overkillRolls.Add(enemy, roll = overkillRandom.NextDouble());
            return roll;
        }
    }

    private sealed record ShotContacts(int Health, Rig.Snapshot Pose)
    {
        public readonly List<ImpactCuts.Ray> Rays = new(4);
    }

    private sealed record ProneContact(EnemyController Target)
    {
        public readonly List<ImpactCuts.Ray> Rays = new(4);
    }

    private readonly record struct DamageSnapshot(bool Watched, Shot Shot, Rig.Snapshot Pose, ShotContacts Contacts,
        int IncomingHealth, float PriorHealth, bool WasCritical, SquibManager PreviousSquibs, int? SavedRevives, bool Crawler);
    private readonly record struct UpdateSnapshot(AnEnemy Preset, float Range, float StoppingDistance);
    private readonly record struct MoveSnapshot(float Speed, float StoppingDistance);
    private readonly record struct Impact(EnemyController Target, Shot Shot, bool Downed, bool Critical, bool FinalDeath, bool Preview,
        Rig.Snapshot Pose, float Damage, ImpactCuts.Ray[] Rays, bool ProneHit = false);
    private readonly record struct StompHit(EnemyEffects Enemy, Rig.Snapshot Pose);
}
