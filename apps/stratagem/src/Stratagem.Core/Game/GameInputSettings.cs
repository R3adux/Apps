using Stratagem.Core.Input;

namespace Stratagem.Core.Game;

/// <summary>Result of reading the game's key bindings. Notes explain what could not be used (shown in the web UI).</summary>
/// <param name="Others">Every keyboard/mouse binding found in the file (all sections), to warn about collisions.</param>
public sealed record BindingsResult(GameBindings Bindings, string? SourceFile, IReadOnlyList<string> Notes, IReadOnlyList<GameBinding>? Others = null)
{
    public IReadOnlyList<GameBinding> AllBindings => Others ?? [];
}

/// <summary>One binding of the game: e.g. MouseX1 → Avatar.CameraSwitchSide (LongPress).</summary>
public sealed record GameBinding(InputRef Input, string Section, string Action, string? Trigger);

/// <summary>
/// Reads the stratagem bindings from the game's <c>input_settings.config</c>
/// (Steam\userdata\&lt;id&gt;\553850\remote\). The file only exists once the player changed a binding;
/// without it the game defaults apply.
/// Structure (checked against a real file from October 2026; Arrowhead's own guide is linked in the file header):
/// <code>
/// Stratagem = {
///     Up = [ { trigger = "Press"  device_type = "Keyboard"  input = "up"  threshold = 0.2  input_type = "Button" } ]
///     Start = [ { trigger = "Press"  device_type = "Keyboard"  input = "left ctrl" ... } ]   // open the stratagem list
/// }
/// </code>
/// Only actions that differ from the game's defaults are stored; an action lists all its bindings (pads too).
/// Seen key names: "left ctrl", "left shift", "up", "tab", "c", "4"; mouse: "MouseButton1…5", "MouseWheel".
/// Seen triggers: Press, Hold, Tap, LongPress, and Y+/Y- for axes.
/// </summary>
public static class GameInputSettings
{
    public const string SteamAppId = "553850";
    public const string FileName = "input_settings.config";

    public static BindingsResult Read(string text, string? sourceFile = null)
    {
        var notes = new List<string>();
        Dictionary<string, object?> root;
        try { root = Sjson.Parse(text); }
        catch (Exception e)
        {
            notes.Add($"Datei nicht lesbar ({e.Message}), Standardbelegung wird verwendet.");
            return new BindingsResult(GameBindings.Default, sourceFile, notes);
        }

        var all = CollectAll(root);
        var section = FindSection(root, "Stratagem");
        if (section is null)
        {
            notes.Add("Stratagem-Tasten im Spiel nicht geändert – Standardbelegung des Spiels wird verwendet.");
            return new BindingsResult(GameBindings.Default, sourceFile, notes, all);
        }

        var d = GameBindings.Default;
        var up = Direction(section, "Up", d.Up, notes);
        var down = Direction(section, "Down", d.Down, notes);
        var left = Direction(section, "Left", d.Left, notes);
        var right = Direction(section, "Right", d.Right, notes);

        var menu = d.Menu;
        var mode = d.MenuMode;
        ReportUnknown(section, "Start", notes);
        var menuEntry = KeyboardEntries(section, "Start").FirstOrDefault();
        if (menuEntry.Entry is null)
        {
            if (Entries(section, "Start").Any()) notes.Add("Stratagem-Liste liegt nicht auf der Tastatur – Strg (halten) wird angenommen.");
        }
        else
        {
            menu = menuEntry.Key;
            var trigger = Str(menuEntry.Entry, "trigger");
            mode = trigger?.ToLowerInvariant() switch
            {
                "hold" or null => MenuMode.Hold,
                "press" or "tap" => MenuMode.Press,
                _ => MenuMode.Hold,
            };
            if (trigger is not null && mode == MenuMode.Hold && !trigger.Equals("hold", StringComparison.OrdinalIgnoreCase))
                notes.Add($"Stratagem-Liste öffnet per '{trigger}' – das wird nicht unterstützt. Bitte im Spiel auf 'Halten' oder 'Drücken' stellen.");
        }

        return new BindingsResult(new GameBindings(menu, mode, up, down, left, right), sourceFile, notes, all);
    }

    /// <summary>All keyboard keys and mouse buttons bound anywhere in the file (axes like the wheel are skipped).</summary>
    private static List<GameBinding> CollectAll(Dictionary<string, object?> root)
    {
        var result = new List<GameBinding>();
        foreach (var (sectionName, sectionValue) in root)
        {
            if (sectionValue is not Dictionary<string, object?> section) continue;
            foreach (var action in section.Keys)
            {
                foreach (var entry in Entries(section, action))
                {
                    var input = ToInput(Str(entry, "device_type"), Str(entry, "input"), LayoutId(entry));
                    if (input is { } i) result.Add(new GameBinding(i, sectionName, action, Str(entry, "trigger")));
                }
            }
        }
        return result;
    }

    internal static InputRef? ToInput(string? device, string? input, int? layoutId = null)
    {
        if (string.Equals(device, "Keyboard", StringComparison.OrdinalIgnoreCase))
            return KeyOf(input, layoutId) is { } key ? InputRef.Of(key) : null;
        if (string.Equals(device, "Mouse", StringComparison.OrdinalIgnoreCase))
        {
            return input?.ToLowerInvariant() switch
            {
                "mousebutton1" => InputRef.Of(MouseButton.Left),
                "mousebutton2" => InputRef.Of(MouseButton.Right),
                "mousebutton3" => InputRef.Of(MouseButton.Middle),
                "mousebutton4" => InputRef.Of(MouseButton.X1),
                "mousebutton5" => InputRef.Of(MouseButton.X2),
                _ => null,
            };
        }
        return null;
    }

