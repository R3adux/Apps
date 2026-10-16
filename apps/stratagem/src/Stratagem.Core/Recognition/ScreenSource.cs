using System.Globalization;
using System.Text.Json;
using Stratagem.Core.Model;
using Stratagem.Core.Service;

namespace Stratagem.Core.Recognition;

/// <summary>Takes a picture of the area of the game window where the stratagem list is (Windows: screen capture).</summary>
public interface IScreenCapture
{
    /// <summary>
    /// The top-left area of the game window, or null if not possible (game not in front, capture blocked).
    /// <paramref name="scale"/>: game window height / 1080.
    /// </summary>
    PixelImage? CaptureMenuArea(out double scale);
}

/// <summary>
/// Remembers which icon belongs to which code. The game shows the arrows only while a stratagem can be called;
/// once seen, the code is known for its icon, also during the cooldown and in later sessions (stored as JSON).
/// </summary>
public sealed class IconMemory
{
    private readonly string? _path;
    private readonly Action<string> _log;
    private readonly List<(ulong Hash, StratagemCode Code)> _entries = [];
    private readonly object _gate = new();

    public IconMemory(string? path = null, Action<string>? log = null)
    {
        _path = path;
        _log = log ?? (_ => { });
        Load();
    }

    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>Records a code seen with its icon. True if this was new.</summary>
    public bool Learn(ulong hash, StratagemCode code)
    {
        lock (_gate)
        {
            if (_entries.Any(e => e.Code.Equals(code) && MenuReader.Distance(e.Hash, hash) <= 4)) return false;
            _entries.Add((hash, code));
            if (_entries.Count > 500) _entries.RemoveAt(0);
        }
        Save();
        return true;
    }

    /// <summary>The code of the nearest known icon, if it is close enough to be the same stratagem.</summary>
    /// <remarks>
    /// Strict on purpose (similar icons, e.g. two eagles, differ by ~12 bits; the same icon in another state by
    /// up to ~10): an unknown entry is only left out for a moment, a wrong one would show the wrong stratagem.
    /// </remarks>
    public StratagemCode? Find(ulong hash, int maxDistance = 9, IReadOnlySet<StratagemCode>? exclude = null)
    {
        lock (_gate)
        {
            StratagemCode? best = null;
            var bestDistance = int.MaxValue;
            foreach (var (h, code) in _entries)
            {
                if (exclude?.Contains(code) == true) continue;
                var d = MenuReader.Distance(h, hash);
                if (d < bestDistance) { bestDistance = d; best = code; }
            }
            return bestDistance <= maxDistance ? best : null;
        }
    }

    private sealed record Stored(string Hash, string Code);

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var stored = JsonSerializer.Deserialize<List<Stored>>(File.ReadAllText(_path)) ?? [];
            foreach (var s in stored)
                if (ulong.TryParse(s.Hash, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) && StratagemCode.TryParse(s.Code, out var c))
                    _entries.Add((h, c));
        }
        catch (Exception e) { _log($"Gemerkte Icons nicht lesbar ({e.Message}), es wird neu gelernt."); }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            List<Stored> copy;
            lock (_gate) copy = _entries.Select(e => new Stored(e.Hash.ToString("x16"), e.Code.Letters)).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(copy));
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch (Exception e) { _log($"Gemerkte Icons nicht gespeichert: {e.Message}"); }
    }
}

/// <summary>
/// The list read from the game screen. Feed it with <see cref="Update"/> each time the list was read.
/// Rows without a known code (never seen with arrows) are left out until they are.
/// </summary>
public sealed class ScreenStratagemSource : IStratagemSource
{
    private readonly IconMemory _memory;
    private readonly Func<StratagemCode, string?> _nameOf;
    private readonly string? _lastPath;
    private readonly object _gate = new();
    private IReadOnlyList<StratagemItem> _current = [];
    private string? _problem = "Noch nicht gelesen – das Radialmenü einmal im Spiel öffnen.";
    private string _signature = "";

    /// <param name="nameOf">Display name for a code (own names, catalog); default: the built-in catalog.</param>
    /// <param name="lastPath">Where the last reading is kept (lastlist.json): after a restart the deck and the radial
    /// menu show it until the game's list is read again.</param>
    public ScreenStratagemSource(IconMemory memory, Func<StratagemCode, string?>? nameOf = null, string? lastPath = null)
    {
        _memory = memory;
        _nameOf = nameOf ?? StratagemCatalog.Default.NameOf;
        _lastPath = lastPath;
        LoadLast();
    }

    private sealed record StoredItem(string Code, bool Available, string? Icon);

