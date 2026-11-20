using System;
using FMOD;
using FMODUnity;
using UnityEngine;

namespace Redline;

internal sealed class GunshotAcoustics
{
    private const float ProbeRange = 30f;
    private const float Diagonal = 0.70710678f;
    private static readonly Vector3[] directions =
    {
        Vector3.right, new(Diagonal, Diagonal, 0), Vector3.up, new(-Diagonal, Diagonal, 0),
        Vector3.left, new(-Diagonal, -Diagonal, 0), Vector3.down, new(Diagonal, -Diagonal, 0)
    };

    private readonly FilteredReturn early = new("REDLINE weapon reflections");
    private readonly FilteredReturn pressure = new("REDLINE gunshot low end");
    private readonly FilteredReturn[] late = { new("REDLINE small room"), new("REDLINE medium room"), new("REDLINE large room") };
    private int sampleRate;
    private Room sampledRoom;
    private Vector3 sampledOrigin;
    private Vector3 sampledRight;
    private float sampleUntil;
    private ShotRoomResponse response;

    public void PlayPressure(RoomCue cue, ChannelGroup output, Action<string> log)
    {
        if (cue.Volume <= 0 || !output.hasHandle())
            return;
        var core = RuntimeManager.CoreSystem;
        if (ReadClockRate(core) && pressure.Ready(core, output, log, cutoff: 340))
            Send(core, pressure.Group, cue with { Pitch = cue.Pitch * 0.71f, DurationMs = 115 }, 1f, 0f, 0.027f, log);
    }

    public void Play(RoomCue cue, ChannelGroup output, Vector3 origin, bool handling, Action<string> log)
    {
        if (cue.Volume <= 0 || !PlayerState.currentRoom)
            return;
        var core = RuntimeManager.CoreSystem;
        if (!ReadClockRate(core))
            return;

        var camera = PlayerState.cameraActive;
        if (!camera) camera = Camera.main;
        Vector3 screenRight = camera ? Battlefield.OnPlane(camera.transform.right).normalized : Vector3.right;
        var current = PlayerState.currentRoom;
        if (sampledRoom != current || Time.unscaledTime >= sampleUntil ||
            (origin - sampledOrigin).sqrMagnitude >= 1.44f || Vector3.Dot(screenRight, sampledRight) < 0.98f)
        {
            sampledRoom = current;
            sampledOrigin = origin;
            sampledRight = screenRight;
            sampleUntil = Time.unscaledTime + 0.18f;
            response = default;
            foreach (var direction in directions)
                if (Physics.Raycast(origin, direction, out var hit, ProbeRange, Battlefield.RoomSolids,
                        QueryTriggerInteraction.Ignore))
                    response.AddWall(hit.distance, Vector3.Dot(direction, screenRight));
        }
        var room = response;
        if (room.Walls == 0)
            return;

        int returned = 0;
        if (early.Ready(core, output, log, cutoff: 4900))
        {
            var reflection = handling ? cue : LimitReport(cue, 120);
            if (handling)
            {
                // Use one early return so that separate handling clicks stay distinct.
                var nearest = room.Left.Distance > 0 && (room.Right.Distance == 0 || room.Left.Distance <= room.Right.Distance)
                    ? room.Left : room.Right;
                if (PlayReflection(core, early.Group, reflection, nearest, log)) returned++;
            }
            else
            {
                if (PlayReflection(core, early.Group, reflection, room.Left, log)) returned++;
                if (PlayReflection(core, early.Group, reflection, room.Right, log)) returned++;
                if (room.Walls >= 3)
                {
                    float pan = room.Farthest.Pan;
                    if (Send(core, early.Group, LimitReport(cue, 130), room.EchoGain,
                            -pan * 0.69f, room.EchoDelay, log)) returned++;
                    if (Send(core, early.Group, LimitReport(cue with { Pitch = cue.Pitch * 0.94f }, 105), room.SecondBounceGain,
                            pan * 0.55f, room.SecondBounceDelay, log)) returned++;
                }
            }
        }

        int profile = room.ReverbProfile;
        bool wet = false;
        if (profile >= 0)
        {
            var reverb = late[profile];
            if (reverb.Ready(core, output, log, reverbProfile: profile))
                wet = Send(core, reverb.Group, handling ? cue : LimitReport(cue, 290),
                    handling ? room.HandlingReverbGain : room.ReverbGain, 0f, 0f, log);
        }
        log($"{(handling ? "handling" : "gunshot")} room={current.roomName} walls={room.Walls} mean={room.MeanDistance:F1} " +
            $"leftMs={room.Left.Delay * 1000:F0} rightMs={room.Right.Delay * 1000:F0} " +
            $"echoMs={(handling ? 0 : room.EchoDelay * 1000):F0} secondMs={(handling ? 0 : room.SecondBounceDelay * 1000):F0} " +
            $"taps={returned} reverb={(wet ? profile : -1)} level={cue.Volume:F2}");
    }

