using System;
using System.Collections.Generic;
using System.IO;
using FMOD;
using FMOD.Studio;
using FMODUnity;
using UnityEngine;

namespace Redline;

internal sealed class WeaponAudio(RedlineSettings settings, Action<string> log)
{
    private const string WeaponEvent = "event:/Elster/Weapons/";
    private const string ShotgunEvent = WeaponEvent + "Shotgun/";
    internal const string SmgReloadEvent = WeaponEvent + "SMG/Reload";
    internal const string SmgReloadMarkerPrefix = "REDLINE/SMG/Reload/";
    private const string PumpShort = ShotgunEvent + "PumpShort";
    private const string Pump = ShotgunEvent + "Pump";
    private const string ShellInsert = ShotgunEvent + "Insert";
    private static readonly HandlingCue rackOpen = new(new("ShotgunHandling/rack-open", 79, 102), Gain: 0.30f);
    private static readonly HandlingCue rackClose = new(new("ShotgunHandling/rack-close", 90, 107), Gain: 0.31f);
    private static readonly HandlingCue shell = new(new("ShotgunHandling/shell-insert", 79, 61), Gain: 0.17f);
    private static readonly HandlingCue pistolOut = new(new("PistolHandling/mag-release", 104, 130));
    private static readonly HandlingCue pistolIn = new(new("PistolHandling/mag-insert", 101, 85));
    private static readonly HandlingCue pistolClose = new(new("PistolHandling/slide-forward", 16, 115));
    private static readonly HandlingCue smgOut = new(new("SmgHandling/mag-release", 10, 150));
    private static readonly HandlingCue smgIn = new(new("SmgHandling/mag-insert", 218, 100), Gain: 0.31f);
    private static readonly HandlingCue smgClose = new(new("SmgHandling/action-close", 10, 140));
    private static readonly HandlingCue revolverOut = new(new("RevolverHandling/cylinder-eject", 48, 130),
        Alternate: new("RevolverHandling/cylinder-eject-alt", 403, 46));
    private static readonly HandlingCue revolverIn = new(new("RevolverHandling/cartridge-insert", 87, 100),
        Alternate: new("RevolverHandling/cartridge-insert-alt", 351, 40));
    private static readonly HandlingCue revolverClose = new(new("RevolverHandling/cylinder-close", 40, 106));
    private static readonly HandlingCue rifleOut = new(new("RifleHandling/breech-open", 126, 153));
    private static readonly HandlingCue rifleIn = new(new("RifleHandling/cartridge-insert", 49, 115));
    private static readonly HandlingCue rifleClose = new(new("RifleHandling/breech-close", 83, 100));
    private static readonly HandlingCue[] shortPump =
    {
        rackOpen, rackClose with { Offset = 0.151f }
    };
    // Vanilla SMG reload is mag out, a long pause, mag in, then a late subtle handle drag.
    private static readonly HandlingCue[] smgReload =
    {
        smgOut with { Offset = 12f / 60f }, smgIn with { Offset = 51f / 60f }
        // Third beat (late handle drag) removed for now.
        // smgClose with { Offset = 84f / 60f, Gain = 0.20f }
    };
    private static readonly HandlingCue[] smgInventoryReload =
    {
        smgOut with { Offset = 0.24f }, smgIn with { Offset = 0.86f }
        // Third beat (late handle drag) removed for now.
        // smgClose with { Offset = 1.45f, Gain = 0.20f }
    };
    private static readonly Dictionary<string, HandlingCue[]> animationReloads = new(StringComparer.Ordinal)
    {
        [WeaponEvent + "Pistol/Reload"] = new[] { pistolOut, pistolIn, pistolClose },
        [WeaponEvent + "Revolver/Reload"] = new[] { revolverOut, revolverIn, revolverClose },
        [WeaponEvent + "Rifle/Reload"] = new[] { rifleOut, rifleIn, rifleClose }
    };
    private static readonly Dictionary<string, HandlingCue[]> handlingEvents = new(StringComparer.Ordinal)
    {
        [PumpShort] = shortPump,
        [Pump] = new[] { rackOpen, rackClose with { Offset = 0.192f } },
        [ShellInsert] = new[] { shell },
        [ShotgunEvent + "Reload"] = new HandlingCue[] { new(new("ShotgunHandling/reload-accent", 35, 105)) },
        [ShotgunEvent + "Reload Inventory"] = new[]
        {
            shell, shell with { Offset = 0.25f }, rackOpen with { Offset = 0.55f }, rackClose with { Offset = 0.742f }
        },
        [WeaponEvent + "Pistol/MagDrop"] = new[] { pistolOut },
        [WeaponEvent + "Pistol/MagInsert"] = new[] { pistolIn },
        [WeaponEvent + "Pistol/SlideForward"] = new[] { pistolClose },
        [WeaponEvent + "Pistol/Reload Inventory"] = new[]
        {
            pistolOut with { Offset = 0.11f }, pistolIn with { Offset = 0.51f }, pistolClose with { Offset = 0.83f }
        },
        [WeaponEvent + "SMG/MagDrop"] = new[] { smgOut },
        [WeaponEvent + "SMG/MagInsert"] = new[] { smgIn },
        [SmgReloadEvent + " Inventory"] = smgInventoryReload,
        [SmgReloadEvent] = smgReload,
        [WeaponEvent + "Revolver/Eject"] = new[] { revolverOut },
        [WeaponEvent + "Revolver/Insert"] = new[] { revolverIn },
        [WeaponEvent + "Revolver/SnapClosed"] = new[] { revolverClose },
        [WeaponEvent + "Revolver/Reload Inventory"] = new HandlingCue[]
        {
            new(new("RevolverHandling/inventory-reload", 18, 0),
                Alternate: new("RevolverHandling/inventory-reload-alt", 18, 0))
        },
        [WeaponEvent + "Rifle/Eject"] = new[] { rifleOut },
        [WeaponEvent + "Rifle/Insert"] = new[] { rifleIn },
        [WeaponEvent + "Rifle/SnapClosed"] = new[] { rifleClose },
        [WeaponEvent + "Rifle/Reload Inventory"] = new[]
        {
            rifleOut with { Offset = 0.32f }, rifleIn with { Offset = 0.99f }, rifleClose with { Offset = 1.79f }
        }
    };
    private static readonly (Items.itemlist Item, string Filename)[] recordings =
    {
        (Items.itemlist.Pistol, "pistol"),
        (Items.itemlist.SMG, "smg"),
        (Items.itemlist.Shotgun, "shotgun"),
        (Items.itemlist.Revolver, "revolver"),
        (Items.itemlist.Rifle, "rifle"),
        (Items.itemlist.FlakGun, "flak"),
        (Items.itemlist.FlareGun, "flare")
    };
    private readonly Dictionary<Items.itemlist, Samples> banks = new();
    private readonly System.Random random = new();
    private readonly GunshotAcoustics acoustics = new();
    private readonly Dictionary<string, Sound> handling = new(StringComparer.Ordinal);
    private readonly HashSet<string> nextAlternate = new(StringComparer.Ordinal);
    private Bus bus;
    private ChannelGroup group;
    private ChannelGroup outputGroup;
    private DSP outputLimiter;
    private bool outputPrepared;
    private ChannelGroup mixGroup;
    private DSP limiter;
    private ChannelGroup reportGroup;
    private DSP reportEq;
    private ChannelGroup shotgunReportGroup;
    private DSP shotgunReportEq;
    private DSP shotgunMidEq;
    private ChannelGroup handlingGroup;
    private bool locked;
    private bool roomFailed;
    private bool hasSamples;
    private float firedRackAt = float.NegativeInfinity;
    private float handledPumpAt = float.NegativeInfinity;
    private float handledShellAt = float.NegativeInfinity;
    private int playedFrame = -1;
    private string playedEvent;
    private int nextRecording;
    private int sampleRate;
    private IEnumerator<HandlingCue[]> warmHandling;
    private bool handlingWarm;
    private int nextSmgReloadMarker = -1;
    private float smgReloadCoveredAt = float.NegativeInfinity;

