using System;
using System.IO;
using UnhollowerRuntimeLib;
using UnityEngine;
using UnityEngine.UI;

namespace Redline;

public sealed partial class RedlineMod
{
    private static readonly string[] ArianeFrameNames =
    {
        "PEN_Nach_33", "END_Ariane_Falke", "MEM_Promise_3",
        "END_Death_11", "END_Circle_4", "ROT_Tooth_2"
    };
    private ArianeFlash arianeFlash;
    private bool ArianeFlashSuppressed => !settings.ArianeFlash.Value || panel.CapturesInput;

    private void InstallArianeFlash()
    {
        try
        {
            EnsureArianeCache();
            string directory = Path.Combine("UserData", "RedlineAriane");
            string[] files = Array.FindAll(Array.ConvertAll(ArianeFrameNames,
                name => Path.Combine(directory, name + ".png")), File.Exists);
            if (files.Length == 0)
            {
                string legacy = Path.Combine("UserData", "RedlineAriane.png");
                if (!File.Exists(legacy))
                    return;
                files = new[] { legacy };
            }
            arianeFlash = new ArianeFlash(files, Log);
            Log($"ariane flash pool frames={files.Length}");
            Hook(typeof(DamageCameraEffects), nameof(DamageCameraEffects.Update), null, nameof(DamageCameraUpdated));
        }
        catch (Exception error) { WarnOnce("Ariane flash image", error); }
    }

    private static void DamageCameraUpdated(DamageCameraEffects __instance)
    {
        try { active.arianeFlash?.Observe(__instance, active.ArianeFlashSuppressed); }
        catch (Exception error) { active.WarnOnce("Ariane flash", error); }
    }

    private void UpdateArianePreview()
    {
        if (!Input.GetKeyDown(KeyCode.F1) || !settings.ArianeFlash.Value)
            return;

        bool panelOpen = panel?.CapturesInput == true;
        Log($"ariane F1 received image={arianeFlash != null} panel={panelOpen}");
        if (!panelOpen)
            arianeFlash?.Preview();
    }
}

internal sealed class ArianeFlash : IDisposable
{
    private const float Duration = 0.12f;
    private const double HitChance = 0.80;
    private const float CrestWait = 0.85f;
    private const float PreviewDuration = 0.32f;
    private readonly Frame[] frames;
    private readonly Action<string> log;
    private readonly System.Random random = new();
    private int nextFrame;
    private Frame lastFrame;
    private Frame automaticFrame, previewFrame, shownFrame;
    private GameObject overlay;
    private RawImage portrait;
    private AspectRatioFitter fitter;
    private int? observedHp;
    private float fallbackAt = float.PositiveInfinity;
    private float startedAt = float.NegativeInfinity;
    private float previewStartedAt = float.NegativeInfinity;
    private float previousIntensity;
    private bool rising;
    private bool automaticPending;
    private bool terminalFlash;
    private bool previewPending;
    private bool overlayVisible;

    public ArianeFlash(string[] files, Action<string> log)
    {
        frames = Array.ConvertAll(files, path => new Frame(path));
        nextFrame = frames.Length;
        this.log = log;
    }

    private void CreateOverlay()
    {
        ReleaseOverlay();
        try
        {
            overlay = new GameObject("REDLINE Ariane Overlay", new[] { Il2CppType.Of<RectTransform>() });
            overlay.SetActive(false);
            var canvas = overlay.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;

            var imageObject = new GameObject("Ariane Frame", new[] { Il2CppType.Of<RectTransform>() });
            imageObject.transform.SetParent(overlay.transform, false);
            portrait = imageObject.AddComponent<RawImage>();
            portrait.raycastTarget = false;
            fitter = imageObject.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        }
        catch
        {
            ReleaseOverlay();
            throw;
        }
    }