    private void LoadLast()
    {
        if (_lastPath is null || !File.Exists(_lastPath)) return;
        try
        {
            var stored = JsonSerializer.Deserialize<List<StoredItem>>(File.ReadAllText(_lastPath)) ?? [];
            var items = stored
                .Where(i => StratagemCode.TryParse(i.Code, out _))
                .Select(i =>
                {
                    var code = StratagemCode.Parse(i.Code);
                    return new StratagemItem(code, _nameOf(code), i.Available, i.Icon is null ? null : Convert.FromBase64String(i.Icon));
                })
                .ToList();
            if (items.Count == 0) return;
            _current = items;
            _problem = "Stand vom letzten Lesen – wird beim nächsten Öffnen des Radialmenüs im Spiel aktualisiert.";
        }
        catch (Exception) { /* a broken file is simply ignored and overwritten */ }
    }

    private void SaveLast(IReadOnlyList<StratagemItem> items)
    {
        if (_lastPath is null) return;
        try
        {
            var stored = items.Select(i => new StoredItem(i.Code.Letters, i.Available, i.Icon is null ? null : Convert.ToBase64String(i.Icon)));
            Directory.CreateDirectory(Path.GetDirectoryName(_lastPath)!);
            File.WriteAllText(_lastPath + ".tmp", JsonSerializer.Serialize(stored));
            File.Move(_lastPath + ".tmp", _lastPath, overwrite: true);
        }
        catch (Exception) { /* only a convenience */ }
    }

    public string Name => "Bildschirm";
    public IReadOnlyList<StratagemItem> Current { get { lock (_gate) return _current; } }
    public string? Problem { get { lock (_gate) return _problem; } }
    public event Action? Changed;

    /// <summary>Takes a reading. Returns true if the list changed.</summary>
    public bool Update(IReadOnlyList<MenuRow> rows)
    {
        if (rows.Count == 0)
        {
            SetProblem("Stratagem-Liste im Bild nicht gefunden (Randlos-Modus? HUD-Größe?). Die letzte gelesene Liste bleibt.");
            return false;
        }
        // Codes shown as arrows are certain; a stratagem appears only once in the list, so a remembered code
        // that is shown as arrows in another row cannot belong to this one.
        var shown = rows.Where(r => r.Code is not null).Select(r => r.Code!).ToHashSet();
        foreach (var row in rows) if (row.Code is { } seen) _memory.Learn(row.IconHash, seen);
        var items = new List<StratagemItem>();
        var used = new HashSet<StratagemCode>(shown);
        foreach (var row in rows)
        {
            var code = row.Code ?? _memory.Find(row.IconHash, exclude: used);
            if (code is null) continue;
            used.Add(code);
            items.Add(new StratagemItem(code, _nameOf(code), row.State == RowState.Callable, row.Icon));
        }
        // Icons change pixel by pixel all the time (cooldown fill): compare what matters.
        var signature = string.Join(",", items.Select(i => $"{i.Code.Letters}:{i.Available}"));
        bool changed;
        lock (_gate)
        {
            changed = signature != _signature || _problem is not null;
            _signature = signature;
            _current = items; // always: the icons are fresher
            _problem = null;
        }
        if (changed)
        {
            SaveLast(items); // only when the list changed, not on every reading
            Changed?.Invoke();
        }
        return changed;
    }

    /// <summary>Names changed (own names): show them now, not only after the next reading.</summary>
    public void RefreshNames()
    {
        lock (_gate)
        {
            if (_current.Count == 0) return;
            _current = _current.Select(i => i with { Name = _nameOf(i.Code) }).ToList();
        }
        Changed?.Invoke();
    }

    private void SetProblem(string problem)
    {
        lock (_gate)
        {
            if (_problem == problem) return;
            _problem = problem;
        }
        Changed?.Invoke();
    }
}

/// <summary>
/// Gives each stratagem a fixed place in the radial menu, so a direction stays a direction although the game's
/// list changes during a mission ("Eagle Rearm" appears after the first eagle, mission stratagems come and go).
/// At least <see cref="MinSlots"/> places; a new stratagem takes a free place, a place of a stratagem that is no
/// longer in the list is only reused when no place is free.
/// </summary>
public sealed class RadialSlots(int minSlots = 8)
{
    private readonly List<string?> _slots = []; // code letters per place
    private readonly object _gate = new();

    public int MinSlots { get; set; } = minSlots;

    public IReadOnlyList<StratagemItem> Arrange(IReadOnlyList<StratagemItem> items)
    {
        lock (_gate)
        {
            while (_slots.Count < Math.Max(MinSlots, items.Count)) _slots.Add(null);
            var present = items.Select(i => i.Code.Letters).ToHashSet();
            foreach (var item in items)
            {
                var key = item.Code.Letters;
                if (_slots.Contains(key)) continue;
                var free = _slots.IndexOf(null);
                if (free < 0) free = _slots.FindIndex(k => k is null || !present.Contains(k));
                if (free < 0) { _slots.Add(null); free = _slots.Count - 1; }
                _slots[free] = key;
            }
            var byKey = items.GroupBy(i => i.Code.Letters).ToDictionary(g => g.Key, g => g.First());
            return _slots.Select(k => k is not null && byKey.TryGetValue(k, out var item) ? item : StratagemItem.Empty).ToList();
        }
    }
}
