using System.Reflection;
using Stratagem.Core.Game;
using Stratagem.Core.Input;
using Stratagem.Core.Model;
using Stratagem.Core.Radial;
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
    /// <summary>The screen reader, once it exists. Null = only the manual list.</summary>
    IStratagemSource? ScreenSource = null);

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
        Radial = new RadialController(platform.RadialView, () => Items, (_, item) => Activate(item, "Radialmenü"), Inject);
        Router = new InputRouter(Radial, Runner, Physical, () => IsActive, OnHotkey);

        platform.Game.Changed += OnGameChanged;
        if (platform.ScreenSource is { } screen) screen.Changed += RaiseChanged;
        Apply(settings);
    }

    public MacroRunner Runner { get; }
    public RadialController Radial { get; }
    public InputRouter Router { get; }
    public PhysicalKeyState Physical { get; }
    public StratagemCatalog Catalog => _catalog;

    /// <summary>Fires on every visible change (list, game focus, settings, macro finished). From any thread.</summary>
    public event Action? Changed;

    public AppSettings Settings
    {
        get { lock (_gate) return SettingsStore.Clone(_settings); }
    }

    public GameBindings Bindings
    {
        get { lock (_gate) return _bindings.Bindings; }
    }

    /// <summary>Hotkeys and the radial menu act only now.</summary>
    public bool IsActive
    {
        get
        {
            lock (_gate) return !_settings.Paused && (_platform.Game.IsFocused || !_settings.OnlyWhenGameFocused);
        }
    }

    public IReadOnlyList<StratagemItem> Items
    {
        get
        {
            lock (_gate) return UseScreen ? _platform.ScreenSource!.Current : _manual;
        }
    }

    private bool UseScreen => _settings.List.Source == ListSource.Screen && _platform.ScreenSource is not null;

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

    public bool Activate(StratagemItem item, string origin)
    {
        if (!IsActive)
        {
            Log($"{origin}: {item.Label} ignoriert – {(Settings.Paused ? "pausiert" : "Spiel nicht im Vordergrund")}");
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
            var items = UseScreen ? _platform.ScreenSource!.Current : _manual;
            string? problem = UseScreen ? _platform.ScreenSource!.Problem : null;
            if (_settings.List.Source == ListSource.Screen && _platform.ScreenSource is null)
                problem = "Bildschirmerkennung gibt es noch nicht – die eigene Liste wird verwendet.";
            return new StateSnapshot(
                ApiVersion,
                AppVersion,
                _settings.Paused,
                !_settings.Paused && (_platform.Game.IsFocused || !_settings.OnlyWhenGameFocused),
                Runner.IsRunning,
                new GameInfo(_platform.Game.IsRunning, _platform.Game.IsFocused, _settings.Game.WindowTitle, _settings.OnlyWhenGameFocused),
                new BindingsInfo(b.Menu.Name, b.MenuMode.ToString(), b.Up.Name, b.Down.Name, b.Left.Name, b.Right.Name, _bindings.SourceFile, _bindings.Notes),
                new ListInfo(_settings.List.Source.ToString(), problem,
                    items.Select((it, i) => new ItemInfo(i, it.Label, it.Name, it.Code.Letters, it.Code.Arrows, it.Available, it.Icon is not null)).ToList()),
                _events.ToList());
        }
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
        Radial.Options = settings.ToRadialOptions();
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
        if (!_platform.Game.IsFocused) Runner.Cancel(); // never type into another window
        RaiseChanged();
    }

    private void UpdateEnabled()
    {
        bool enabled;
        lock (_gate) enabled = _settings.Radial.Enabled;
        Radial.Enabled = enabled && IsActive;
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
