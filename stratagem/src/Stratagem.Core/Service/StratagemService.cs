using System.Reflection;
using Stratagem.Core.Game;
using Stratagem.Core.Input;
using Stratagem.Core.Model;
using Stratagem.Core.Radial;
using Stratagem.Core.Recognition;
using Stratagem.Core.Settings;

namespace Stratagem.Core.Service;

/// <summary>Platform parts the service needs (Windows implementations in Stratagem.Windows).</summary>
public sealed record ServicePlatform(
    IInputOutput Output,
    IRadialView RadialView,
    IGameWatcher Game,
    IPrecisionTimer? Timer = null,
    /// <summary>Finds and reads the game's input_settings.config; null if not found.</summary>
    Func<BindingsResult?>? ReadGameBindings = null,
    /// <summary>Screen capture of the game (for reading its stratagem list). Null = only the manual list.</summary>
    IScreenCapture? Capture = null,
    /// <summary>Where learned icons are stored (icons.json). Null = not stored.</summary>
    string? DataDir = null);

/// <summary>
/// The core of the tool: holds settings, bindings and the current list, and is the only place that
/// starts macros. Every trigger (radial menu, hotkeys, web/phone, Stream Deck) ends up in <see cref="Activate(StratagemItem, string)"/>.
/// </summary>
public sealed class StratagemService : IDisposable
{
    public const int ApiVersion = 1;

    private readonly SettingsStore _store;
    private readonly ServicePlatform _platform;
    private readonly StratagemCatalog _catalog;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly LinkedList<EventInfo> _events = new();

    private AppSettings _settings;
    private BindingsResult _bindings = new(GameBindings.Default, null, []);
    private IReadOnlyList<StratagemItem> _manual = [];

    public StratagemService(SettingsStore store, AppSettings settings, ServicePlatform platform, StratagemCatalog? catalog = null, Action<string>? log = null)
    {
        _store = store;
        _settings = settings;
        _platform = platform;
        _catalog = catalog ?? StratagemCatalog.Default;
        _log = log ?? (_ => { });

        Physical = new PhysicalKeyState();
        Runner = new MacroRunner(platform.Output, Physical, platform.Timer, _log);
        Radial = new RadialController(platform.RadialView, () => RadialItems, (_, item) => ActivateFromRadial(item), Inject);
        Router = new InputRouter(Radial, Runner, Physical, () => IsActive, OnHotkey);
        Radial.Opened += OnRadialOpened;
        Radial.Closed += OnRadialClosed;
        if (platform.Capture is not null)
        {
            _screen = new ScreenStratagemSource(
                new IconMemory(platform.DataDir is { } dir ? Path.Combine(dir, "icons.json") : null, _log), _catalog);
            _screen.Changed += OnListChanged;
        }

        platform.Game.Changed += OnGameChanged;
        Apply(settings);
    }

    public MacroRunner Runner { get; }
    public RadialController Radial { get; }
    public InputRouter Router { get; }
    public PhysicalKeyState Physical { get; }
    public StratagemCatalog Catalog => _catalog;

    /// <summary>Fires on every visible change (list, game focus, settings, macro finished). From any thread.</summary>
    public event Action? Changed;

    /// <summary>
    /// <see cref="IsActive"/> changed: the Windows side installs its input hooks only while active, so outside
    /// the game (or paused) no keyboard or mouse event passes through this tool at all.
    /// </summary>
    public event Action<bool>? ActiveChanged;

    /// <summary>A copy to edit and pass to <see cref="UpdateSettings"/>.</summary>
    public AppSettings Settings
    {
        get { lock (_gate) return SettingsStore.Clone(_settings); }
    }

    /// <summary>The current settings, shared and read-only by convention (no copy: for frequent reads). Never modify.</summary>
    public AppSettings Current => Volatile.Read(ref _settings);

    public GameBindings Bindings
    {
        get { lock (_gate) return _bindings.Bindings; }
    }

    /// <summary>Hotkeys and the radial menu act only now. Lock-free: read on the hook thread.</summary>
    public bool IsActive => _active;

    private volatile bool _active;

    /// <summary>The list in the game's order (web UI, phone deck, "place n" hotkeys).</summary>
    public IReadOnlyList<StratagemItem> Items
    {
        get
        {
            lock (_gate) return UseScreen ? _screen!.Current : _manual;
        }
    }