    // At 1 HP the stock death handlers take over and the vignette stops pulsing.
    private static bool VeryLowHealth(int hp) => hp is >= 1 and <= 15;
    private static bool CanPreview => (PlayerState.player || PlayerState.pcs) && !PlayerState.noplayer
        && !PlayerState.cutscene && !PlayerState.gameOver;
    private static bool CanFlash => CanPreview && PlayerState.gameState == PlayerState.gameStates.play;

    public void Hit(int before, bool suppressed)
    {
        if (suppressed)
            return;

        int hp = PlayerState.hp;
        if (hp >= before)
            return;

        observedHp = hp;
        CheckHealthLoss(before, hp, CanFlash, "damage_hook");
    }

    private void ObserveHealth(int hp, bool playing)
    {
        int before = observedHp ?? hp;
        observedHp = playing ? hp : null;
        if (playing && hp < before)
            CheckHealthLoss(before, hp, playing, "health_drop");
    }

    private void CheckHealthLoss(int before, int hp, bool playing, string source)
    {
        if (!playing || !VeryLowHealth(hp))
        {
            log($"ariane hp loss hp={before}->{hp} source={source} eligible=False playing={playing}");
            return;
        }

        bool queued = random.NextDouble() < HitChance;
        log($"ariane low-health hit hp={before}->{hp} source={source} queued={queued}");
        if (queued)
        {
            ResetAutomatic();
            terminalFlash = hp == 1;
            if (!terminalFlash)
                fallbackAt = Time.time + CrestWait;
        }
    }

    public void Observe(DamageCameraEffects camera, bool suppressed)
    {
        if (suppressed || float.IsPositiveInfinity(fallbackAt))
            return;

        if (!CanFlash || !VeryLowHealth(PlayerState.hp))
            return;

        var vignette = camera.redVignette;
        if (camera.shock || !vignette || !vignette.isActiveAndEnabled)
        {
            ClearPulse();
            return;
        }

        // Read the vignette after the game updates it so the image lands on its crest.
        float intensity = vignette.intensity;
        bool crest = rising && intensity < previousIntensity;
        if (intensity != previousIntensity)
            rising = intensity > previousIntensity;
        previousIntensity = intensity;
        if (!crest || previewPending || Time.unscaledTime - previewStartedAt < PreviewDuration)
            return;

        StartAutomatic("crest", intensity);
    }

    private void StartAutomatic(string trigger, float intensity = 0f)
    {
        fallbackAt = float.PositiveInfinity;
        startedAt = Time.unscaledTime;
        automaticFrame = NextFrame();
        automaticPending = true;
        log($"ariane flash start hp={PlayerState.hp} trigger={trigger} vignette={intensity:F3} art={automaticFrame.Name}");
    }

    public void Preview()
    {
        if (!CanPreview)
        {
            log($"ariane F1 blocked state={PlayerState.gameState} noplayer={PlayerState.noplayer} cutscene={PlayerState.cutscene} gameOver={PlayerState.gameOver}");
            return;
        }

        previewPending = true;
        log($"ariane F1 queued hp={PlayerState.hp}");
    }

    public void Tick(bool suppressed)
    {
        if (suppressed)
        {
            Reset();
            SetVisible(false);
            return;
        }

        bool playing = CanFlash;
        int hp = PlayerState.hp;
        ObserveHealth(hp, playing);
        if (!terminalFlash && (!playing || !VeryLowHealth(hp)))
            ResetAutomatic();

        float now = Time.unscaledTime;
        bool firstPreviewFrame = previewPending;
        if (!terminalFlash && (firstPreviewFrame || now - previewStartedAt < PreviewDuration) && CanPreview)
        {
            previewPending = false;
            if (firstPreviewFrame)
            {
                previewStartedAt = now;
                previewFrame = NextFrame();
                ClearPulse();
            }
            float previewElapsed = now - previewStartedAt;
            float fadeIn = Mathf.Clamp01((previewElapsed + 0.025f) / 0.05f);
            float fadeOut = Mathf.Clamp01((PreviewDuration - previewElapsed) / 0.11f);
            ShowFrame(previewFrame, fadeIn * fadeOut);
            if (firstPreviewFrame)
                log($"ariane F1 overlay shown hp={PlayerState.hp} art={previewFrame.Name} screen={Screen.width}x{Screen.height}");
            return;
        }
        ClearPreview();

        if (terminalFlash && automaticFrame == null)
            StartAutomatic("terminal_hit");
        else if (Time.time >= fallbackAt)
            StartAutomatic("hit_delay");

        float elapsed = now - startedAt;
        if (elapsed >= Duration)
        {
            terminalFlash = false;
            SetVisible(false);
            return;
        }

        float pulseIn = Mathf.Clamp01((elapsed + 0.012f) / 0.037f);
        float pulseOut = Mathf.Clamp01((Duration - elapsed) / 0.070f);
        ShowFrame(automaticFrame, pulseIn * pulseOut);
        if (automaticPending)
        {
            automaticPending = false;
            log($"ariane low-health overlay shown hp={PlayerState.hp} art={automaticFrame.Name}");
        }
    }

