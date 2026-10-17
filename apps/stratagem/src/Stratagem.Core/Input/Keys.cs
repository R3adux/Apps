namespace Stratagem.Core.Input;

/// <summary>
/// A physical keyboard key, identified by its set-1 scancode (+ E0 "extended" prefix).
/// Scancodes are positions, not letters: the same on QWERTY, QWERTZ and AZERTY — exactly what games read.
/// </summary>
public readonly record struct Key(ushort ScanCode, bool Extended)
{
    /// <summary>W3C KeyboardEvent.code name ("KeyW", "ControlLeft", "ArrowUp"), or "SC_0x.." if unknown.</summary>
    public string Name => KeyNames.NameOf(this);
    public override string ToString() => Name;
}

public enum MouseButton { Left, Right, Middle, X1, X2 }

/// <summary>A trigger: a keyboard key or a mouse button. Stored as "KeyW" / "MouseX1".</summary>
public readonly record struct InputRef(Key? Key, MouseButton? Mouse)
{
    public static InputRef Of(Key key) => new(key, null);
    public static InputRef Of(MouseButton button) => new(null, button);

    public string Name => Key is { } k ? k.Name : $"Mouse{Mouse}";
    public override string ToString() => Name;

    public static bool TryParse(string? name, out InputRef input)
    {
        input = default;
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.StartsWith("Mouse", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<MouseButton>(name[5..], ignoreCase: true, out var button)
            && Enum.IsDefined(button))
        {
            input = Of(button);
            return true;
        }
        if (KeyNames.TryParse(name, out var key)) { input = Of(key); return true; }
        return false;
    }

    public static InputRef Parse(string name) =>
        TryParse(name, out var input) ? input : throw new FormatException($"Unbekannte Taste: '{name}'");
}

/// <summary>
/// Names ↔ scancodes. Canonical names are the W3C UI Events "code" values, so the web UI can use
/// KeyboardEvent.code directly. Game config files use other names; see <see cref="FromGameName"/>.
/// </summary>
public static class KeyNames
{
    public static readonly Key ControlLeft = new(0x1D, false);
    public static readonly Key ControlRight = new(0x1D, true);
    public static readonly Key ArrowUp = new(0x48, true);
    public static readonly Key ArrowDown = new(0x50, true);
    public static readonly Key ArrowLeft = new(0x4B, true);
    public static readonly Key ArrowRight = new(0x4D, true);
    public static readonly Key KeyW = new(0x11, false);
    public static readonly Key KeyA = new(0x1E, false);
    public static readonly Key KeyS = new(0x1F, false);
    public static readonly Key KeyD = new(0x20, false);
    public static readonly Key Escape = new(0x01, false);

    private static readonly (string Name, ushort Scan, bool Ext)[] Table =
    [
        ("Escape", 0x01, false), ("Digit1", 0x02, false), ("Digit2", 0x03, false), ("Digit3", 0x04, false),
        ("Digit4", 0x05, false), ("Digit5", 0x06, false), ("Digit6", 0x07, false), ("Digit7", 0x08, false),
        ("Digit8", 0x09, false), ("Digit9", 0x0A, false), ("Digit0", 0x0B, false), ("Minus", 0x0C, false),
        ("Equal", 0x0D, false), ("Backspace", 0x0E, false), ("Tab", 0x0F, false),
        ("KeyQ", 0x10, false), ("KeyW", 0x11, false), ("KeyE", 0x12, false), ("KeyR", 0x13, false),
        ("KeyT", 0x14, false), ("KeyY", 0x15, false), ("KeyU", 0x16, false), ("KeyI", 0x17, false),
        ("KeyO", 0x18, false), ("KeyP", 0x19, false), ("BracketLeft", 0x1A, false), ("BracketRight", 0x1B, false),
        ("Enter", 0x1C, false), ("ControlLeft", 0x1D, false),
        ("KeyA", 0x1E, false), ("KeyS", 0x1F, false), ("KeyD", 0x20, false), ("KeyF", 0x21, false),
        ("KeyG", 0x22, false), ("KeyH", 0x23, false), ("KeyJ", 0x24, false), ("KeyK", 0x25, false),
        ("KeyL", 0x26, false), ("Semicolon", 0x27, false), ("Quote", 0x28, false), ("Backquote", 0x29, false),
        ("ShiftLeft", 0x2A, false), ("Backslash", 0x2B, false),
        ("KeyZ", 0x2C, false), ("KeyX", 0x2D, false), ("KeyC", 0x2E, false), ("KeyV", 0x2F, false),
        ("KeyB", 0x30, false), ("KeyN", 0x31, false), ("KeyM", 0x32, false), ("Comma", 0x33, false),
        ("Period", 0x34, false), ("Slash", 0x35, false), ("ShiftRight", 0x36, false),
        ("NumpadMultiply", 0x37, false), ("AltLeft", 0x38, false), ("Space", 0x39, false), ("CapsLock", 0x3A, false),
        ("F1", 0x3B, false), ("F2", 0x3C, false), ("F3", 0x3D, false), ("F4", 0x3E, false), ("F5", 0x3F, false),
        ("F6", 0x40, false), ("F7", 0x41, false), ("F8", 0x42, false), ("F9", 0x43, false), ("F10", 0x44, false),
        ("Pause", 0x45, false), ("ScrollLock", 0x46, false), // the hooks see NumLock as extended 0x45, Pause as plain 0x45
        ("Numpad7", 0x47, false), ("Numpad8", 0x48, false), ("Numpad9", 0x49, false), ("NumpadSubtract", 0x4A, false),
        ("Numpad4", 0x4B, false), ("Numpad5", 0x4C, false), ("Numpad6", 0x4D, false), ("NumpadAdd", 0x4E, false),
        ("Numpad1", 0x4F, false), ("Numpad2", 0x50, false), ("Numpad3", 0x51, false), ("Numpad0", 0x52, false),
        ("NumpadDecimal", 0x53, false), ("IntlBackslash", 0x56, false), ("F11", 0x57, false), ("F12", 0x58, false),
        ("F13", 0x64, false), ("F14", 0x65, false), ("F15", 0x66, false), ("F16", 0x67, false), ("F17", 0x68, false),
        ("F18", 0x69, false), ("F19", 0x6A, false), ("F20", 0x6B, false), ("F21", 0x6C, false), ("F22", 0x6D, false),
        ("F23", 0x6E, false), ("F24", 0x76, false),
        ("NumpadEnter", 0x1C, true), ("ControlRight", 0x1D, true), ("NumpadDivide", 0x35, true),
        ("AltRight", 0x38, true), ("Home", 0x47, true), ("ArrowUp", 0x48, true), ("PageUp", 0x49, true),
        ("ArrowLeft", 0x4B, true), ("ArrowRight", 0x4D, true), ("End", 0x4F, true), ("ArrowDown", 0x50, true),
        ("PageDown", 0x51, true), ("Insert", 0x52, true), ("Delete", 0x53, true),
        ("MetaLeft", 0x5B, true), ("MetaRight", 0x5C, true), ("ContextMenu", 0x5D, true), ("NumLock", 0x45, true),
    ];