    /// <summary>
    /// The radial menu's places. For the list read from the game every stratagem keeps its direction
    /// (with empty places); the own list is shown as it is. Same instance until something changes.
    /// </summary>
    public IReadOnlyList<StratagemItem> RadialItems
    {
        get
        {
            lock (_gate) return _radialItems;
        }
    }

    private readonly ScreenStratagemSource? _screen;
    private readonly RadialSlots _slots = new();
    private IReadOnlyList<StratagemItem> _radialItems = [];
    private int _menuOpenedByUs;

    private bool UseScreen => _settings.List.Source == ListSource.Screen && _screen is not null;

    public void UpdateSettings(AppSettings settings)
    {
        var copy = SettingsStore.Clone(settings);
        foreach (var fix in copy.Normalize()) _log($"Einstellung korrigiert: {fix}");
        _store.Save(copy);
        Apply(copy);
        Log("Einstellungen gespeichert");
    }

    public void SetPaused(bool paused)
    {
        var s = Settings;
        s.Paused = paused;
        UpdateSettings(s);
    }

    /// <summary>Re-reads the game's key bindings (called when input_settings.config changed).</summary>
    public void ReloadBindings()
    {
        BindingsResult result;
        lock (_gate)
        {
            result = ResolveBindings(_settings);
            _bindings = result;
        }
        Log($"Tastenbelegung: Menü {result.Bindings.Menu} ({result.Bindings.MenuMode}), " +
            $"Richtungen {result.Bindings.Up}/{result.Bindings.Down}/{result.Bindings.Left}/{result.Bindings.Right}" +
            (result.SourceFile is null ? " (eingestellt)" : " (aus dem Spiel)"));
        foreach (var note in result.Notes) Log(note);
        RaiseChanged();
    }

    public bool Activate(int slot, string origin)
    {
        var items = Items;
        if (slot < 0 || slot >= items.Count)
        {
            Log($"{origin}: Platz {slot + 1} ist leer");
            return false;
        }
        return Activate(items[slot], origin);
    }

    public bool Activate(StratagemCode code, string origin) =>
        Activate(new StratagemItem(code, _catalog.NameOf(code)), origin);

    /// <summary>
    /// Selection in the radial menu. If the game's list was opened when the menu appeared (to read it), only the
    /// arrows are typed, right after the reading (same queue).
    /// </summary>
    private bool ActivateFromRadial(StratagemItem item)
    {
        var opened = Interlocked.Exchange(ref _menuOpenedByUs, 0) == 1;
        if (!opened) return Activate(item, "Radialmenü");
        if (!IsActive || !item.Available)
        {
            CloseOpenedMenu();
            return Activate(item, "Radialmenü"); // logs why
        }
        GameBindings bindings;
        MacroTiming timing;
        lock (_gate)
        {
            bindings = _bindings.Bindings;
            timing = _settings.ToTiming();
        }
        Log($"Radialmenü: {item.Label} ({item.Code.Arrows})");
        Runner.Enqueue(MacroPlanner.Plan(item.Code, bindings, timing, menuAlreadyOpen: true), item.Label)
            .ContinueWith(t => { if (!t.Result) Log($"{item.Label} abgebrochen"); RaiseChanged(); }, TaskScheduler.Default);
        return true;
    }

    private void OnRadialOpened()
    {
        if (!UseScreen || !IsActive || _platform.Capture is null) return;
        GameBindings bindings;
        MacroTiming timing;
        int readMs;
        lock (_gate)
        {
            bindings = _bindings.Bindings;
            timing = _settings.ToTiming();
            readMs = _settings.Timing.ListReadMs;
        }
        Interlocked.Exchange(ref _menuOpenedByUs, 1);
        Runner.Enqueue(MacroPlanner.OpenMenu(bindings, timing, readMs, ReadScreen), "Liste öffnen", MacroPlanner.KeepHeld(bindings));
    }

    private void OnRadialClosed(bool selected)
    {
        if (!selected) CloseOpenedMenu();
    }