    private static RoomCue LimitReport(RoomCue cue, uint limit) =>
        cue with { DurationMs = cue.DurationMs == 0 ? limit : Math.Min(cue.DurationMs, limit) };

    private bool ReadClockRate(FMOD.System core) =>
        sampleRate > 0 || core.getSoftwareFormat(out sampleRate, out _, out _) == RESULT.OK && sampleRate > 0;

    private bool PlayReflection(FMOD.System core, ChannelGroup group, RoomCue cue, ShotReflection reflection,
        Action<string> log) => reflection.Gain > 0 &&
        Send(core, group, cue, reflection.Gain, reflection.Pan, reflection.Delay, log);

    private bool Send(FMOD.System core, ChannelGroup group, RoomCue cue, float gain, float pan,
        float reflectionDelay, Action<string> log)
    {
        float delay = cue.Offset + reflectionDelay;
        var result = core.playSound(cue.Sample, group, true, out var channel);
        if (result == RESULT.OK && (result = channel.getDSPClock(out _, out ulong clock)) == RESULT.OK)
        {
            ulong begin = clock + (ulong)(delay * sampleRate);
            ulong end = cue.DurationMs == 0 ? 0 : begin + (ulong)(cue.DurationMs * (double)sampleRate / 1000d);
            ulong fade = (ulong)(Math.Min(32d, cue.DurationMs * 0.42) * sampleRate / 1000d);
            if ((result = channel.setVolume(cue.Volume * gain)) == RESULT.OK &&
                (result = channel.setPitch(cue.Pitch)) == RESULT.OK &&
                (cue.StartMs == 0 || (result = channel.setPosition(cue.StartMs, TIMEUNIT.MS)) == RESULT.OK) &&
                (result = channel.setPan(pan)) == RESULT.OK &&
                (result = channel.setPriority(140)) == RESULT.OK &&
                (end == 0 || ((result = channel.addFadePoint(end - fade, 1f)) == RESULT.OK &&
                    (result = channel.addFadePoint(end, 0f)) == RESULT.OK)) &&
                (result = channel.setDelay(delay > 0 ? begin : 0, end, true)) == RESULT.OK &&
                (result = channel.setPaused(false)) == RESULT.OK)
                return true;
        }
        if (channel.hasHandle())
            channel.stop();
        log($"weapon room return failed delay={delay:F3} error={result}");
        return false;
    }

    public void Clear()
    {
        sampledRoom = null;
        response = default;
        early.Clear();
        pressure.Clear();
        foreach (var reverb in late)
            reverb.Clear();
    }

    public void Dispose()
    {
        early.Dispose();
        pressure.Dispose();
        foreach (var reverb in late)
            reverb.Dispose();
    }

    private sealed class FilteredReturn(string name)
    {
        public ChannelGroup Group;
        private DSP effect;
        private bool attempted;

        public bool Ready(FMOD.System core, ChannelGroup parent, Action<string> log, int cutoff = 0, int reverbProfile = -1)
        {
            if (attempted)
                return Group.hasHandle();
            attempted = true;
            var type = cutoff > 0 ? DSP_TYPE.LOWPASS_SIMPLE : DSP_TYPE.SFXREVERB;
            if (core.createChannelGroup(name, out Group) != RESULT.OK ||
                parent.addGroup(Group) != RESULT.OK ||
                core.createDSPByType(type, out effect) != RESULT.OK ||
                (cutoff > 0 ? effect.setParameterFloat((int)DSP_LOWPASS_SIMPLE.CUTOFF, cutoff) != RESULT.OK
                    : !ConfigureReverb(reverbProfile)) ||
                Group.addDSP(0, effect) != RESULT.OK)
            {
                Dispose();
                log($"weapon room return unavailable cutoff={cutoff} reverb={reverbProfile}");
                return false;
            }
            return true;
        }