    internal static int SmgReloadMarkerCount => smgReload.Length;
    internal static float SmgReloadMarkerTime(int marker) => smgReload[marker].Offset;

    public bool BeginSmgReload(bool markersReady)
    {
        nextSmgReloadMarker = -1;
        if (!markersReady || !settings.WeaponHandling.Value || CombatWorld.Networked || !GetHandlingOutput())
            return false;
        foreach (var cue in smgReload)
            if (SelectHandling(cue).File == null)
                return false;
        nextSmgReloadMarker = 0;
        smgReloadCoveredAt = Time.unscaledTime;
        log($"weapon reload uses animation cues event={SmgReloadEvent}");
        return true;
    }

    public void PlaySmgReloadMarker(int marker, Vector3 origin)
    {
        if (marker < 0 || marker >= smgReload.Length || marker != nextSmgReloadMarker)
            return;
        nextSmgReloadMarker = marker + 1 < smgReload.Length ? marker + 1 : -1;
        var cue = smgReload[marker] with { Offset = 0f };
        if (TryHandling(new[] { cue }, origin: origin))
            log($"weapon handling animation marker weapon=SMG beat={marker + 1} file={cue.Take.File} elapsed={Time.unscaledTime - smgReloadCoveredAt:F2}s");
    }

    public bool Replaced(string path) =>
        playedFrame == Time.frameCount && string.Equals(playedEvent, path, StringComparison.Ordinal);

