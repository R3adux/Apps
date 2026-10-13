using Stratagem.Core.Input;
using Stratagem.Core.Model;
using Stratagem.Core.Radial;

namespace Stratagem.Core.Settings;

/// <summary>
/// Everything the player can set. Stored as JSON (settings.json); keys are stored by name ("ControlLeft",
/// "MouseX1"), codes as letters ("URDDD"), so the file stays readable and editable by hand.
/// Game-specific values (window title, process name) live here too, so a game patch needs no new program.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public bool Paused { get; set; }
    /// <summary>Hotkeys/radial only act while the game window is in the foreground.</summary>
    public bool OnlyWhenGameFocused { get; set; } = true;
    public GameSettings Game { get; set; } = new();
    public RadialSettings Radial { get; set; } = new();
    public TimingSettings Timing { get; set; } = new();
    public ListSettings List { get; set; } = new();
    public List<HotkeySetting> Hotkeys { get; set; } = [];
    public WebSettings Web { get; set; } = new();

    public RadialOptions ToRadialOptions() => new(
        Radial.Mode,
        InputRef.TryParse(Radial.Trigger, out var trigger) ? trigger : RadialOptions.Default.Trigger,
        Radial.DragButton,
        Radial.DeadzoneCounts,
        Radial.ClampCounts,
        Radial.TimeoutMs);

    public MacroTiming ToTiming() => new MacroTiming(Timing.KeyHoldMs, Timing.KeyGapMs, Timing.MenuOpenMs).Clamped();

    /// <summary>Bindings typed in by the player (used when the game file is not read or not found).</summary>
    public GameBindings ToManualBindings()
    {
        var d = GameBindings.Default;
        return new GameBindings(
            KeyOr(Game.MenuKey, d.Menu), Game.MenuMode,
            KeyOr(Game.Up, d.Up), KeyOr(Game.Down, d.Down), KeyOr(Game.Left, d.Left), KeyOr(Game.Right, d.Right));

        static Key KeyOr(string? name, Key fallback) => KeyNames.TryParse(name, out var k) ? k : fallback;
    }

    public IReadOnlyDictionary<InputRef, HotkeyAction> ToHotkeys()
    {
        var result = new Dictionary<InputRef, HotkeyAction>();
        foreach (var h in Hotkeys)
        {
            if (!InputRef.TryParse(h.Input, out var input)) continue;
            if (StratagemCode.TryParse(h.Code, out var code)) result[input] = HotkeyAction.ForCode(code);
            else if (h.Slot is >= 1 and <= 32) result[input] = HotkeyAction.ForSlot(h.Slot.Value - 1);
        }
        return result;
    }

    public IReadOnlyList<StratagemItem> ToManualItems(StratagemCatalog catalog) =>
        List.Manual
            .Select(e => StratagemCode.TryParse(e.Code, out var code)
                ? new StratagemItem(code, string.IsNullOrWhiteSpace(e.Name) ? catalog.NameOf(code) : e.Name.Trim())
                : null)
            .OfType<StratagemItem>()
            .ToList();

    /// <summary>Repairs out-of-range values (hand-edited file, older version). Returns what was changed.</summary>
    public IReadOnlyList<string> Normalize()
    {
        var fixes = new List<string>();
        Game ??= new();
        Radial ??= new();
        Timing ??= new();
        List ??= new();
        List.Manual ??= [];
        Hotkeys ??= [];
        Web ??= new();

        if (!InputRef.TryParse(Radial.Trigger, out _)) { fixes.Add($"Unbekannte Auslöse-Taste '{Radial.Trigger}', zurück auf Strg links."); Radial.Trigger = "ControlLeft"; }
        Radial.DeadzoneCounts = Clamp(Radial.DeadzoneCounts, 5, 1000, nameof(Radial.DeadzoneCounts), fixes);
        Radial.ClampCounts = Clamp(Radial.ClampCounts, Radial.DeadzoneCounts + 1, 5000, nameof(Radial.ClampCounts), fixes);
        Radial.TimeoutMs = Clamp(Radial.TimeoutMs, 500, 60000, nameof(Radial.TimeoutMs), fixes);
        Radial.SizePx = Clamp(Radial.SizePx, 200, 1200, nameof(Radial.SizePx), fixes);
        if (Radial.Opacity is < 0.2 or > 1.0 || double.IsNaN(Radial.Opacity)) { Radial.Opacity = 0.85; fixes.Add("Deckkraft"); }
        Timing.KeyHoldMs = Clamp(Timing.KeyHoldMs, 5, 500, nameof(Timing.KeyHoldMs), fixes);
        Timing.KeyGapMs = Clamp(Timing.KeyGapMs, 5, 500, nameof(Timing.KeyGapMs), fixes);
        Timing.MenuOpenMs = Clamp(Timing.MenuOpenMs, 0, 2000, nameof(Timing.MenuOpenMs), fixes);
        Web.Port = Clamp(Web.Port, 1024, 65535, nameof(Web.Port), fixes);
        if (string.IsNullOrWhiteSpace(Web.Token) || Web.Token.Length < 16) Web.Token = WebSettings.NewToken();
        if (string.IsNullOrWhiteSpace(Game.WindowTitle)) Game.WindowTitle = new GameSettings().WindowTitle;
        if (string.IsNullOrWhiteSpace(Game.ProcessName)) Game.ProcessName = new GameSettings().ProcessName;
        Version = CurrentVersion;
        return fixes;

        static int Clamp(int value, int min, int max, string name, List<string> fixes)
        {
            if (value >= min && value <= max) return value;
            fixes.Add($"{name} {value} → {Math.Clamp(value, min, max)}");
            return Math.Clamp(value, min, max);
        }
    }
}

