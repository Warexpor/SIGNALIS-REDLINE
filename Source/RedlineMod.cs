using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(Redline.RedlineMod), "REDLINE", "0.1.2", "Redline")]
[assembly: MelonGame("rose-engine", "SIGNALIS")]

namespace Redline;

public sealed class RedlineMod : MelonMod
{
    private static RedlineMod active;
    private readonly Dictionary<int, EnemyEffects> enemies = new();
    private readonly List<int> expired = new();
    private readonly Queue<Impact> impacts = new();
    private BloodEffects blood;
    private EffectsPhysics physics;
    private SkinMap skinMap;
    private MelonPreferences_Entry<float> bloodAmount;
    private MelonPreferences_Entry<bool> dismember;
    private MelonPreferences_Entry<bool> crawl;
    private MelonPreferences_Entry<bool> ragdoll;
    private MelonPreferences_Entry<bool> diagnosticLog;
    private MelonPreferences_Entry<bool> scenePreview;
    private Camera.CameraCallback beforeCull;
    private int lastRenderFrame = -1;
    private static Shot? currentShot;

    public override void OnInitializeMelon()
    {
        active = this;
        var settings = MelonPreferences.CreateCategory("Redline", "REDLINE");
        settings.SetFilePath("UserData/Redline.cfg");
        bloodAmount = settings.CreateEntry("Blood", 1.25f, description: "Blood density, 0–2.5");
        dismember = settings.CreateEntry("Dismemberment", true);
        crawl = settings.CreateEntry("Crawling", true);
        ragdoll = settings.CreateEntry("Ragdoll", true);
        diagnosticLog = settings.CreateEntry("DiagnosticLog", false);
        scenePreview = settings.CreateEntry("ScenePreviewF8", false,
            description: "F8 previews a leg hit on the nearest active enemy without dealing damage");
        settings.SaveToFile(false);

        blood = new BloodEffects();
        physics = new EffectsPhysics();
        try { skinMap = new SkinMap(Path.Combine("UserData", "RedlineSkinMap.gz")); }
        catch (Exception error)
        {
            WarnOnce("Skin map", error);
            skinMap = new SkinMap("");
        }
        Hook(typeof(PlayerAttack), "RaycastShot", nameof(ShotBegin), null, nameof(ShotEnd));
        Hook(typeof(EnemyController), "TakeDamage", nameof(DamageBegin), nameof(DamageEnd), null,
            typeof(float), typeof(float), typeof(float), typeof(bool));
        Hook(typeof(EnemyController), "Movement", nameof(MoveBegin), nameof(MoveEnd), nameof(MoveCleanup));
        Hook(typeof(EnemyController), "ResetEnemy", nameof(ResetEnemy), null);
        try
        {
            beforeCull = (Action<Camera>)BeforeCull;
            Camera.onPreCull += beforeCull;
        }
        catch (Exception ex)
        {
            beforeCull = null;
            WarnOnce("camera hook", ex);
        }
        LoggerInstance.Msg($"REDLINE loaded with {skinMap.Count} local skin bindings.");
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
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"Could not hook {type.Name}.{name}: {exception}");
        }
    }

    private static HarmonyMethod Handler(string name) => name == null ? null :
        new HarmonyMethod(AccessTools.DeclaredMethod(typeof(RedlineMod), name));

    private static void ShotBegin(PlayerAttack __instance, out Shot? __state)
    {
        __state = currentShot;
        try
        {
            var weapon = __instance.weapon;
            if (__instance.melee || !weapon)
                return;
            var item = weapon.parentItem;
            bool heavy = item && item._item is Items.itemlist.Shotgun or Items.itemlist.FlakGun
                or Items.itemlist.Rifle or Items.itemlist.Revolver;
            var muzzle = PlayerAttack.muzzle;
            var actor = __instance.transform;
            currentShot = new Shot(heavy, muzzle ? muzzle.position : actor.position, actor.forward);
        }
        catch (Exception ex)
        {
            active.WarnOnce("shot", ex);
        }
    }

    private static Exception ShotEnd(Shot? __state, Exception __exception)
    {
        currentShot = __state;
        return __exception;
    }

    private static void DamageBegin(EnemyController __instance, out DamageSnapshot __state)
    {
        __state = default;
        if (currentShot is not Shot shot || !__instance || !__instance.hitbox || !__instance.Preset
            || __instance.state == EnemyController.enemystate.dead)
            return;
        Rig.Snapshot pose = null;
        try
        {
            if (active.ragdoll.Value && EnemyEffects.IsSupported(__instance))
                pose = active.GetEnemy(__instance).CapturePose();
        }
        catch (Exception ex) { active.WarnOnce("pre-hit pose", ex); }
        __state = new DamageSnapshot(shot, pose, true);
    }

    private static void DamageEnd(EnemyController __instance, DamageSnapshot __state, bool __runOriginal)
    {
        if (!__state.Tracked || !__runOriginal || !__instance)
            return;
        bool downed = __instance.state == EnemyController.enemystate.dead;
        active.impacts.Enqueue(new Impact(__instance, __state.Shot, downed, false, __state.Pose));
    }

    private static void MoveBegin(EnemyController __instance, out float? __state)
    {
        __state = null;
        if (!active.crawl.Value || !__instance || !active.enemies.TryGetValue(__instance.GetInstanceID(), out var enemy)
            || !enemy.IsCrawling || __instance.state == EnemyController.enemystate.dead)
            return;
        __state = __instance.speedMod;
        __instance.speedMod *= 0.46f;
    }

    private static void MoveEnd(EnemyController __instance, float? __state)
    {
        if (__state.HasValue && __instance)
            __instance.speedMod = __state.Value;
    }

    private static Exception MoveCleanup(EnemyController __instance, float? __state, Exception __exception)
    {
        if (__exception != null)
            MoveEnd(__instance, __state);
        return __exception;
    }

    private static void ResetEnemy(EnemyController __instance)
    {
        if (__instance && active.enemies.TryGetValue(__instance.GetInstanceID(), out var enemy))
        {
            enemy.Dispose();
            active.enemies.Remove(__instance.GetInstanceID());
        }
    }

    public override void OnLateUpdate()
    {
        if (Time.deltaTime <= 0)
            return;
        if (scenePreview.Value && Input.GetKeyDown(KeyCode.F8))
        {
            try { PreviewImpact(); }
            catch (Exception ex) { WarnOnce("scene preview", ex); }
        }

        while (impacts.Count != 0)
        {
            Impact impact = impacts.Dequeue();
            try { ApplyImpact(impact); }
            catch (Exception ex) { WarnOnce("impact", ex); }
        }

        try { physics.Tick(); }
        catch (Exception ex) { WarnOnce("physics", ex); }

        expired.Clear();
        foreach (var pair in enemies)
        {
            var enemy = pair.Value;
            try
            {
                if (!enemy.Target)
                    expired.Add(pair.Key);
                else if (enemy.Target.isActiveAndEnabled)
                    enemy.Tick(crawl.Value, ragdoll.Value, blood, bloodAmount.Value);
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
        try { blood.Tick(); }
        catch (Exception ex) { WarnOnce("blood", ex); }
    }

    public override void OnUpdate()
    {
        foreach (var enemy in enemies.Values)
            enemy.RestorePose();
    }

    private void BeforeCull(Camera camera)
    {
        if (lastRenderFrame == Time.frameCount || !camera || camera.cameraType != CameraType.Game)
            return;
        lastRenderFrame = Time.frameCount;
        foreach (var enemy in enemies.Values)
        {
            try { enemy.BeforeRender(); }
            catch (Exception ex) { WarnOnce("render pose", ex); }
        }
    }

    public override void OnDeinitializeMelon()
    {
        if (beforeCull != null)
            Camera.onPreCull -= beforeCull;
        foreach (var enemy in enemies.Values)
            enemy.Dispose();
        enemies.Clear();
        blood?.Clear();
        physics?.ClearLimbs();
    }

    private void ApplyImpact(Impact impact)
    {
        var target = impact.Target;
        if (!target || !target.isActiveAndEnabled)
            return;

        var enemy = GetEnemy(target);
        Vector3 dir = Battlefield.OnPlane(target.transform.position - impact.Shot.Origin).normalized;
        if (dir.sqrMagnitude < 0.01f)
            dir = Battlefield.OnPlane(impact.Shot.Direction).normalized;

        try
        {
            enemy.Impact(dir, impact.Downed, impact.Shot.IsHeavy, impact.Preview, impact.Pose,
                dismember.Value, ragdoll.Value, blood, bloodAmount.Value);
        }
        finally
        {
            if (impact.Downed && impact.Pose != null)
                enemy.RestorePose();
        }
    }

    private EnemyEffects GetEnemy(EnemyController target)
    {
        int id = target.GetInstanceID();
        if (!enemies.TryGetValue(id, out var enemy))
            enemies.Add(id, enemy = new EnemyEffects(target, physics, skinMap, Log));
        return enemy;
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        impacts.Clear();
        blood?.Clear();
        physics?.ClearLimbs();
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
            if (!obj || !obj.isActiveAndEnabled || !obj.Preset || obj.state == EnemyController.enemystate.dead)
                continue;
            float distance = (obj.transform.position - player.transform.position).sqrMagnitude;
            if (distance >= best)
                continue;
            nearest = obj;
            best = distance;
        }
        if (!nearest)
            return;
        var shot = new Shot(true, player.transform.position, player.transform.forward);
        impacts.Enqueue(new Impact(nearest, shot, false, true, null));
    }

    private readonly HashSet<string> warnings = new();

    private void WarnOnce(string feature, Exception error)
    {
        if (warnings.Add(feature))
            LoggerInstance.Warning($"{feature}: {error}");
    }

    private void Log(string message)
    {
        if (diagnosticLog.Value)
            LoggerInstance.Msg(message);
    }

    private readonly record struct Shot(bool IsHeavy, Vector3 Origin, Vector3 Direction);
    private readonly record struct DamageSnapshot(Shot Shot, Rig.Snapshot Pose, bool Tracked);
    private readonly record struct Impact(EnemyController Target, Shot Shot, bool Downed, bool Preview, Rig.Snapshot Pose);
}