    public void Warmup()
    {
        if (settings.RecordedGunshots.Value && nextRecording < recordings.Length)
            Load(recordings[nextRecording++].Item);
        else if (settings.WeaponHandling.Value && !handlingWarm)
        {
            warmHandling ??= handlingEvents.Values.GetEnumerator();
            if (warmHandling.MoveNext())
                foreach (var cue in warmHandling.Current)
                {
                    LoadHandling(cue.Take.File);
                    if (cue.Alternate.File != null)
                        LoadHandling(cue.Alternate.File);
                }
            else
            {
                handlingWarm = true;
                warmHandling.Dispose();
                warmHandling = null;
            }
        }
        if ((hasSamples || settings.WeaponHandling.Value) && GetOutput())
        {
            if (settings.RecordedGunshots.Value)
            {
                GetReportOutput();
                if (banks.TryGetValue(Items.itemlist.Shotgun, out var shotgun) && shotgun.Count > 0)
                    GetReportOutput(shotgun: true, raw: shotgun.Raw);
            }
            if (settings.WeaponHandling.Value) GetHandlingOutput();
        }
    }

    public void Play(AnWeapon weapon, Vector3 origin)
    {
        if (!weapon || !weapon.parentItem)
            return;
        var item = weapon.parentItem._item;
        bool shotgun = item == Items.itemlist.Shotgun;
        if (shotgun)
            firedRackAt = float.NegativeInfinity;
        if (PlayReport(weapon, item, origin) && shotgun && TryHandling(shortPump, 0.285f, origin))
        {
            firedRackAt = Time.unscaledTime;
            log("shotgun post-shot rack scheduled rack-open + rack-close");
        }
    }