    private Frame NextFrame()
    {
        if (nextFrame == frames.Length)
        {
            for (int i = frames.Length - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (frames[i], frames[j]) = (frames[j], frames[i]);
            }
            if (frames.Length > 1 && frames[0] == lastFrame)
            {
                int j = random.Next(1, frames.Length);
                (frames[0], frames[j]) = (frames[j], frames[0]);
            }
            nextFrame = 0;
        }
        return lastFrame = frames[nextFrame++];
    }

    private void ShowFrame(Frame frame, float opacity)
    {
        if (!overlay || !portrait || !fitter)
            CreateOverlay();
        if (shownFrame != frame || !frame.Texture)
        {
            var texture = frame.GetTexture();
            portrait.texture = texture;
            fitter.aspectRatio = (float)texture.width / texture.height;
            shownFrame = frame;
        }
        portrait.color = new Color(1f, 1f, 1f, opacity);
        SetVisible(true);
    }

    private void SetVisible(bool visible)
    {
        if (overlayVisible == visible)
            return;

        overlayVisible = visible;
        if (overlay)
            overlay.SetActive(visible);
    }

    public void Reset()
    {
        observedHp = null;
        ClearPreview();
        ResetAutomatic();
    }

    public void SceneChanged()
    {
        Reset();
        ReleaseOverlay();
    }

    private void ClearPreview()
    {
        previewPending = false;
        previewStartedAt = float.NegativeInfinity;
        previewFrame = null;
    }

    private void ResetAutomatic()
    {
        fallbackAt = float.PositiveInfinity;
        ClearPulse();
    }

    private void ClearPulse()
    {
        startedAt = float.NegativeInfinity;
        automaticFrame = null;
        automaticPending = false;
        terminalFlash = false;
        previousIntensity = float.NaN;
        rising = false;
    }

    public void Dispose() => ReleaseOverlay();

    private void ReleaseOverlay()
    {
        var oldOverlay = overlay;
        overlay = null;
        portrait = null;
        fitter = null;
        shownFrame = null;
        overlayVisible = false;
        if (oldOverlay)
            UnityEngine.Object.Destroy(oldOverlay);
        foreach (var frame in frames)
            frame.Release();
    }

    private sealed class Frame(string path)
    {
        public readonly string Name = Path.GetFileNameWithoutExtension(path);
        private readonly byte[] image = File.ReadAllBytes(path);
        public Texture2D Texture { get; private set; }

        public Texture2D GetTexture()
        {
            if (Texture)
                return Texture;

            Texture = new Texture2D(2, 2, TextureFormat.RGB24, false)
            {
                name = "REDLINE Ariane " + Name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            try
            {
                if (!ImageConversion.LoadImage(Texture, image, true))
                    throw new InvalidDataException($"Could not decode {Name}.png.");
                return Texture;
            }
            catch
            {
                Release();
                throw;
            }
        }

        public void Release()
        {
            if (Texture)
                UnityEngine.Object.Destroy(Texture);
            Texture = null;
        }
    }
}
