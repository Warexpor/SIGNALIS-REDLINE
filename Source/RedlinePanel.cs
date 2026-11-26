using System;
using System.Globalization;
using MelonLoader;
using UnityEngine;

namespace Redline;

internal sealed class RedlinePanel
{
    private const float Width = 654f;
    private const float HeaderBottom = 86f;
    private const float BodyTop = HeaderBottom + 1f;
    private const float BodyHeight = 315f;
    private const float FooterTop = BodyTop + BodyHeight;
    private const float Height = FooterTop + 52f;
    private const int CloseControl = -2;
    private const int ResetControl = -3;

    private static readonly Color Backdrop = new(0.015f, 0.02f, 0.024f, 0.76f);
    private static readonly Color Panel = new(0.045f, 0.052f, 0.058f);
    private static readonly Color Workspace = new(0.064f, 0.073f, 0.080f);
    private static readonly Color Border = new(0.26f, 0.29f, 0.30f);
    private static readonly Color Rule = new(0.14f, 0.16f, 0.17f);
    private static readonly Color Accent = new(1f, 0f, 0f);
    private static readonly Color Selected = new(0.105f, 0.123f, 0.136f);
    private static readonly Color Ink = new(0.90f, 0.91f, 0.88f);
    private static readonly Color Muted = new(0.64f, 0.69f, 0.69f);
    private static readonly Color Disabled = new(0.43f, 0.47f, 0.48f);
    private readonly RedlineSettings settings;
    private readonly Section[] sections;
    private PauseMenu ownedPause;
    private GameObject hiddenPauseMenu;
    private GUIStyle heading;
    private GUIStyle body;
    private GUIStyle small;
    private Vector2 origin;
    private float scale;
    private float styleScale;
    private bool previousCursorVisible;
    private CursorLockMode previousCursorLock;
    private bool dirty;
    private bool confirmingReset;
    private int sectionIndex;
    private int selection;
    private int pressed = -1;
    private int closedFrame = -2;

    public RedlinePanel(RedlineSettings settings)
    {
        this.settings = settings;
        sections = new[]
        {
            new Section("Combat", new[]
            {
                Slider("Reinforcements", "Adds enemies to eligible rooms on first entry.", settings.ReinforcementRate, maximum: 1f, suffix: "%"),
                Switch("Mag caches", "Let eligible final kills and finishing stomps drop ammo.", settings.MagCaches),
                Switch("Elster injuries", "Heavy leg hits can damage Elster's leg actuator. Arm tear is disabled for now.", settings.ElsterTrauma),
                Switch("Ariane flash", "Show Ariane flashes on low-health hits and allow the F1 preview.", settings.ArianeFlash)
            }),
            new Section("Audio", new[]
            {
                Switch("Recorded gunshots", "Use the recorded firing sounds for Elster's weapons.", settings.RecordedGunshots),
                Slider("Gunshot volume", "Volume of recorded shots.", settings.GunshotVolume, enabled: () => settings.RecordedGunshots.Value),
                Slider("Weapon room", "Strength of reflections and reverb for recorded shots and weapon handling.",
                    settings.GunshotRoom, enabled: () => settings.RecordedGunshots.Value || settings.WeaponHandling.Value),
                Switch("Weapon handling", "Use local handling recordings when available and soften empty clicks.", settings.WeaponHandling),
                Slider("Handling volume", "Volume of handling and empty clicks.", settings.HandlingVolume, enabled: () => settings.WeaponHandling.Value)
            }),
            new Section("Blood", new[]
            {
                Slider("Blood density", "Overall amount of REDLINE blood.", settings.Blood, maximum: 2.5f),
                Slider("Impact spray", "Amount of blood sprayed by shots, cuts and stomps.", settings.Spray),
                Slider("Pool size", "Size of newly formed blood pools.", settings.Pools),
                Slider("Wound bleeding", "Blood drops from exposed cuts as the body moves.", settings.WoundFlow),
                Slider("Elster stains", "Strength of blood stains on Elster's clothing.", settings.ElsterStains),
                Switch("Wall splatter", "Let airborne blood leave marks on walls.", settings.WallSplatter)
            }),
            new Section("Injuries", new[]
            {
                Switch("Dismemberment", "Allow shots and stomps to sever enemy parts.", settings.Dismember),
                Switch("Crawling", "Let living enemies crawl after losing a leg.", settings.Crawl, CanCut),
                Slider("Limb cut sensitivity", "How readily ordinary shots accumulate enough damage to cut.", settings.CutSensitivity, enabled: CanCut),
                Switch("Decapitation", "Allow fatal head cuts.", settings.Decapitation, CanCut),
                Slider("Waist split chance", "Base chance of splitting the torso on a heavy fatal shot.",
                    settings.WaistChance, 1f, 0.01f, "%", () => CanCut() && CanRagdoll()),
                Switch("Reassemble on revival", "Let Replikas pull reachable limbs back when they revive from death.", settings.Reassemble, CanCut)
            }),
            new Section("Physics", new[]
            {
                Switch("Ragdoll physics", "Use physical bodies for finishing stomps and qualifying heavy kills.", settings.Ragdoll),
                Switch("Extreme ragdoll", "Give heavy shots more knockback and tumble.", settings.ExtremeRagdoll, CanRagdoll),
                Slider("Throw speed", "Scale how fast the body launches along the floor.", settings.ThrowSpeed, enabled: CanRagdoll),
                Slider("Vertical lift", "Scale how strongly the body launches up or down.", settings.Lift, enabled: CanRagdoll),
                Slider("Spin", "Scale the amount of tumble when the body launches.", settings.Spin, enabled: CanRagdoll)
            }),
            new Section("System", new[]
            {
                Switch("Heavy-shot overkill", "Let powerful fatal hits bypass an enemy's recoverable fall.", settings.HeavyOverkill, CanRagdoll),
                Slider("Gore sound volume", "Volume of bullet impacts, fresh wounds, cuts, reattachment, and stomps.", settings.CutVolume),
                Switch("Diagnostic log", "Write detailed traces to UserData/Redline.verbose.log.", settings.DiagnosticLog),
                Switch("F8 cut preview", "Preview a leg cut on a nearby enemy without dealing HP damage.", settings.ScenePreview),
                Switch("F10 revival test", "Revive a standard Replika corpse, including permanent deaths.",
                    settings.RevivalTest)
            })
        };

        bool CanCut() => settings.Dismember.Value;
        bool CanRagdoll() => settings.Ragdoll.Value;
    }