    private bool PlayReport(AnWeapon weapon, Items.itemlist item, Vector3 origin)
    {
        if (!settings.RecordedGunshots.Value || string.IsNullOrEmpty(weapon.shotMod))
            return false;
        float volume = RedlineSettings.Factor(settings.GunshotVolume);
        var samples = Load(item);
        if (samples.Count == 0 || !GetOutput())
            return false;
        int next = samples.Count == 1 ? 0 : samples.Last < 0 ? random.Next(samples.Count)
            : (samples.Last + random.Next(1, samples.Count)) % samples.Count;
        bool shotgun = item == Items.itemlist.Shotgun;
        var output = GetReportOutput(shotgun, samples.Raw);
        var result = RuntimeManager.CoreSystem.playSound(samples.Files[next], output, true, out var channel);
        if (result != RESULT.OK)
        {
            log($"gunshot playback failed weapon={item} error={result}");
            return false;
        }
        float pitch = samples.Raw ? 1f : Pitch();
        float roomMix = RedlineSettings.Factor(settings.GunshotRoom) * (item == Items.itemlist.SMG ? 0.79f : 1.08f);
        float headroom = 0.72f / (1f + 0.21f * roomMix);
        bool tunedShotgun = shotgun && !samples.Raw && shotgunReportGroup.hasHandle();
        float reportGain = shotgun && !samples.Raw ? (tunedShotgun ? 1.78f : 1.48f) : 1f;
        if (channel.setVolume(volume * headroom * reportGain) != RESULT.OK || channel.setPitch(pitch) != RESULT.OK ||
            channel.setPaused(false) != RESULT.OK)
        {
            channel.stop();
            log($"gunshot playback setup failed weapon={item} take={next + 1}");
            return false;
        }
        samples.Last = next;
        playedEvent = weapon.shotMod;
        playedFrame = Time.frameCount;
        log($"gunshot replaced weapon={item} take={next + 1} event={playedEvent}{(samples.Raw ? " raw=True" : "")}");
        if (!samples.Raw)
        {
            float lowEnd = item switch
            {
                Items.itemlist.Shotgun when tunedShotgun => 0.31f,
                Items.itemlist.Shotgun or Items.itemlist.Rifle or Items.itemlist.Revolver or Items.itemlist.FlakGun => 0.27f,
                Items.itemlist.SMG => 0.11f,
                _ => 0.16f
            };
            try { acoustics.PlayPressure(new RoomCue(samples.Files[next], pitch, volume * headroom * lowEnd), mixGroup, log); }
            catch (Exception error) { log($"gunshot low-end layer failed: {error}"); }
        }
        PlayRoom(new RoomCue(samples.Files[next], pitch, volume * headroom * roomMix), origin, false);
        return true;
    }

    public static bool IsShotgunPump(string path) => path is Pump or PumpShort;

    private static bool IsSmgReloadGranular(string path) => path == WeaponEvent + "SMG/MagDrop"
        || path == WeaponEvent + "SMG/MagInsert" || path == WeaponEvent + "SMG/SlideForward";

    public bool PlayReloadRack() => PlayHandling(Pump, reloadMarker: true);

    public bool PlayPumpEmitter(string path)
    {
        if (!IsShotgunPump(path) || !settings.WeaponHandling.Value || CombatWorld.Networked)
            return false;
        // The animated pump can fire late. The rack scheduled for this shot already took its place.
        if (path == PumpShort && settings.RecordedGunshots.Value && !float.IsNegativeInfinity(firedRackAt))
            return true;
        return PlayHandling(path);
    }

    public bool PlayDryClick(string path, Vector3 position)
    {
        // The stock events use three click families with different levels. Match them to the rack.
        float gain = path switch
        {
            WeaponEvent + "Pistol/Empty" or WeaponEvent + "Rifle/Empty" => 0.28f,
            WeaponEvent + "SMG/Empty" or WeaponEvent + "FlareGun/Empty" => 0.31f,
            ShotgunEvent + "Empty" or WeaponEvent + "Revolver/Empty" => 0.49f,
            _ => 0f
        };
        if (gain == 0 || !settings.WeaponHandling.Value || CombatWorld.Networked)
            return false;

        var click = RuntimeManager.CreateInstance(path);
        float volume = gain * RedlineSettings.Factor(settings.HandlingVolume);
        try
        {
            if (click.set3DAttributes(RuntimeUtils.To3DAttributes(position)) != RESULT.OK ||
                click.setVolume(volume) != RESULT.OK || click.start() != RESULT.OK)
                return false;
        }
        finally { click.release(); }
        log($"weapon empty click level={volume:F2} event={path}");
        return true;
    }