    private static readonly Dictionary<string, Key> ByName =
        Table.ToDictionary(t => t.Name, t => new Key(t.Scan, t.Ext), StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<Key, string> ByKey = Table.ToDictionary(t => new Key(t.Scan, t.Ext), t => t.Name);

    public static IEnumerable<string> All => Table.Select(t => t.Name);

    public static string NameOf(Key key) => ByKey.TryGetValue(key, out var name) ? name : $"SC_0x{(key.Extended ? "E0" : "")}{key.ScanCode:X2}";

    public static bool TryParse(string? name, out Key key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (ByName.TryGetValue(name.Trim(), out key)) return true;
        if (FromGameName(name) is not { } fromGame) return false;
        key = fromGame;
        return true;
    }

    /// <summary>
    /// Maps key names as games/tools write them ("w", "left ctrl", "up arrow", "num 1", "lctrl") to a key.
    /// Tolerant on purpose: the exact naming in input_settings.config is not documented.
    /// </summary>
    public static Key? FromGameName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var n = new string(raw.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (n.Length == 0) return null;
        if (ByName.TryGetValue(n, out var direct)) return direct;
        if (n.Length == 1 && n[0] is >= 'a' and <= 'z') return ByName["Key" + char.ToUpperInvariant(n[0])];
        if (n.Length == 1 && n[0] is >= '0' and <= '9') return ByName["Digit" + n];
        foreach (var prefix in new[] { "numpad", "num", "kp", "keypad" })
        {
            if (n.StartsWith(prefix) && n.Length == prefix.Length + 1 && char.IsDigit(n[^1])) return ByName["Numpad" + n[^1]];
        }
        return n switch
        {
            "ctrl" or "control" or "lctrl" or "leftctrl" or "lcontrol" or "leftcontrol" or "ctrlleft" => ControlLeft,
            "rctrl" or "rightctrl" or "rcontrol" or "rightcontrol" or "ctrlright" => ControlRight,
            "shift" or "lshift" or "leftshift" => ByName["ShiftLeft"],
            "rshift" or "rightshift" => ByName["ShiftRight"],
            "alt" or "lalt" or "leftalt" => ByName["AltLeft"],
            "ralt" or "rightalt" or "altgr" => ByName["AltRight"],
            "up" or "uparrow" or "arrowup" or "cursorup" => ArrowUp,
            "down" or "downarrow" or "arrowdown" or "cursordown" => ArrowDown,
            "left" or "leftarrow" or "arrowleft" or "cursorleft" => ArrowLeft,
            "right" or "rightarrow" or "arrowright" or "cursorright" => ArrowRight,
            "esc" => Escape,
            "return" => ByName["Enter"],
            "spacebar" => ByName["Space"],
            "caps" or "capital" => ByName["CapsLock"],
            "del" => ByName["Delete"],
            "ins" => ByName["Insert"],
            "pgup" or "prior" => ByName["PageUp"],
            "pgdn" or "pgdown" or "next" => ByName["PageDown"],
            "pos1" => ByName["Home"],
            "tilde" or "grave" => ByName["Backquote"],
            _ => null,
        };
    }
}