    /// <summary>
    /// Candidate files, best first: the active Steam user, then other Steam users (newest first),
    /// then the documented fallback under %APPDATA%\Arrowhead\Helldivers2.
    /// </summary>
    public static IReadOnlyList<string> Locate(string? steamPath, uint? activeSteamUser, string? appData)
    {
        var found = new List<string>();
        if (!string.IsNullOrEmpty(steamPath))
        {
            var userdata = Path.Combine(steamPath, "userdata");
            if (activeSteamUser is > 0)
                Add(found, Path.Combine(userdata, activeSteamUser.Value.ToString(), SteamAppId, "remote", FileName));
            if (Directory.Exists(userdata))
            {
                var others = Directory.EnumerateDirectories(userdata)
                    .Select(dir => Path.Combine(dir, SteamAppId, "remote", FileName))
                    .Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc);
                foreach (var f in others) Add(found, f);
            }
        }
        if (!string.IsNullOrEmpty(appData))
        {
            Add(found, Path.Combine(appData, "Arrowhead", "Helldivers2", FileName));
            Add(found, Path.Combine(appData, "Arrowhead", "Helldivers2", "saves", FileName));
        }
        return found;

        static void Add(List<string> list, string path)
        {
            if (File.Exists(path) && !list.Contains(path, StringComparer.OrdinalIgnoreCase)) list.Add(path);
        }
    }

    private static Key Direction(Dictionary<string, object?> section, string action, Key fallback, List<string> notes)
    {
        ReportUnknown(section, action, notes);
        var keys = KeyboardEntries(section, action).Select(e => e.Key).ToList();
        if (keys.Count == 0)
        {
            if (Entries(section, action).Any()) notes.Add($"Stratagem '{action}' liegt nicht auf der Tastatur – {fallback} wird verwendet.");
            return fallback;
        }
        // Arrow keys first: they do not collide with walking (WASD).
        return keys.FirstOrDefault(k => k == KeyNames.ArrowUp || k == KeyNames.ArrowDown || k == KeyNames.ArrowLeft || k == KeyNames.ArrowRight, keys[0]);
    }

    private static void ReportUnknown(Dictionary<string, object?> section, string action, List<string> notes)
    {
        foreach (var entry in Entries(section, action))
        {
            var device = Str(entry, "device_type");
            var input = Str(entry, "input");
            if (device is not null && !device.Equals("Keyboard", StringComparison.OrdinalIgnoreCase)) continue;
            if (input is not null && KeyOf(input, LayoutId(entry)) is null)
                notes.Add($"Unbekannter Tastenname '{input}' bei Stratagem '{action}'.");
        }
    }

    private static IEnumerable<(Dictionary<string, object?> Entry, Key Key)> KeyboardEntries(Dictionary<string, object?> section, string action)
    {
        foreach (var entry in Entries(section, action))
        {
            var device = Str(entry, "device_type");
            if (device is not null && !device.Equals("Keyboard", StringComparison.OrdinalIgnoreCase)) continue;
            if (KeyOf(Str(entry, "input"), LayoutId(entry)) is { } key) yield return (entry, key);
        }
    }

    /// <summary>
    /// The physical key. The game writes the key's name ("w", "left ctrl", "up") and its position on the keyboard
    /// (fixed_layout_id = set-1 scancode, without the E0 prefix). The position wins: names of letter keys depend on
    /// the keyboard layout (QWERTZ, AZERTY), positions do not. The name still tells extended keys apart
    /// ("up" = E0 48, not Numpad 8).
    /// </summary>
    internal static Key? KeyOf(string? input, int? layoutId)
    {
        var named = KeyNames.FromGameName(input);
        if (layoutId is not (> 0 and < 0x80)) return named;
        if (named is { } n && n.ScanCode == layoutId) return n;
        // An unknown name on a position that also has an E0 twin (Numpad / vs. /, right Ctrl, arrows vs. numpad):
        // which of the two is meant is not known – better report it than press the wrong one.
        if (named is null && HasExtendedTwin(layoutId.Value)) return null;
        return new Key((ushort)layoutId.Value, false);
    }

    private static bool HasExtendedTwin(int scan) => scan is 0x1C or 0x1D or 0x35 or 0x38 or (>= 0x47 and <= 0x53) or (>= 0x5B and <= 0x5D);

    private static int? LayoutId(Dictionary<string, object?> entry) =>
        entry.TryGetValue("fixed_layout_id", out var v) && v is double d && d == Math.Floor(d) ? (int)d : null;

    private static IEnumerable<Dictionary<string, object?>> Entries(Dictionary<string, object?> section, string action) =>
        section.TryGetValue(action, out var value) switch
        {
            true when value is List<object?> list => list.OfType<Dictionary<string, object?>>(),
            true when value is Dictionary<string, object?> single => [single],
            _ => [],
        };

    private static string? Str(Dictionary<string, object?> entry, string key) =>
        entry.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static Dictionary<string, object?>? FindSection(Dictionary<string, object?> node, string name)
    {
        if (node.TryGetValue(name, out var direct) && direct is Dictionary<string, object?> hit) return hit;
        foreach (var child in node.Values)
        {
            if (child is Dictionary<string, object?> obj && FindSection(obj, name) is { } found) return found;
        }
        return null;
    }
}