    public bool PlayHandling(string path, bool reloadMarker = false)
    {
        if (path == null || !settings.WeaponHandling.Value || CombatWorld.Networked)
            return false;
        if (IsSmgReloadGranular(path))
        {
            if (Time.unscaledTime - smgReloadCoveredAt < 2.2f)
            {
                log($"SMG reload granular suppressed event={path} elapsed={Time.unscaledTime - smgReloadCoveredAt:F2}s");
                return true;
            }
        }
        else if (path == SmgReloadEvent || path == SmgReloadEvent + " Inventory")
            smgReloadCoveredAt = Time.unscaledTime;
        if (animationReloads.TryGetValue(path, out var animation))
        {
            if (!GetHandlingOutput())
                return false;
            foreach (var cue in animation)
                if (SelectHandling(cue).File == null)
                    return false;
            log($"weapon reload uses animation cues event={path}");
            return true;
        }
        if (!handlingEvents.TryGetValue(path, out var cues))
            return false;
        bool pumpCue = IsShotgunPump(path);
        if (pumpCue && (Time.unscaledTime - handledPumpAt < 0.12f ||
            !reloadMarker && Time.unscaledTime - firedRackAt < 0.60f))
        {
            log($"shotgun duplicate pump suppressed event={path}");
            return true;
        }
        bool shellCue = path == ShellInsert;
        // The intro and the next reload clip can both cue the same insertion during the transition.
        if (shellCue && Time.unscaledTime - handledShellAt < 0.08f)
        {
            log("shotgun duplicate shell insert suppressed");
            return true;
        }
        if (!TryHandling(cues))
        {
            log($"weapon handling native fallback event={path}");
            return false;
        }
        if (pumpCue)
            handledPumpAt = Time.unscaledTime;
        if (shellCue)
            handledShellAt = Time.unscaledTime;
        log($"weapon handling replaced event={path} parts={cues.Length}");
        return true;
    }

    private bool TryHandling(HandlingCue[] cues, float delay = 0f, Vector3? origin = null)
    {
        if (!settings.WeaponHandling.Value || CombatWorld.Networked)
            return false;
        var takes = new HandlingTake[cues.Length];
        for (int i = 0; i < cues.Length; i++)
        {
            takes[i] = SelectHandling(cues[i]);
            if (takes[i].File == null)
                return false;
        }
        if (!GetHandlingOutput())
            return false;

        float volume = RedlineSettings.Factor(settings.HandlingVolume);
        var channels = new Channel[cues.Length];
        var core = RuntimeManager.CoreSystem;
        bool played = false;
        try
        {
            if (!ReadSampleRate())
                return false;
            for (int i = 0; i < cues.Length; i++)
            {
                if (!PrepareHandling(takes[i], volume * cues[i].Gain, out channels[i]))
                    return false;
            }
            if (channels[0].getDSPClock(out _, out ulong clock) != RESULT.OK)
                return false;
            for (int i = 0; i < channels.Length; i++)
                if (!ScheduleHandling(channels[i], clock, delay + cues[i].Offset, takes[i].DurationMs))
                    return false;

            // Start the immediate cue last, so a scheduling failure can still fall back cleanly.
            for (int i = channels.Length - 1; i >= 0; i--)
                if (channels[i].setPaused(false) != RESULT.OK)
                    return false;
            played = true;
            for (int i = 0; i < cues.Length; i++)
            {
                if (cues[i].Alternate.File == null)
                    continue;
                if (takes[i].File == cues[i].Take.File)
                    nextAlternate.Add(cues[i].Take.File);
                else
                    nextAlternate.Remove(cues[i].Take.File);
            }
            PlayHandlingRoom(cues, takes, volume, delay, origin);
            return true;
        }
        finally
        {
            if (!played)
                foreach (var channel in channels)
                    if (channel.hasHandle())
                        channel.stop();
        }
    }

    private bool ReadSampleRate() =>
        sampleRate > 0 || RuntimeManager.CoreSystem.getSoftwareFormat(out sampleRate, out _, out _) == RESULT.OK && sampleRate > 0;

    private void PlayHandlingRoom(HandlingCue[] cues, HandlingTake[] takes, float volume, float delay, Vector3? origin)
    {
        float wet = volume * 0.74f * RedlineSettings.Factor(settings.GunshotRoom);
        if (roomFailed || wet <= 0 || !PlayerState.currentRoom)
            return;
        try
        {
            if (!origin.HasValue)
            {
                var player = PlayerAttack.instance;
                if (!player)
                    return;
                var muzzle = PlayerAttack.muzzle;
                origin = muzzle ? muzzle.position : player.transform.position;
            }
            for (int i = 0; i < cues.Length; i++)
            {
                var take = takes[i];
                var cue = new RoomCue(handling[take.File], 1f, wet * cues[i].Gain, delay + cues[i].Offset,
                    take.StartMs, take.DurationMs);
                PlayRoom(cue, origin.Value, true);
            }
        }
        catch (Exception error) { RoomFailure(error); }
    }