    private Control[] Rows => sections[sectionIndex].Rows;

    private static Control Switch(string label, string help, MelonPreferences_Entry<bool> entry, Func<bool> enabled = null) =>
        new(label, help, entry, null, 0, 0, "", enabled);

    private static Control Slider(string label, string help, MelonPreferences_Entry<float> entry, float maximum = 2f,
        float step = 0.05f, string suffix = "x", Func<bool> enabled = null) =>
        new(label, help, null, entry, maximum, step, suffix, enabled);

    private sealed record Section(string Label, Control[] Rows);
    private sealed record Control(string Label, string Help, MelonPreferences_Entry<bool> Toggle, MelonPreferences_Entry<float> Slider,
        float Maximum, float Step, string Suffix, Func<bool> Enabled)
    {
        public bool IsEnabled => Enabled?.Invoke() != false;
    }

    public bool IsOpen { get; private set; }
    public bool CapturesInput => IsOpen || Time.frameCount == closedFrame;

    public void Update(string scene)
    {
        if (!Input.GetKeyDown(KeyCode.F9))
            return;
        if (IsOpen)
        {
            Close();
            return;
        }
        PauseMenu pause = null;
        bool pauseGameplay = false;
        if (scene != "MainMenu")
        {
            var state = PlayerState.gameState;
            pauseGameplay = state == PlayerState.gameStates.play && PlayerState.player && !PlayerState.noplayer;
            if (!pauseGameplay && state != PlayerState.gameStates.paused)
                return;
            pause = PauseMenu.instance;
            if (!pause)
                return;
        }

        previousCursorLock = Cursor.lockState;
        previousCursorVisible = Cursor.visible;
        IsOpen = true;
        if (pauseGameplay)
        {
            ownedPause = pause;
            pause.setPause(true);
            // setPause writes gameState immediately. The derived pause flags update later.
            if (PlayerState.gameState != PlayerState.gameStates.paused)
            {
                Close();
                return;
            }
        }
        if (pause && pause.MenuObject && pause.MenuObject.activeSelf)
        {
            hiddenPauseMenu = pause.MenuObject;
            hiddenPauseMenu.SetActive(false);
        }
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    public void Close(bool resume = true)
    {
        bool wasOpen = IsOpen;
        if (wasOpen)
        {
            IsOpen = false;
            pressed = -1;
            confirmingReset = false;
            closedFrame = Time.frameCount;
        }
        var pause = ownedPause;
        var menu = hiddenPauseMenu;
        ownedPause = null;
        hiddenPauseMenu = null;
        try
        {
            if (resume && PlayerState.gameState == PlayerState.gameStates.paused)
            {
                if (pause)
                    pause.setPause(false);
                else if (menu)
                    menu.SetActive(true);
            }
        }
        finally
        {
            if (wasOpen)
            {
                Cursor.lockState = previousCursorLock;
                Cursor.visible = previousCursorVisible;
            }
            if (dirty)
            {
                settings.Category.SaveToFile(false);
                dirty = false;
            }
        }
    }

    public void Draw()
    {
        float fit = Mathf.Min((Screen.width - 24f) / Width, (Screen.height - 24f) / Height);
        scale = Mathf.Min(fit, Mathf.Min(1.7f, Screen.height / 760f));
        if (scale <= 0)
            return;
        origin = new Vector2((Screen.width - Width * scale) * 0.5f, (Screen.height - Height * scale) * 0.5f);
        var input = Event.current;
        var pointer = (input.mousePosition - origin) / scale;
        if (input.type != EventType.Repaint)
        {
            HandleInput(input, pointer);
            return;
        }
        if (heading == null)
        {
            heading = Style(FontStyle.Bold);
            body = Style(FontStyle.Normal);
            small = Style(FontStyle.Normal);
        }
        if (scale != styleScale)
        {
            styleScale = scale;
            heading.fontSize = Mathf.RoundToInt(25f * scale);
            body.fontSize = Mathf.RoundToInt(16f * scale);
            small.fontSize = Mathf.RoundToInt(13f * scale);
        }

        int depth = GUI.depth;
        GUI.depth = -100;
        try
        {
            Fill(new Rect(0, 0, Screen.width, Screen.height), Backdrop);
            Box(0, 0, Width, Height, Border);
            Box(1, 1, Width - 2, Height - 2, Panel);
            Text(24, 14, 230, 31, "REDLINE", heading, Accent);
            Text(24, 52, 300, 20, "SIGNALIS COMBAT OVERHAUL", small, Muted);
            Box(1, HeaderBottom, Width - 2, 1, Border);
            Box(1, FooterTop, Width - 2, 1, Border);
            if (confirmingReset)
                DrawResetConfirmation(pointer);
            else
                DrawSettings(pointer);
        }
        finally { GUI.depth = depth; }
    }

    private void DrawSettings(Vector2 pointer)
    {
        Box(152, BodyTop, Width - 153, BodyHeight, Workspace);
        Box(151, BodyTop, 1, BodyHeight, Rule);
        for (int i = 0; i < sections.Length; i++)
        {
            Rect tab = TabAt(i);
            bool active = i == sectionIndex;
            if (active || tab.Contains(pointer))
                Box(tab.x, tab.y, tab.width, tab.height, Selected);
            if (active)
                Box(tab.x, tab.y + 7, 2, tab.height - 14, Accent);
            Text(tab.x + 12, tab.y, tab.width - 12, tab.height, sections[i].Label, body, active ? Ink : Muted);
        }

        for (int i = 0; i < Rows.Length; i++)
            DrawRow(i, pointer);

        int hoverRow = RowAt(pointer);
        var help = Rows[hoverRow >= 0 ? hoverRow : selection];
        Box(170, FooterTop - 40, 462, 1, Rule);
        Text(178, FooterTop - 35, 448, 24, help.Help, small, help.IsEnabled ? Muted : Disabled);
        Text(24, FooterTop + 7, 296, 34, "Tab  Section     ↑↓  Select     ←→  Adjust", small, Muted);
        DrawButton(ResetButton, "Reset defaults [R]", pointer);
        DrawButton(CloseButton, "Save & close", pointer, primary: true);
    }

    private void DrawResetConfirmation(Vector2 pointer)
    {
        Box(1, BodyTop, Width - 2, BodyHeight, Workspace);
        Text(26, BodyTop + 61, 602, 39, "Reset all REDLINE settings?", heading, Ink);
        Text(27, BodyTop + 112, 600, 26, "This restores the defaults across all sections.", body, Muted);
        Text(27, BodyTop + 142, 600, 26, "The defaults are saved when you close the panel.", body, Muted);
        DrawButton(ResetButton, "Cancel [Esc]", pointer);
        DrawButton(CloseButton, "Reset all [Y]", pointer, primary: true);
    }

    private void DrawButton(Rect button, string label, Vector2 pointer, bool primary = false)
    {
        bool hovered = button.Contains(pointer);
        Box(button.x, button.y, button.width, button.height, primary || hovered ? Accent : Border);
        if (!primary || !hovered)
            Box(button.x + 1, button.y + 1, button.width - 2, button.height - 2, hovered ? Selected : Panel);
        Text(button.x, button.y, button.width, button.height, label, small,
            primary ? (hovered ? Panel : Accent) : Ink, TextAnchor.MiddleCenter);
    }

    private void DrawRow(int index, Vector2 pointer)
    {
        Rect row = Row(index);
        var control = Rows[index];
        bool enabled = control.IsEnabled;
        if (enabled && row.Contains(pointer))
            Box(row.x, row.y, row.width, row.height, Selected);
        if (enabled && selection == index)
        {
            Box(row.x, row.y + 9, 2, row.height - 18, Accent);
        }
        if (index < Rows.Length - 1)
            Box(row.x + 8, row.yMax, row.width - 16, 1, Rule);

        Text(row.x + 8, row.y, 238, row.height, control.Label, body, enabled ? Ink : Disabled);

        if (control.Slider != null)
        {
            Rect track = Track(index);
            float value = RedlineSettings.Read(control.Slider, control.Maximum);
            float thumb = track.x + track.width * value / control.Maximum;
            Box(track.x, track.center.y, track.width, 1, Border);
            Box(track.x, track.center.y, thumb - track.x, 1, enabled ? Muted : Disabled);
            Box(thumb - 2, track.center.y - 5, 4, 11, !enabled ? Disabled : selection == index ? Accent : Ink);
            string display = control.Suffix == "%" ? value.ToString("0%", CultureInfo.InvariantCulture)
                : value.ToString("0.00", CultureInfo.InvariantCulture) + control.Suffix;
            Text(564, row.y, 60, row.height, display, small, enabled ? Ink : Disabled, TextAnchor.MiddleRight);
            return;
        }

        bool valueOn = control.Toggle.Value;
        Box(569, row.center.y - 6, 13, 13, enabled ? Muted : Disabled);
        Box(570, row.center.y - 5, 11, 11, Workspace);
        if (valueOn)
            Box(572, row.center.y - 3, 7, 7, enabled ? Ink : Disabled);
        Text(587, row.y, 37, row.height, valueOn ? "On" : "Off", small,
            !enabled ? Disabled : valueOn ? Ink : Muted, TextAnchor.MiddleRight);
    }

    private void HandleInput(Event input, Vector2 pointer)
    {
        if (confirmingReset)
        {
            HandleResetInput(input, pointer);
            return;
        }
        if (input.type == EventType.KeyDown)
        {
            switch (input.keyCode)
            {
                case KeyCode.Escape:
                    Close();
                    break;
                case KeyCode.UpArrow:
                    MoveSelection(-1);
                    break;
                case KeyCode.DownArrow:
                    MoveSelection(1);
                    break;
                case KeyCode.Tab:
                    SelectSection((sectionIndex + (input.shift ? -1 : 1) + sections.Length) % sections.Length);
                    break;
                case KeyCode.R:
                    BeginReset();
                    break;
                case KeyCode.LeftArrow:
                    Adjust(selection, -1);
                    break;
                case KeyCode.RightArrow:
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                case KeyCode.Space:
                    Adjust(selection, 1);
                    break;
                default:
                    return;
            }
            input.Use();
        }
        else if (input.type == EventType.MouseDown && input.button == 0)
        {
            pressed = FooterControlAt(pointer);
            for (int i = 0; i < sections.Length; i++)
            {
                if (TabAt(i).Contains(pointer))
                {
                    SelectSection(i);
                    break;
                }
            }
            int row = RowAt(pointer);
            if (row >= 0 && Rows[row].IsEnabled)
            {
                selection = row;
                if (Rows[row].Toggle != null || Track(row).Contains(pointer))
                    pressed = row;
                if (pressed == row && Rows[row].Slider != null)
                    SetSliderFromMouse(pointer.x);
            }
            input.Use();
        }
        else if (input.type == EventType.MouseDrag && pressed >= 0 && Rows[pressed].Slider != null)
        {
            SetSliderFromMouse(pointer.x);
            input.Use();
        }
        else if (input.type == EventType.MouseUp && input.button == 0)
        {
            int released = pressed;
            pressed = -1;
            if (released == CloseControl && CloseButton.Contains(pointer))
                Close();
            else if (released == ResetControl && ResetButton.Contains(pointer))
                BeginReset();
            else if (released >= 0 && Rows[released].Toggle != null && Row(released).Contains(pointer))
                Adjust(released, 1);
            input.Use();
        }
    }

    private void BeginReset()
    {
        confirmingReset = true;
        pressed = -1;
    }

    private void HandleResetInput(Event input, Vector2 pointer)
    {
        if (input.type == EventType.KeyDown)
        {
            if (input.keyCode == KeyCode.Y)
                ConfirmReset();
            else if (input.keyCode == KeyCode.Escape)
            {
                confirmingReset = false;
                pressed = -1;
            }
            input.Use();
        }
        else if (input.type == EventType.MouseDown && input.button == 0)
        {
            pressed = FooterControlAt(pointer);
            input.Use();
        }
        else if (input.type == EventType.MouseUp && input.button == 0)
        {
            int released = pressed;
            pressed = -1;
            if (released == CloseControl && CloseButton.Contains(pointer))
                ConfirmReset();
            else if (released == ResetControl && ResetButton.Contains(pointer))
                confirmingReset = false;
            input.Use();
        }
    }

    private void ConfirmReset()
    {
        settings.ResetToDefaults();
        dirty = true;
        confirmingReset = false;
        pressed = -1;
    }

    private void MoveSelection(int step)
    {
        for (int i = 0; i < Rows.Length; i++)
        {
            selection = (selection + step + Rows.Length) % Rows.Length;
            if (Rows[selection].IsEnabled)
                return;
        }
    }

    private void SelectSection(int index)
    {
        sectionIndex = index;
        selection = 0;
        pressed = -1;
        if (!Rows[selection].IsEnabled)
            MoveSelection(1);
    }

    private void Adjust(int row, int step)
    {
        var control = Rows[row];
        if (!control.IsEnabled)
            return;
        if (control.Slider != null)
            SetSlider(control, RedlineSettings.Read(control.Slider, control.Maximum) + step * control.Step);
        else
        {
            control.Toggle.Value = !control.Toggle.Value;
            dirty = true;
        }
    }

    private void SetSliderFromMouse(float x)
    {
        Rect track = Track(pressed);
        var control = Rows[pressed];
        float fraction = Mathf.Clamp01((x - track.x) / track.width);
        SetSlider(control, fraction * control.Maximum);
    }

    private void SetSlider(Control control, float value)
    {
        value = Mathf.Round(Mathf.Clamp(value, 0, control.Maximum) * 100f) / 100f;
        if (control.Slider.Value == value)
            return;
        control.Slider.Value = value;
        dirty = true;
    }

    private int RowAt(Vector2 pointer)
    {
        for (int i = 0; i < Rows.Length; i++)
            if (Row(i).Contains(pointer))
                return i;
        return -1;
    }

    private static int FooterControlAt(Vector2 pointer) =>
        CloseButton.Contains(pointer) ? CloseControl : ResetButton.Contains(pointer) ? ResetControl : -1;

    private static Rect TabAt(int index) => new(16, BodyTop + 15 + index * 42, 120, 37);
    private static Rect Row(int index) => new(170, BodyTop + 12 + index * 43, 462, 40);
    private static Rect Track(int index) => new(405, Row(index).center.y - 12, 150, 24);
    private static Rect ResetButton => new(336, FooterTop + 9, 146, 31);
    private static Rect CloseButton => new(494, FooterTop + 9, 136, 31);

    private static GUIStyle Style(FontStyle weight) => new()
    {
        fontStyle = weight,
        clipping = TextClipping.Clip
    };

    private Rect Place(float x, float y, float width, float height) =>
        new(origin.x + x * scale, origin.y + y * scale, width * scale, height * scale);

    private void Box(float x, float y, float width, float height, Color color)
    {
        if (width > 0 && height > 0)
            Fill(Place(x, y, width, height), color);
    }

    private static void Fill(Rect rect, Color color) =>
        GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, color, 0, 0);

    private void Text(float x, float y, float width, float height, string text, GUIStyle style, Color color,
        TextAnchor alignment = TextAnchor.MiddleLeft)
    {
        style.alignment = alignment;
        style.normal.textColor = color;
        GUI.Label(Place(x, y, width, height), text, style);
    }
}