        private bool ConfigureReverb(int profile)
        {
            // Separate returns let the previous room's tail decay with its original settings.
            float decay = profile == 0 ? 820f : profile == 1 ? 1330f : 1840f;
            return Parameter(DSP_SFXREVERB.DECAYTIME, decay) &&
                Parameter(DSP_SFXREVERB.EARLYDELAY, 14f + profile * 14f) &&
                Parameter(DSP_SFXREVERB.LATEDELAY, 15f + profile * 8f) &&
                Parameter(DSP_SFXREVERB.LOWSHELFFREQUENCY, 240f) &&
                Parameter(DSP_SFXREVERB.LOWSHELFGAIN, -3.5f) &&
                Parameter(DSP_SFXREVERB.HIGHCUT, 4900f - profile * 550f) &&
                Parameter(DSP_SFXREVERB.EARLYLATEMIX, 70f) &&
                Parameter(DSP_SFXREVERB.WETLEVEL, -0.3f) &&
                Parameter(DSP_SFXREVERB.DRYLEVEL, -80f);
        }

        private bool Parameter(DSP_SFXREVERB parameter, float value) =>
            effect.setParameterFloat((int)parameter, value) == RESULT.OK;

        public void Clear()
        {
            if (Group.hasHandle())
                Group.stop();
            if (effect.hasHandle())
                effect.reset();
            if (!Group.hasHandle())
                attempted = false;
        }

        public void Dispose()
        {
            if (Group.hasHandle())
            {
                Group.stop();
                if (effect.hasHandle())
                    Group.removeDSP(effect);
                Group.release();
                Group = default;
            }
            if (effect.hasHandle())
            {
                effect.release();
                effect = default;
            }
        }
    }
}

internal readonly record struct RoomCue(Sound Sample, float Pitch, float Volume, float Offset = 0, uint StartMs = 0,
    uint DurationMs = 0);

internal readonly record struct ShotReflection(float Distance, float Pan)
{
    public float Delay => Distance <= 0 ? 0 : Mathf.Clamp(0.019f + 0.0026f * Distance, 0.022f, 0.093f);
    public float Gain => Distance <= 0 ? 0 : 0.32f / (1f + 0.035f * Distance);
}

internal struct ShotRoomResponse
{
    private float distanceSum;
    public int Walls { get; private set; }
    public ShotReflection Left { get; private set; }
    public ShotReflection Right { get; private set; }
    public ShotReflection Farthest { get; private set; }

    public float MeanDistance => Walls == 0 ? 0 : distanceSum / Walls;
    public float EchoDelay => Walls < 3 ? 0 : 0.070f + 0.0032f * Farthest.Distance;
    public float EchoGain => 0.43f / (1f + 0.017f * Farthest.Distance);
    public float SecondBounceDelay => Walls < 3 ? 0 : EchoDelay + 0.068f + 0.0014f * MeanDistance;
    public float SecondBounceGain => 0.28f / (1f + 0.023f * Farthest.Distance);
    public float ReverbGain => 0.53f + 0.066f * Walls;
    public float HandlingReverbGain => 0.35f + 0.053f * Walls;
    public int ReverbProfile => Walls < 3 ? -1 : MeanDistance < 6.5f ? 0 : MeanDistance < 13f ? 1 : 2;

    public void AddWall(float distance, float horizontal)
    {
        distance = Mathf.Max(0.1f, distance);
        distanceSum += distance;
        Walls++;
        if (distance > Farthest.Distance)
            Farthest = new ShotReflection(distance, Mathf.Clamp(horizontal, -0.78f, 0.78f));
        if (horizontal < -0.15f && (Left.Distance <= 0 || distance < Left.Distance))
            Left = new ShotReflection(distance, Mathf.Max(-0.78f, horizontal));
        if (horizontal > 0.15f && (Right.Distance <= 0 || distance < Right.Distance))
            Right = new ShotReflection(distance, Mathf.Min(0.78f, horizontal));
    }
}