    private void PlayRoom(RoomCue cue, Vector3 origin, bool handlingCue)
    {
        if (roomFailed || cue.Volume <= 0 || !PlayerState.currentRoom)
            return;
        try
        {
            PrepareRoomOutput();
            if (mixGroup.hasHandle())
                acoustics.Play(cue, mixGroup, origin, handlingCue, log);
        }
        catch (Exception error) { RoomFailure(error); }
    }

    private void RoomFailure(Exception error)
    {
        roomFailed = true;
        log($"weapon room effects failed: {error}");
    }

    private bool PrepareHandling(HandlingTake take, float volume, out Channel channel)
    {
        if (RuntimeManager.CoreSystem.playSound(handling[take.File], handlingGroup, true, out channel) != RESULT.OK)
            return false;
        if (channel.setVolume(volume) == RESULT.OK &&
            (take.StartMs == 0 || channel.setPosition(take.StartMs, TIMEUNIT.MS) == RESULT.OK))
            return true;
        channel.stop();
        channel = default;
        return false;
    }

    private bool ScheduleHandling(Channel channel, ulong clock, float offset, uint milliseconds)
    {
        ulong scheduled = clock + (ulong)(sampleRate * offset);
        if (milliseconds == 0)
            return offset <= 0 || channel.setDelay(scheduled, 0, true) == RESULT.OK;
        ulong end = scheduled + (ulong)(sampleRate * milliseconds / 1000d);
        return channel.addFadePoint(end - (ulong)(sampleRate * 0.012f), 1f) == RESULT.OK &&
            channel.addFadePoint(end, 0f) == RESULT.OK &&
            channel.setDelay(offset > 0 ? scheduled : 0, end, true) == RESULT.OK;
    }

    private bool LoadHandling(string name)
    {
        if (handling.TryGetValue(name, out var sound))
            return sound.hasHandle();
        string path = Path.GetFullPath(Path.Combine("UserData", "RedlineAudio", name + ".wav"));
        if (File.Exists(path))
        {
            var result = RuntimeManager.CoreSystem.createSound(path, MODE._2D | MODE.CREATESAMPLE, out sound);
            if (result != RESULT.OK)
            {
                log($"weapon handling load failed file={name} error={result}");
                sound = default;
            }
        }
        handling.Add(name, sound);
        return sound.hasHandle();
    }

    private HandlingTake SelectHandling(HandlingCue cue)
    {
        if (cue.Alternate.File != null && nextAlternate.Contains(cue.Take.File) && LoadHandling(cue.Alternate.File))
            return cue.Alternate;
        if (LoadHandling(cue.Take.File))
            return cue.Take;
        return cue.Alternate.File != null && LoadHandling(cue.Alternate.File) ? cue.Alternate : default;
    }

    private bool GetHandlingOutput()
    {
        if (handlingGroup.hasHandle())
            return true;
        if (!GetOutput())
            return false;
        PrepareRoomOutput();
        if (RuntimeManager.CoreSystem.createChannelGroup("REDLINE weapon handling", out handlingGroup) != RESULT.OK)
            return false;
        var parent = mixGroup.hasHandle() ? mixGroup : group;
        if (parent.addGroup(handlingGroup) == RESULT.OK)
            return true;
        handlingGroup.release();
        handlingGroup = default;
        return false;
    }

    private float Pitch() => 0.985f + 0.03f * (float)random.NextDouble();

    public void ClearRoom()
    {
        acoustics.Clear();
        roomFailed = false;
        firedRackAt = float.NegativeInfinity;
        handledPumpAt = float.NegativeInfinity;
        handledShellAt = float.NegativeInfinity;
        nextSmgReloadMarker = -1;
        smgReloadCoveredAt = float.NegativeInfinity;
        if (handlingGroup.hasHandle())
            handlingGroup.stop();
    }

