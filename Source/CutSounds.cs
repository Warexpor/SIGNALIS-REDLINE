using System;
using System.Collections.Generic;
using System.IO;
using FMOD;
using FMODUnity;
using UnityEngine;

namespace Redline;

internal sealed class CutSounds(RedlineSettings settings, Func<ChannelGroup> output, Action<string> log)
{
    private const string Bank = "DET";
    private const string Chop = "event:/Enemy/EULR/ChopMeat";
    private static readonly string[] foleyNames = { "impact", "sever", "stomp" };
    private readonly List<Sound>[] foley = { new(2), new(2), new(2) };
    private readonly int[] lastTake = { -1, -1, -1 };
    private readonly bool[] scanned = new bool[3];
    private readonly System.Random variation = new();
    private int warmIndex;
    private bool attempted;
    private bool loaded;
    private float lastCut = float.NegativeInfinity;
    private float lastImpact = float.NegativeInfinity;
    private int previousPitch = -1;

    public void Play(Vector3 point, bool stomp) => Play(point, stomp, false);

    public void PlayReassembly(Vector3 point) => Play(point, false, true);

    public void Warmup()
    {
        if (warmIndex < foley.Length && RedlineSettings.Factor(settings.CutVolume) > 0)
            LoadFoley(warmIndex++);
    }

    public void PlayImpact(Vector3 point, bool heavy)
    {
        float gain = RedlineSettings.Factor(settings.CutVolume);
        if (gain <= 0 || Time.time - lastImpact < 0.115f || Time.time - lastCut < 0.075f)
            return;
        if (PlayFoley(0, point, (heavy ? 0.44f : 0.32f) * gain, 1f))
            lastImpact = Time.time;
    }

    public void PlayHeavyCut(Vector3 point)
    {
        float gain = RedlineSettings.Factor(settings.CutVolume);
        if (gain <= 0)
            return;
        if (PlayFoley(2, point, 0.78f * gain, 0.89f))
            lastCut = Time.time;
        else
            Play(point, stomp: false);
    }

    private void Play(Vector3 point, bool stomp, bool joining)
    {
        float gain = RedlineSettings.Factor(settings.CutVolume);
        if (gain <= 0 || !stomp && Time.time - lastCut < 0.10f)
            return;
        if (!joining && PlayFoley(stomp ? 2 : 1, point, (stomp ? 0.68f : 0.78f) * gain, stomp ? 0.94f : 1f))
        {
            lastCut = Time.time;
            return;
        }
        try
        {
            var listener = PlayerAttack.instance;
            float distance = listener ? Vector3.Distance(listener.transform.position, point) : 0;
            float volume = joining
                ? 0.72f * (1f - Mathf.InverseLerp(8f, 26f, distance)) * gain
                : (stomp ? 2.65f : 2.25f) * (1f - 0.68f * Mathf.InverseLerp(9f, 45f, distance)) * gain;
            if (volume <= 0)
                return;
            lastCut = Time.time;
            if (!attempted)
            {
                attempted = true;
                // Keep our own bank reference so the cut remains available after room changes.
                RuntimeManager.LoadBank(Bank);
                loaded = RuntimeManager.HasBankLoaded(Bank);
            }
            if (!loaded)
                return;
            // Reuse the stock event at three nearby pitches and avoid consecutive repeats.
            int pitchIndex = previousPitch < 0 ? variation.Next(3) : (previousPitch + 1 + variation.Next(2)) % 3;
            previousPitch = pitchIndex;
            float pitch = (joining ? 1.02f : stomp ? 0.82f : 0.88f) + pitchIndex * 0.055f;
            var sound = RuntimeManager.CreateInstance(Chop);
            try
            {
                RESULT gainResult = sound.setVolume(volume);
                sound.setPitch(pitch);
                RESULT result = sound.start();
                log($"{(joining ? "reassembly sound" : stomp ? "stomp crunch" : "cut sound")} event={Chop} volume={volume:F2} pitch={pitch:F2} distance={distance:F1} gain={gainResult} result={result}");
            }
            finally { sound.release(); }
        }
        catch (Exception error) { log($"cut sound error: {error}"); }
    }

    private void LoadFoley(int kind)
    {
        if (scanned[kind])
            return;
        scanned[kind] = true;
        for (int take = 1; take <= 2; take++)
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
            if (voice.setVolume(volume) != RESULT.OK || voice.setPan(pan) != RESULT.OK ||
                voice.setPitch(pitch * (0.95f + 0.10f * (float)variation.NextDouble())) != RESULT.OK ||
                voice.setPaused(false) != RESULT.OK)
            {
                voice.stop();
                return false;
            }
            lastTake[kind] = index;
            log($"gore sound cue={foleyNames[kind]} take={index + 1} distance={distance:F1} pan={pan:F2}");
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
        if (!loaded)
            return;
        try { RuntimeManager.UnloadBank(Bank); }
        catch (Exception error) { log($"cut sound bank release: {error}"); }
        loaded = false;
    }
}