public sealed class GameSettings
{
    /// <summary>The game window is recognised by its title (no access to the game process, see KONZEPT.md).</summary>
    public string WindowTitle { get; set; } = "HELLDIVERS™ 2";
    /// <summary>Only used to show "game running" (process list, no handle to the process).</summary>
    public string ProcessName { get; set; } = "helldivers2";
    /// <summary>Read the bindings from the game's input_settings.config (and follow changes).</summary>
    public bool ReadBindingsFromGame { get; set; } = true;
    public string MenuKey { get; set; } = "ControlLeft";
    public MenuMode MenuMode { get; set; } = MenuMode.Hold;
    public string Up { get; set; } = "KeyW";
    public string Down { get; set; } = "KeyS";
    public string Left { get; set; } = "KeyA";
    public string Right { get; set; } = "KeyD";
}

public sealed class RadialSettings
{
    public bool Enabled { get; set; } = true;
    public RadialMode Mode { get; set; } = RadialMode.HoldRelease;
    public string Trigger { get; set; } = "ControlLeft";
    public MouseButton DragButton { get; set; } = MouseButton.Left;
    /// <summary>Mouse counts (not pixels): depends on the mouse DPI.</summary>
    public int DeadzoneCounts { get; set; } = 40;
    public int ClampCounts { get; set; } = 160;
    public int TimeoutMs { get; set; } = 5000;
    public int SizePx { get; set; } = 420;
    public double Opacity { get; set; } = 0.85;
}

public sealed class TimingSettings
{
    public int KeyHoldMs { get; set; } = 35;
    public int KeyGapMs { get; set; } = 35;
    public int MenuOpenMs { get; set; } = 60;
}

public enum ListSource
{
    /// <summary>The player maintains the list in the web UI.</summary>
    Manual,
    /// <summary>Read from the in-game stratagem list on screen (not built yet: needs screenshots).</summary>
    Screen,
}

public sealed class ListSettings
{
    public ListSource Source { get; set; } = ListSource.Manual;
    public List<ManualEntry> Manual { get; set; } =
    [
        new() { Name = "Reinforce", Code = "UDRLU" },
        new() { Name = "Resupply", Code = "DDUR" },
        new() { Name = "Eagle 500kg Bomb", Code = "URDDD" },
        new() { Name = "Orbital Precision Strike", Code = "RRU" },
        new() { Name = "Eagle Airstrike", Code = "URDR" },
        new() { Name = "SOS Beacon", Code = "UDRU" },
    ];
}

public sealed class ManualEntry
{
    public string? Name { get; set; }
    public string Code { get; set; } = "";
}

public sealed class HotkeySetting
{
    public string Input { get; set; } = "";
    /// <summary>1-based place in the current list …</summary>
    public int? Slot { get; set; }
    /// <summary>… or a fixed code ("URDDD"), which wins over Slot.</summary>
    public string? Code { get; set; }
}

public sealed class WebSettings
{
    public int Port { get; set; } = 8133;
    /// <summary>Reachable from the home network (phone as stream deck). Needs the token.</summary>
    public bool AllowLan { get; set; }
    public string Token { get; set; } = NewToken();

    public static string NewToken() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
}