    private Samples Load(Items.itemlist item)
    {
        if (banks.TryGetValue(item, out var samples))
            return samples;
        samples = new Samples();
        banks.Add(item, samples);
        string prefix = Array.Find(recordings, recording => recording.Item == item).Filename;
        if (prefix == null)
            return samples;
        if (item == Items.itemlist.Shotgun)
        {
            LoadSamples(samples, "shotgun-raw", 4);
            samples.Raw = samples.Count > 0;
        }
        if (!samples.Raw)
            LoadSamples(samples, prefix, 3);
        hasSamples |= samples.Count > 0;
        log($"gunshot bank weapon={item} takes={samples.Count}{(samples.Raw ? " raw=True" : "")}");
        return samples;
    }

    private void LoadSamples(Samples samples, string prefix, int count)
    {
        for (int i = 1; i <= count; i++)
        {
            string path = Path.GetFullPath(Path.Combine("UserData", "RedlineAudio", $"{prefix}-{i}.wav"));
            if (!File.Exists(path))
                continue;
            var result = RuntimeManager.CoreSystem.createSound(path, MODE._2D | MODE.CREATESAMPLE, out var sound);
            if (result == RESULT.OK)
                samples.Files.Add(sound);
            else
                log($"gunshot load failed file={Path.GetFileName(path)} error={result}");
        }
    }

    private bool GetOutput()
    {
        if (group.hasHandle())
            return true;
        if (!locked)
        {
            bus = RuntimeManager.GetBus("bus:/SFX/Combat/Gunshots");
            if (bus.lockChannelGroup() != RESULT.OK)
                return false;
            locked = true;
        }
        return bus.getChannelGroup(out group) == RESULT.OK && group.hasHandle();
    }

    // Keep coincident impact and gunshot peaks under the same limiter.
    public ChannelGroup GetGoreOutput() => GetOutput() ? GetMixOutput() : default;

    private void PrepareRoomOutput()
    {
        if (mixGroup.hasHandle())
            return;
        var core = RuntimeManager.CoreSystem;
        if (core.createChannelGroup("REDLINE weapon audio", out mixGroup) != RESULT.OK)
            return;
        if (GetMixOutput().addGroup(mixGroup) != RESULT.OK)
        {
            mixGroup.release();
            mixGroup = default;
            return;
        }
        if (!AddLimiter(mixGroup, -6.8f, 55f, 1f, out limiter))
            log("weapon limiter unavailable");
    }

    private ChannelGroup GetMixOutput()
    {
        if (outputPrepared)
            return outputGroup.hasHandle() ? outputGroup : group;
        outputPrepared = true;
        var core = RuntimeManager.CoreSystem;
        // Catch the combined shotgun and room signal before the native bus applies its gain.
        if (core.createChannelGroup("REDLINE weapon output", out var output) == RESULT.OK &&
            AddLimiter(output, -6.5f, 8f, 0f, out outputLimiter) && group.addGroup(output) == RESULT.OK)
        {
            outputGroup = output;
            return outputGroup;
        }
        ReleaseOutput(ref output, ref outputLimiter);
        log("weapon output limiter unavailable");
        return group;
    }

    private static bool AddLimiter(ChannelGroup output, float ceiling, float release, float gain, out DSP effect)
    {
        if (RuntimeManager.CoreSystem.createDSPByType(DSP_TYPE.LIMITER, out effect) == RESULT.OK &&
            effect.setParameterFloat((int)DSP_LIMITER.CEILING, ceiling) == RESULT.OK &&
            effect.setParameterFloat((int)DSP_LIMITER.RELEASETIME, release) == RESULT.OK &&
            effect.setParameterFloat((int)DSP_LIMITER.MAXIMIZERGAIN, gain) == RESULT.OK &&
            output.addDSP(0, effect) == RESULT.OK)
            return true;
        if (effect.hasHandle()) effect.release();
        effect = default;
        return false;
    }

