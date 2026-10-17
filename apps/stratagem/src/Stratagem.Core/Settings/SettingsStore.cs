using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stratagem.Core.Settings;

/// <summary>Loads/saves settings.json. A broken file is kept as settings.broken.json and defaults are used.</summary>
public sealed class SettingsStore(string path, Action<string>? log = null)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _gate = new();
    private readonly Action<string> _log = log ?? (_ => { });

    public string Path => path;

    public AppSettings Load()
    {
        lock (_gate)
        {
            AppSettings settings;
            if (!File.Exists(path))
            {
                settings = new AppSettings();
                settings.Normalize();
                SaveLocked(settings);
                return settings;
            }
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
            }
            catch (Exception e)
            {
                var broken = System.IO.Path.ChangeExtension(path, ".broken.json");
                File.Copy(path, broken, overwrite: true);
                _log($"Einstellungen nicht lesbar ({e.Message}), gesichert als {broken}. Standardwerte werden verwendet.");
                settings = new AppSettings();
            }
            var fixes = settings.Normalize();
            foreach (var fix in fixes) _log($"Einstellung korrigiert: {fix}");
            if (fixes.Count > 0) SaveLocked(settings); // repaired and migrated once, not on every start
            return settings;
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate) SaveLocked(settings);
    }

    private void SaveLocked(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
        File.Move(temp, path, overwrite: true); // atomic: never a half-written file after a crash
    }

    /// <summary>Deep copy via JSON (settings are edited as a copy, then swapped in).</summary>
    public static AppSettings Clone(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, Json), Json)!;
}
