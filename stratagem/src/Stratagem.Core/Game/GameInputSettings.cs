using Stratagem.Core.Input;

namespace Stratagem.Core.Game;

/// <summary>Result of reading the game's key bindings. Notes explain what could not be used (shown in the web UI).</summary>
public sealed record BindingsResult(GameBindings Bindings, string? SourceFile, IReadOnlyList<string> Notes);

/// <summary>
/// Reads the stratagem bindings from the game's <c>input_settings.config</c>
/// (Steam\userdata\&lt;id&gt;\553850\remote\). The file only exists once the player changed a binding;
/// without it the game defaults apply.
/// Known structure (from community tools, not documented by Arrowhead):
/// <code>
/// Stratagem = {
///     Up = [ { device_type = "Keyboard"  input = "w"  trigger = "Press" ... } ]
///     Start = [ { ... trigger = "Hold" } ]   // open the stratagem list
/// }
/// </code>
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

        var section = FindSection(root, "Stratagem");
        if (section is null)
        {
            notes.Add("Kein Abschnitt 'Stratagem' gefunden – Standardbelegung des Spiels wird verwendet.");
            return new BindingsResult(GameBindings.Default, sourceFile, notes);
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

        return new BindingsResult(new GameBindings(menu, mode, up, down, left, right), sourceFile, notes);
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
            if (input is not null && KeyNames.FromGameName(input) is null)
                notes.Add($"Unbekannter Tastenname '{input}' bei Stratagem '{action}'.");
        }
    }

    private static IEnumerable<(Dictionary<string, object?> Entry, Key Key)> KeyboardEntries(Dictionary<string, object?> section, string action)
    {
        foreach (var entry in Entries(section, action))
        {
            var device = Str(entry, "device_type");
            if (device is not null && !device.Equals("Keyboard", StringComparison.OrdinalIgnoreCase)) continue;
            if (KeyNames.FromGameName(Str(entry, "input")) is { } key) yield return (entry, key);
        }
    }

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