    private ChannelGroup GetReportOutput(bool shotgun = false, bool raw = false)
    {
        var existing = shotgun ? shotgunReportGroup : reportGroup;
        if (existing.hasHandle())
            return existing;
        PrepareRoomOutput();
        var parent = mixGroup.hasHandle() ? mixGroup : group;
        if (raw && !mixGroup.hasHandle())
            return parent;
        // The separate dry path keeps the slower room limiter from holding down the opening blast.
        shotgun &= mixGroup.hasHandle() && (raw || outputGroup.hasHandle());
        ref var reports = ref (shotgun ? ref shotgunReportGroup : ref reportGroup);
        ref var eq = ref (shotgun ? ref shotgunReportEq : ref reportEq);
        if (reports.hasHandle())
            return reports;
        var destination = shotgun && !raw ? outputGroup : parent;
        var core = RuntimeManager.CoreSystem;
        if (core.createChannelGroup(shotgun ? "REDLINE shotgun report" : "REDLINE gunshot reports", out var output) != RESULT.OK)
            return parent;
        if (destination.addGroup(output) != RESULT.OK)
        {
            output.release();
            return parent;
        }
        reports = output;
        if (raw)
        {
            if (!AddReportEq(output, 105f, 1.45f, 1.55f, out eq))
                log("raw shotgun bass EQ unavailable");
            return output;
        }
        if (!AddReportEq(output, shotgun ? 100f : 155f, shotgun ? 2.6f : 1.45f, shotgun ? 5.6f : 2.4f, out eq))
            log("gunshot EQ unavailable");
        if (shotgun && !AddReportEq(output, 585f, 1.8f, 2.35f, out shotgunMidEq))
            log("shotgun midrange EQ unavailable");
        return output;
    }

    private static bool AddReportEq(ChannelGroup output, float center, float width, float gain, out DSP effect)
    {
        if (RuntimeManager.CoreSystem.createDSPByType(DSP_TYPE.PARAMEQ, out effect) == RESULT.OK &&
            effect.setParameterFloat((int)DSP_PARAMEQ.CENTER, center) == RESULT.OK &&
            effect.setParameterFloat((int)DSP_PARAMEQ.BANDWIDTH, width) == RESULT.OK &&
            effect.setParameterFloat((int)DSP_PARAMEQ.GAIN, gain) == RESULT.OK &&
            output.addDSP(0, effect) == RESULT.OK)
            return true;
        if (effect.hasHandle()) effect.release();
        effect = default;
        return false;
    }

    private static void ReleaseEffect(ChannelGroup output, ref DSP effect)
    {
        if (!effect.hasHandle())
            return;
        output.removeDSP(effect);
        effect.release();
        effect = default;
    }

    private static void ReleaseOutput(ref ChannelGroup output, ref DSP effect)
    {
        if (!output.hasHandle())
            return;
        output.stop();
        ReleaseEffect(output, ref effect);
        output.release();
        output = default;
    }

    public void Dispose()
    {
        acoustics.Dispose();
        warmHandling?.Dispose();
        ReleaseEffect(shotgunReportGroup, ref shotgunMidEq);
        ReleaseOutput(ref shotgunReportGroup, ref shotgunReportEq);
        ReleaseOutput(ref reportGroup, ref reportEq);
        if (handlingGroup.hasHandle())
        {
            handlingGroup.stop();
            handlingGroup.release();
            handlingGroup = default;
        }
        ReleaseOutput(ref mixGroup, ref limiter);
        ReleaseOutput(ref outputGroup, ref outputLimiter);
        foreach (var sample in handling.Values)
            if (sample.hasHandle())
                sample.release();
        handling.Clear();
        nextAlternate.Clear();
        foreach (var samples in banks.Values)
            foreach (var sound in samples.Files)
                sound.release();
        banks.Clear();
        if (locked)
            bus.unlockChannelGroup();
        locked = false;
        group = default;
    }

    private sealed class Samples
    {
        public readonly List<Sound> Files = new(3);
        public int Count => Files.Count;
        public int Last = -1;
        public bool Raw;
    }

    private readonly record struct HandlingTake(string File, uint StartMs, uint DurationMs);
    private readonly record struct HandlingCue(HandlingTake Take, float Offset = 0f, float Gain = 0.37f,
        HandlingTake Alternate = default);
}
