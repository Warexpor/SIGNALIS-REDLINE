using System;
using System.Collections.Generic;
using System.IO;
using FMOD;
using FMODUnity;
using UnityEngine;

namespace Redline;

internal sealed class CutSounds(RedlineSettings settings, Func<ChannelGroup> output, Action<string> log)
{
    private const int Impact = 0;
    private const int Sever = 1;
    private const int HeavyCut = 2;
    private const int Bleed = 3;
    private const int StompImpact = 4;
    private const int Join = 5;
    private static readonly string[] foleyNames = { "impact", "sever", "stomp", "bleed", "stomp-impact", "join" };
    private readonly List<Sound>[] foley = { new(2), new(2), new(2), new(2), new(3), new(2) };
    private readonly int[] lastTake = { -1, -1, -1, -1, -1, -1 };
    private readonly bool[] scanned = new bool[foleyNames.Length];
    private readonly System.Random variation = new();
    private int warmIndex;
    private float lastCut = float.NegativeInfinity;
    private float lastImpact = float.NegativeInfinity;
    private float lastBleed = float.NegativeInfinity;

    public void Play(Vector3 point, bool stomp)
    {
        if (stomp)
        {
            if (!PlayCut(StompImpact, point, 0.68f, 0.96f))
                PlayCut(HeavyCut, point, 0.68f, 0.94f);
        }
        else if (Time.time - lastCut >= 0.10f)
            PlayCut(Sever, point, 0.65f, 0.95f);
    }

    public void PlayReassembly(Vector3 point)
    {
        if (Time.time - lastCut >= 0.10f && !PlayCut(Join, point, 0.36f, 1f))
            PlayCut(Sever, point, 0.36f, 1.07f);
    }

    public void Warmup()
    {
        if (warmIndex < foley.Length && RedlineSettings.Factor(settings.CutVolume) > 0)
            LoadFoley(warmIndex++);
    }

    public void PlayImpact(Vector3 point, bool heavy)
    {
        float gain = RedlineSettings.Factor(settings.CutVolume);
        if (gain <= 0 || Time.time - lastImpact < 0.10f)
            return;
        if (PlayFoley(Impact, point, (heavy ? 0.42f : 0.32f) * gain, 1f))
            lastImpact = Time.time;
    }

    public bool PlayBleed(Vector3 point, float pressure)
    {
        float gain = RedlineSettings.Factor(settings.CutVolume);
        if (gain <= 0 || pressure < 0.28f || Time.time - lastBleed < 0.90f
            || Time.time - lastImpact < 0.21f || Time.time - lastCut < 0.23f)
            return false;
        if (!PlayFoley(Bleed, point, Mathf.Lerp(0.08f, 0.16f, pressure) * gain, 1f))
            return false;
        lastBleed = Time.time;
        return true;
    }

    public void PlayHeavyCut(Vector3 point) => PlayCut(HeavyCut, point, 0.78f, 0.89f);

    private bool PlayCut(int cue, Vector3 point, float volume, float pitch)
    {
        float gain = RedlineSettings.Factor(settings.CutVolume);
        if (gain <= 0 || !PlayFoley(cue, point, volume * gain, pitch))
            return false;
        lastCut = Time.time;
        return true;
    }

    private void LoadFoley(int kind)
    {
        if (scanned[kind])
            return;
        scanned[kind] = true;
        int takeCount = kind == StompImpact ? 3 : 2;
        for (int take = 1; take <= takeCount; take++)
        {
            string filename = $"{foleyNames[kind]}-{take}.wav";
            string path = Path.GetFullPath(Path.Combine("UserData", "RedlineAudio", "Gore", filename));
            if (!File.Exists(path))
                continue;
            var result = RuntimeManager.CoreSystem.createSound(path, MODE._2D | MODE.CREATESAMPLE, out var sound);
            if (result == RESULT.OK)
                foley[kind].Add(sound);
            else
                log($"gore audio load failed file={filename} error={result}");
        }
        if (foley[kind].Count > 0)
            log($"gore audio bank cue={foleyNames[kind]} takes={foley[kind].Count}");
    }

    private bool PlayFoley(int kind, Vector3 point, float volume, float pitch)
    {
        try
        {
            LoadFoley(kind);
            var takes = foley[kind];
            if (takes.Count == 0)
                return false;
            var foleyOutput = output();
            if (!foleyOutput.hasHandle())
                return false;
            var listener = PlayerAttack.instance;
            var offset = listener ? point - listener.transform.position : Vector3.zero;
            float distance = offset.magnitude;
            var camera = PlayerState.cameraActive;
            var right = camera ? Battlefield.OnPlane(camera.transform.right).normalized : Vector3.right;
            float pan = Mathf.Clamp(Vector3.Dot(offset, right) / 18f, -0.76f, 0.76f);
            volume = Mathf.Min(volume * (1f - 0.83f * Mathf.InverseLerp(6f, 35f, distance)), 0.86f);
            int index = lastTake[kind] < 0 ? variation.Next(takes.Count) : (lastTake[kind] + 1) % takes.Count;
            var result = RuntimeManager.CoreSystem.playSound(takes[index], foleyOutput, true, out var voice);
            if (result != RESULT.OK)
                return false;
            float spread = kind == StompImpact ? 0.08f : 0.05f;
            float playbackPitch = pitch * (1f + spread * (2f * (float)variation.NextDouble() - 1f));
            if (voice.setVolume(volume) != RESULT.OK || voice.setPan(pan) != RESULT.OK ||
                voice.setPitch(playbackPitch) != RESULT.OK ||
                voice.setPaused(false) != RESULT.OK)
            {
                voice.stop();
                return false;
            }
            lastTake[kind] = index;
            log($"gore sound cue={foleyNames[kind]} take={index + 1} pitch={playbackPitch:F3} distance={distance:F1} pan={pan:F2}");
            return true;
        }
        catch (Exception error)
        {
            log($"gore sound error: {error}");
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var takes in foley)
        {
            foreach (var sound in takes)
                if (sound.hasHandle())
                    sound.release();
            takes.Clear();
        }
    }
}
