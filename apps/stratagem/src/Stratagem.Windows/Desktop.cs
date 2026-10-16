using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Win32;
using Stratagem.Core.Game;

namespace Stratagem.Windows;

/// <summary>Start with Windows (HKCU\...\Run, no admin rights needed).</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Stratagem";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string value && value.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(Name, $"\"{Environment.ProcessPath}\" --autostart");
            else key.DeleteValue(Name, throwOnMissingValue: false);
        }
    }
}

/// <summary>
/// Finds the game's input_settings.config through Steam (registry), reads it and watches it, so a changed
/// binding in the game is picked up without a restart.
/// </summary>
public sealed class GameBindingsSource : IDisposable
{
    private readonly Action<string> _log;
    private FileSystemWatcher? _watcher;
    private string? _watched;
    private System.Threading.Timer? _debounce;

    public GameBindingsSource(Action<string> log) => _log = log;

    /// <summary>The file changed (debounced, from a thread-pool thread).</summary>
    public event Action? Changed;

    public BindingsResult? Read()
    {
        var steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        var activeUser = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null) is int id && id > 0 ? (uint?)id : null;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var files = GameInputSettings.Locate(steamPath?.Replace('/', '\\'), activeUser, appData);
        if (files.Count == 0) return null;
        var file = files[0];
        Watch(file);
        return GameInputSettings.Read(ReadShared(file), file);
    }

    // Steam/the game may be writing the file right now: read shared, retry briefly.
    private static string ReadShared(string file)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100);
            }
        }
    }

    private void Watch(string file)
    {
        if (string.Equals(_watched, file, StringComparison.OrdinalIgnoreCase)) return;
        _watcher?.Dispose();
        _watched = file;
        try
        {
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(file)!, Path.GetFileName(file))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => Debounce();
            _watcher.Created += (_, _) => Debounce();
            _watcher.Renamed += (_, _) => Debounce();
        }
        catch (Exception e) { _log($"Spieleinstellungen werden nicht überwacht: {e.Message}"); }
    }

    private void Debounce()
    {
        _debounce?.Dispose();
        _debounce = new System.Threading.Timer(_ => Changed?.Invoke(), null, 1000, Timeout.Infinite);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}

/// <summary>Log file (stratagem.log) written in the background: the input hooks must never wait for the disk.</summary>
public sealed class FileLog : IDisposable
{
    private readonly string _path;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 2000);
    private readonly Thread _thread;
    private const long MaxBytes = 1_000_000;

    public FileLog(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _thread = new Thread(Write) { IsBackground = true, Name = "Stratagem log" };
        _thread.Start();
    }

    public void Log(string text)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {text}";
        Debug.WriteLine(line);
        _queue.TryAdd(line); // never blocks; drops lines if the disk hangs
    }

    private void Write()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes) File.Move(_path, _path + ".old", overwrite: true);
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (Exception) { /* no log is better than a crash */ }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
