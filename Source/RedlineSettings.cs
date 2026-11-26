using MelonLoader;
using UnityEngine;

namespace Redline;

internal sealed class RedlineSettings
{
    public readonly MelonPreferences_Category Category;
    public readonly MelonPreferences_Entry<float> Blood, Spray, Pools, WoundFlow, ElsterStains;
    public readonly MelonPreferences_Entry<float> CutSensitivity, WaistChance, ThrowSpeed, Lift, Spin, CutVolume;
    public readonly MelonPreferences_Entry<float> ReinforcementRate, GunshotVolume, GunshotRoom, HandlingVolume;
    public readonly MelonPreferences_Entry<bool> MagCaches, ElsterTrauma, ArianeFlash, RecordedGunshots, WeaponHandling;
    public readonly MelonPreferences_Entry<bool> WallSplatter, Dismember, Crawl, Decapitation, Reassemble, Ragdoll,
        ExtremeRagdoll, HeavyOverkill, DiagnosticLog, ScenePreview, RevivalTest;

    public RedlineSettings()
    {
        Category = MelonPreferences.CreateCategory("Redline", "REDLINE");
        Category.SetFilePath("UserData/Redline.cfg");
        ReinforcementRate = Category.CreateEntry("ReinforcementRate", 0f);
        MagCaches = Category.CreateEntry("MagCaches", true,
            description: "Let eligible final kills and finishing stomps drop ammo");
        ElsterTrauma = Category.CreateEntry("ElsterTrauma", true);
        ArianeFlash = Category.CreateEntry("ArianeFlash", true,
            description: "Enable the low-health Ariane flash and its F1 preview");
        RecordedGunshots = Category.CreateEntry("RecordedGunshots", true);
        GunshotVolume = Category.CreateEntry("GunshotVolume", 1f);
        GunshotRoom = Category.CreateEntry("GunshotRoom", 1f,
            description: "Room reflections and reverb on recorded shots and weapon handling, from 0 to 2");
        // Keep the existing key so previously saved on/off choices carry over.
        WeaponHandling = Category.CreateEntry("ShotgunHandling", true,
            description: "Use local handling WAVs when available and soften empty clicks");
        HandlingVolume = Category.CreateEntry("HandlingVolume", 0.65f,
            description: "Volume multiplier for handling and empty clicks");
        Blood = Category.CreateEntry("Blood", 1.25f, description: "Blood density, from 0 to 2.5");
        Spray = Category.CreateEntry("SprayMultiplier", 1f);
        Pools = Category.CreateEntry("PoolSizeMultiplier", 1f);
        WoundFlow = Category.CreateEntry("WoundFlowMultiplier", 1f);
        ElsterStains = Category.CreateEntry("ElsterStainMultiplier", 1f);
        WallSplatter = Category.CreateEntry("WallSplatter", true);
        Dismember = Category.CreateEntry("Dismemberment", true);
        Crawl = Category.CreateEntry("Crawling", true);
        CutSensitivity = Category.CreateEntry("LimbCutSensitivity", 1f);
        Decapitation = Category.CreateEntry("Decapitation", true);
        WaistChance = Category.CreateEntry("WaistCutChance", 0.42f,
            description: "Base waist-split chance on a heavy fatal shot, from 0 to 1");
        Reassemble = Category.CreateEntry("ReassembleOnRevival", true,
            description: "Let Replikas that revive from death pull reachable limbs back");
        Ragdoll = Category.CreateEntry("Ragdoll", true);
        ExtremeRagdoll = Category.CreateEntry("ExtremeRagdoll", false,
            description: "Increase heavy-shot ragdoll knockback, lift, and spin within the launch limits");
        ThrowSpeed = Category.CreateEntry("RagdollThrowMultiplier", 1.15f);
        Lift = Category.CreateEntry("RagdollLiftMultiplier", 1f);
        Spin = Category.CreateEntry("RagdollSpinMultiplier", 1f);
        HeavyOverkill = Category.CreateEntry("HeavyOverkill", true);
        CutVolume = Category.CreateEntry("CutVolumeMultiplier", 1f);
        DiagnosticLog = Category.CreateEntry("DiagnosticLog", true);
        ScenePreview = Category.CreateEntry("ScenePreviewF8", false,
            description: "F8 previews a leg hit on the nearest active enemy without dealing damage");
        RevivalTest = Category.CreateEntry("RevivalTestF10", false,
            description: "F10 revives standard Replika corpses in the room, including permanent deaths");
        Category.SaveToFile(false);
    }

    public void ResetToDefaults()
    {
        foreach (var entry in Category.Entries)
            entry.ResetToDefault();
    }

    public float BloodDensity => Read(Blood, 2.5f);
    public static float Factor(MelonPreferences_Entry<float> entry) => Read(entry, 2f);

    public static float Read(MelonPreferences_Entry<float> entry, float max) =>
        Mathf.Clamp(float.IsNaN(entry.Value) ? entry.DefaultValue : entry.Value, 0f, max);
}