    private void CloseOpenedMenu()
    {
        if (Interlocked.Exchange(ref _menuOpenedByUs, 0) == 0) return;
        GameBindings bindings;
        MacroTiming timing;
        lock (_gate)
        {
            bindings = _bindings.Bindings;
            timing = _settings.ToTiming();
        }
        Runner.Enqueue(MacroPlanner.CloseMenu(bindings, timing), "Liste schließen");
    }

    /// <summary>On the macro thread, while the game's list is open: read it.</summary>
    private void ReadScreen()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        double scale = 1;
        var image = _platform.Capture?.CaptureMenuArea(out scale);
        if (image is null)
        {
            Log("Bildschirm nicht lesbar (Spiel nicht im Vordergrund oder Aufnahme blockiert).");
            return;
        }
        var rows = MenuReader.Read(image, scale * Current.List.ScreenScale);
        _screen!.Update(rows);
        if (watch.ElapsedMilliseconds > 30) _log($"Liste gelesen in {watch.ElapsedMilliseconds} ms ({rows.Count} Zeilen)");
    }

    /// <summary>Reads the game's list from a picture (diagnosis: uploaded screenshot). Returns the rows found.</summary>
    public IReadOnlyList<MenuRow> ReadPicture(PixelImage image, double scale)
    {
        var rows = MenuReader.Read(image, scale);
        _screen?.Update(rows);
        return rows;
    }

    private void OnListChanged()
    {
        UpdateRadialItems();
        Radial.Refresh(RadialItems);
        RaiseChanged();
    }

    private void UpdateRadialItems()
    {
        lock (_gate)
        {
            if (UseScreen)
            {
                _slots.MinSlots = _settings.Radial.ScreenSlots;
                var arranged = _slots.Arrange(_screen!.Current);
                if (!arranged.SequenceEqual(_radialItems, ReferenceEqualityComparer.Instance)) _radialItems = arranged;
            }
            else _radialItems = _manual;
        }
    }

    public bool Activate(StratagemItem item, string origin)
    {
        if (!IsActive)
        {
            Log($"{origin}: {item.Label} ignoriert – {(Current.Paused ? "pausiert" : "Spiel nicht im Vordergrund")}");
            return false;
        }
        if (!item.Available)
        {
            Log($"{origin}: {item.Label} ist gerade nicht verfügbar");
            return false;
        }
        GameBindings bindings;
        MacroTiming timing;
        lock (_gate)
        {
            bindings = _bindings.Bindings;
            timing = _settings.ToTiming();
        }
        var steps = MacroPlanner.Plan(item.Code, bindings, timing);
        if (!Runner.TryRun(steps, item.Label, out var done))
        {
            Log($"{origin}: {item.Label} verworfen – es läuft schon ein Stratagem");
            return false;
        }
        Log($"{origin}: {item.Label} ({item.Code.Arrows})");
        RaiseChanged();
        done.ContinueWith(t =>
        {
            if (!t.Result) Log($"{item.Label} abgebrochen");
            RaiseChanged();
        }, TaskScheduler.Default);
        return true;
    }

    public StateSnapshot GetState()
    {
        lock (_gate)
        {
            var b = _bindings.Bindings;
            var items = UseScreen ? _screen!.Current : _manual;
            string? problem = UseScreen ? _screen!.Problem : null;
            if (_settings.List.Source == ListSource.Screen && _screen is null)
                problem = "Bildschirm lesen geht hier nicht – die eigene Liste wird verwendet.";
            return new StateSnapshot(
                ApiVersion,
                AppVersion,
                _settings.Paused,
                _active,
                Runner.IsRunning,
                new GameInfo(_platform.Game.IsRunning, _platform.Game.IsFocused, _settings.Game.WindowTitle, _settings.OnlyWhenGameFocused),
                new BindingsInfo(b.Menu.Name, b.MenuMode.ToString(), b.Up.Name, b.Down.Name, b.Left.Name, b.Right.Name, _bindings.SourceFile, _bindings.Notes,
                    Conflicts(_settings, _bindings)),
                new ListInfo(_settings.List.Source.ToString(), problem,
                    items.Select((it, i) => new ItemInfo(i, it.Label, it.Name, it.Code.Letters, it.Code.Arrows, it.Available, it.Icon is not null)).ToList()),
                _events.ToList());
        }
    }

    /// <summary>
    /// Keys/buttons of this tool that the game also uses: there they would no longer reach the game.
    /// The trigger on the game's "open stratagem list" key is fine on purpose (the tool passes it on).
    /// Only bindings the player changed are known (the file has no defaults).
    /// </summary>
    private static IReadOnlyList<ConflictInfo> Conflicts(AppSettings settings, BindingsResult bindings)
    {
        if (bindings.AllBindings.Count == 0) return [];
        var mine = new List<(InputRef Input, string By)>();
        if (settings.Radial.Enabled && InputRef.TryParse(settings.Radial.Trigger, out var trigger)) mine.Add((trigger, "radial"));
        mine.AddRange(settings.ToHotkeys().Keys.Select(k => (k, "hotkey")));
        var result = new List<ConflictInfo>();
        foreach (var (input, by) in mine)
        {
            foreach (var g in bindings.AllBindings)
            {
                if (g.Input != input) continue;
                if (by == "radial" && g.Section == "Stratagem" && g.Action == "Start") continue;
                result.Add(new ConflictInfo(input.Name, by, g.Section, g.Action, g.Trigger));
            }
        }
        return result;
    }

    /// <summary>Icon of a list entry (PNG), if the source provides one.</summary>
    public byte[]? IconOf(int index)
    {
        var items = Items;
        return index >= 0 && index < items.Count ? items[index].Icon : null;
    }

    public static string AppVersion { get; } =
        typeof(StratagemService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public void Log(string text)
    {
        _log(text);
        lock (_gate)
        {
            _events.AddFirst(new EventInfo(DateTimeOffset.Now, text));
            while (_events.Count > 40) _events.RemoveLast();
        }
        RaiseChanged();
    }

    private void Apply(AppSettings settings)
    {
        lock (_gate)
        {
            _settings = settings;
            _manual = settings.ToManualItems(_catalog);
            _bindings = ResolveBindings(settings);
        }
        UpdateRadialItems();
        var options = settings.ToRadialOptions();
        if (Radial.Options != options) Radial.Options = options; // setting it closes an open menu
        Router.SetHotkeys(settings.ToHotkeys());
        UpdateEnabled();
        RaiseChanged();
    }

    private BindingsResult ResolveBindings(AppSettings settings)
    {
        if (settings.Game.ReadBindingsFromGame && _platform.ReadGameBindings is { } read)
        {
            try
            {
                if (read() is { } fromGame) return fromGame;
                return new BindingsResult(settings.ToManualBindings(), null,
                    ["Keine Tastenbelegung des Spiels gefunden (input_settings.config gibt es erst, wenn im Spiel eine Taste geändert wurde) – die eingestellte Belegung wird verwendet."]);
            }
            catch (Exception e)
            {
                return new BindingsResult(settings.ToManualBindings(), null, [$"Tastenbelegung des Spiels nicht lesbar: {e.Message}"]);
            }
        }
        return new BindingsResult(settings.ToManualBindings(), null, []);
    }

    private void OnGameChanged()
    {
        UpdateEnabled();
        if (!_platform.Game.IsFocused)
        {
            Interlocked.Exchange(ref _menuOpenedByUs, 0);
            Runner.Cancel(); // never type into another window; also releases a held list key
        }
        RaiseChanged();
    }

    private void UpdateEnabled()
    {
        bool active, radial, changed;
        lock (_gate) // game focus and settings change on different threads
        {
            active = !_settings.Paused && (_platform.Game.IsFocused || !_settings.OnlyWhenGameFocused);
            radial = _settings.Radial.Enabled;
            changed = active != _active;
            _active = active;
        }
        Radial.Enabled = radial && active;
        if (changed) ActiveChanged?.Invoke(active);
    }

    private void OnHotkey(HotkeyAction action)
    {
        if (action.Code is { } code) Activate(code, "Taste");
        else if (action.Slot is { } slot) Activate(slot, "Taste");
    }

    private void Inject(InputRef input, bool down)
    {
        if (input.Key is { } key) _platform.Output.Send(key, down);
        else if (input.Mouse is { } button) _platform.Output.Send(button, down);
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception e) { _log($"Fehler bei Aktualisierung: {e.Message}"); }
    }

    public void Dispose()
    {
        _platform.Game.Changed -= OnGameChanged;
        Radial.Enabled = false;
        Runner.Dispose();
    }
}
