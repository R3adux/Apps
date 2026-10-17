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
        Radial.KeepTriggerOnChord = KeepTriggerOnChord;
        if (platform.Capture is not null)
        {
            _screen = new ScreenStratagemSource(
                new IconMemory(platform.DataDir is { } dir ? Path.Combine(dir, "icons.json") : null, _log),
                code => Current.NameOf(code, _catalog),
                platform.DataDir is { } data ? Path.Combine(data, "lastlist.json") : null);
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

    /// <summary>
    /// The places in a stable order (web UI, phone deck, "place n" hotkeys). For the list read from the game these
    /// are the radial menu's places, empty ones included: "place 3" stays the same stratagem during a mission,
    /// although the game inserts "Eagle Rearm" mid-list after the first eagle.
    /// </summary>
    public IReadOnlyList<StratagemItem> Items
    {
        get
        {
            lock (_gate) return _radialItems;
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
        // The file is read outside the lock: the hook thread takes it and must never wait for a disk.
        var result = ResolveBindings(Current);
        lock (_gate) _bindings = result;
        Log($"Tastenbelegung: Menü {result.Bindings.Menu} ({result.Bindings.MenuMode}), " +
            $"Richtungen {result.Bindings.Up}/{result.Bindings.Down}/{result.Bindings.Left}/{result.Bindings.Right}" +
            (result.SourceFile is null ? " (eingestellt)" : " (aus dem Spiel)"));
        foreach (var note in result.Notes) Log(note);
        RaiseChanged();
    }

    public bool Activate(int slot, string origin)
    {
        var items = Items;
        if (slot < 0 || slot >= items.Count || items[slot].IsEmpty)
        {
            Log($"{origin}: Platz {slot + 1} ist leer");
            return false;
        }
        return Activate(items[slot], origin);
    }

    public bool Activate(StratagemCode code, string origin) =>
        Activate(new StratagemItem(code, _catalog.NameOf(code)), origin);

    // --- the game's list --------------------------------------------------------------------------
    //
    // Reading the list from the screen needs it open. Hold mode of the radial menu (the default): the list opens
    // with the trigger, like without this tool, and stays open while the menu is; a selection then only types the
    // arrows. Tap mode: the menu stays open while the player walks, so the list is only opened briefly to read it.
    // Before a code is typed, the fresh reading must show it as callable – nothing is typed into a list that
    // did not open (in a vehicle, while swimming), which would make the player walk.

    // Rejected: something row-like was there, but not the list (no code, no known icon) – a closed list in front of a
    // busy scene, or a list nothing of which is known yet. Refuses like "not visible", but may need closing.
    private const int ListVisibleUnknown = 0, ListVisible = 1, ListNotVisible = 2, ListRejected = 3;

    // Macro thread only (set by a reading, used by the next steps of the same sequence).
    private int _listVisible;
    private bool _lastReadingStable;
    private bool _readerWorked; // a list was read in this session: "not found" then really means "not open"
    private int _notFoundInARow;
    private int _unreadableInARow;
    private long _unreadableLoggedAt = long.MinValue / 2;
    private bool _noticeShown;
    private bool _weOpenedList; // press mode: pressing the list key again closes only a list we opened
    private long _lastGoodRead = long.MinValue / 2;

    // Ends the reading of a list the player closed already (hook thread → macro thread).
    private CancellationTokenSource? _openAbort;

    /// <summary>Something the player should know even while playing (shown as a tray message). Once per session.</summary>
    public event Action<string>? Notice;

    private bool CanRead => UseScreen && _platform.Capture is not null;

    /// <summary>
    /// Selection in the radial menu. If the game's list was opened when the menu appeared (to read it), only the
    /// arrows are typed, right after the reading (same queue).
    /// </summary>
    private bool ActivateFromRadial(StratagemItem item)
    {
        var opened = Interlocked.Exchange(ref _menuOpenedByUs, 0) == 1;
        Interlocked.Exchange(ref _openAbort, null); // the reading must finish: the code waits for it
        if (!IsActive || (!item.Available && !CanRead))
        {
            if (opened) CloseOpenedMenu(); // the list must not stay open
            return Activate(item, "Radialmenü"); // logs why
        }
        if (!opened && !CanRead) return Activate(item, "Radialmenü");
        var (bindings, timing, _) = Snapshot();
        Log($"Radialmenü: {item.Label} ({item.Code.Arrows})");
        // Queued, not dropped: it belongs to the reading before it.
        Runner.Enqueue(CodeSteps(item, bindings, timing, menuOpen: opened), item.Label)
            .ContinueWith(_ => RaiseChanged(), TaskScheduler.Default);
        return true;
    }

    private (GameBindings Bindings, MacroTiming Timing, int ReadMs) Snapshot()
    {
        lock (_gate) return (_bindings.Bindings, _settings.ToTiming(), _settings.Timing.ListReadMs);
    }

    private void OnRadialOpened()
    {
        if (!CanRead || !IsActive) return;
        var (bindings, timing, readMs) = Snapshot();
        if (Current.Radial.Mode == RadialMode.HoldRelease)
        {
            Interlocked.Exchange(ref _menuOpenedByUs, 1);
            var abort = new CancellationTokenSource();
            Interlocked.Exchange(ref _openAbort, abort);
            Runner.Enqueue(OpenAndRead(bindings, timing, readMs), "Liste öffnen",
                MacroPlanner.KeepHeld(bindings), abort: abort.Token);
            return;
        }
        // Tap mode: a short look (open, read, close). The player's walking keys wait meanwhile, so they do not
        // end up as arrows in the list. Not again right after a reading.
        if (Environment.TickCount64 - Interlocked.Read(ref _lastGoodRead) < 2000) return;
        Runner.Enqueue([.. OpenAndRead(bindings, timing, readMs), .. CloseList(bindings, timing)],
            "Liste lesen", guard: bindings.DirectionKeys.ToHashSet());
    }

    private void OnRadialClosed(RadialClose close)
    {
        if (close.Reason == RadialCloseReason.Selected) return; // ActivateFromRadial takes over the open list
        if (Interlocked.Exchange(ref _menuOpenedByUs, 0) == 0) return;
        var abort = Interlocked.Exchange(ref _openAbort, null);
        var (bindings, timing, _) = Snapshot();
        switch (close.Reason)
        {
            case RadialCloseReason.Chord when TriggerIsListKey(bindings):
                // The player holds the list key and pressed another key (Ctrl+W: typing a code by hand). The list
                // stays open. Hold mode: the held key is theirs now – the game sees their release later; if they let
                // go before the hand-over runs, it is released then. Press mode: the trigger is not sent again
                // (KeepTriggerOnChord), it would close the list.
                if (bindings.MenuMode == MenuMode.Hold)
                {
                    Physical.Set(bindings.Menu, true);
                    Runner.Enqueue([new MacroStep.HandOver(bindings.Menu)], "Liste übergeben");
                }
                else if (!IsDirection(bindings, close.ChordKey))
                {
                    // Press mode, a shortcut (Ctrl+V): the trigger goes to the game as without the tool (and toggles
                    // the list we opened shut); the reading is not needed any more. The player holds Ctrl: if the
                    // stopped tap releases it, it is pressed again for them (and their own release clears it).
                    Physical.Set(bindings.Menu, true);
                    abort?.Cancel();
                }
                break;
            case RadialCloseReason.Released when bindings.MenuMode == MenuMode.Press && close.OpenMs < 300 && !close.EverHighlighted:
                // A quick tap in "press" mode is how the player opens the game's own list: leave it open.
                break;
            default:
                // Closed without a selection: stop the reading at once (the player may be walking already) and close.
                abort?.Cancel();
                Runner.Enqueue(CloseList(bindings, timing), "Liste schließen");
                break;
        }
    }

    private bool TriggerIsListKey(GameBindings bindings) => Radial.Options.Trigger == InputRef.Of(bindings.Menu);

    /// <summary>
    /// Press mode, the menu opened the list, and the player now types a code by hand (an arrow key): the trigger
    /// must not be sent again, it would toggle the list shut. Any other key (Ctrl+V in the chat) gets its Ctrl.
    /// </summary>
    private bool KeepTriggerOnChord(InputRef other)
    {
        if (Volatile.Read(ref _menuOpenedByUs) != 1) return false;
        var bindings = Snapshot().Bindings;
        return bindings.MenuMode == MenuMode.Press && TriggerIsListKey(bindings) && IsDirection(bindings, other);
    }

    private static bool IsDirection(GameBindings bindings, InputRef? input) =>
        input?.Key is { } key && bindings.DirectionKeys.Contains(key);

    private void CloseOpenedMenu()
    {
        var (bindings, timing, _) = Snapshot();
        Runner.Enqueue(CloseList(bindings, timing), "Liste schließen");
    }

    /// <summary>Opens the game's list and reads it once it should be on screen. Hold mode: the key stays down.</summary>
    private List<MacroStep> OpenAndRead(GameBindings bindings, MacroTiming timing, int readMs)
    {
        var t = timing.Clamped();
        var press = bindings.MenuMode == MenuMode.Press;
        var steps = new List<MacroStep>
        {
            new MacroStep.Run(() => { _listVisible = ListVisibleUnknown; _lastReadingStable = false; _weOpenedList = false; }),
            new MacroStep.Press(bindings.Menu),
        };
        if (press)
        {
            steps.Add(new MacroStep.Wait(t.KeyHoldMs));
            steps.Add(new MacroStep.Release(bindings.Menu));
            steps.Add(new MacroStep.Run(() => _weOpenedList = true)); // toggled open: closing needs another press
        }
        steps.Add(new MacroStep.Wait(Math.Max(t.MenuOpenMs, readMs)));
        steps.Add(new MacroStep.Run(ReadUntilStable));
        // Hold mode: only a job that ran to here keeps the key down; an aborted one released it already.
        if (!press) steps.Add(new MacroStep.Run(() => _weOpenedList = true));
        return steps;
    }

    /// <summary>
    /// Closes a list we opened. Press mode: only if we opened it and it is (or may be) open – pressing again would
    /// open it. Ends with a short pause, so a next press of the list key is a new press for the game.
    /// </summary>
    private List<MacroStep> CloseList(GameBindings bindings, MacroTiming timing)
    {
        var t = timing.Clamped();
        var done = new MacroStep.Run(() => _weOpenedList = false);
        if (bindings.MenuMode == MenuMode.Hold)
            return [new MacroStep.Decide(() => _weOpenedList ? null : [done]), new MacroStep.Release(bindings.Menu), new MacroStep.Wait(t.KeyGapMs), done];
        return
        [
            new MacroStep.Decide(() => !_weOpenedList || (_listVisible == ListNotVisible && _readerWorked) ? [done] : null), // rejected: close
            new MacroStep.Press(bindings.Menu),
            new MacroStep.Wait(t.KeyHoldMs),
            new MacroStep.Release(bindings.Menu),
            new MacroStep.Wait(t.KeyGapMs),
            done,
        ];
    }

    /// <summary>
    /// One stratagem with the list read first: open (unless already open), read, check, type, close.
    /// With the own list (or no screen capture) the plain sequence.
    /// </summary>
    private IReadOnlyList<MacroStep> CodeSteps(StratagemItem item, GameBindings bindings, MacroTiming timing, bool menuOpen)
    {
        var steps = new List<MacroStep>();
        if (!menuOpen)
        {
            if (!CanRead) return MacroPlanner.Plan(item.Code, bindings, timing);
            steps.AddRange(OpenAndRead(bindings, timing, Snapshot().ReadMs));
        }
        if (CanRead) steps.Add(new MacroStep.Decide(() => Verify(item, bindings, timing)));
        steps.AddRange(MacroPlanner.Plan(item.Code, bindings, timing, menuAlreadyOpen: true));
        steps.Add(new MacroStep.Run(() => _weOpenedList = false)); // a complete code closes the list
        return steps;
    }

    /// <summary>
    /// On the macro thread, right before typing: null = type the code; otherwise the steps that close the list
    /// instead (the job then ends). Only refuses on clear evidence – an unclear picture never blocks a code.
    /// </summary>
    private IReadOnlyList<MacroStep>? Verify(StratagemItem item, GameBindings bindings, MacroTiming timing)
    {
        string? why = null;
        if (_listVisible is ListNotVisible or ListRejected && _readerWorked) why = "die Stratagem-Liste ist nicht aufgegangen (z. B. im Fahrzeug)";
        else if (_listVisible == ListVisible && _lastReadingStable)
        {
            var found = _screen!.Find(item.Code);
            if (found is { Available: false }) why = "gerade nicht verfügbar (Abklingzeit)";
            else if (found is null && _screen.UnknownRows == 0) why = "nicht (mehr) in der Liste";
        }
        if (why is null) return null;
        Log($"{item.Label} nicht getippt – {why}");
        // Hold mode: ending the job releases the list key. Press mode: close the list if it is open.
        var t = timing.Clamped();
        var done = new MacroStep.Run(() => _weOpenedList = false);
        return bindings.MenuMode == MenuMode.Press && _listVisible is ListVisible or ListRejected && _weOpenedList
            ? [new MacroStep.Press(bindings.Menu), new MacroStep.Wait(t.KeyHoldMs), new MacroStep.Release(bindings.Menu), new MacroStep.Wait(t.KeyGapMs), done]
            : [done];
    }

    /// <summary>
    /// On the macro thread, while the game's list should be open: read it until two pictures in a row agree
    /// (the list may still be appearing, an explosion may flash behind it). A few dozen milliseconds normally.
    /// </summary>
    private void ReadUntilStable(CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<MenuRow>? previous = null;
        var screenScale = Current.List.ScreenScale;
        var rejected = false;
        while (!ct.IsCancellationRequested)
        {
            var image = _platform.Capture?.CaptureMenuArea(out var scale) is { } captured ? (captured, scale) : default;
            if (image.captured is null)
            {
                // Unknown, not "closed": a black picture (exclusive fullscreen, HDR) proves nothing.
                _listVisible = ListVisibleUnknown;
                _lastReadingStable = false;
                var now = Environment.TickCount64;
                if (now - _unreadableLoggedAt > 30_000)
                {
                    _unreadableLoggedAt = now;
                    Log("Bildschirm nicht lesbar (Vollbild/HDR, oder das Spiel war nicht vorn) – Codes werden ungeprüft getippt.");
                }
                if (++_unreadableInARow >= 3) ShowNotice();
                return;
            }
            _unreadableInARow = 0;
            var rows = MenuReader.Read(image.captured, image.scale * screenScale);
            // The game scene behind a closed list can look like a row or two (an objective with an icon): only a
            // reading with a code or a known icon in it is the list.
            if (!_screen!.LooksLikeTheList(rows))
            {
                rejected |= rows.Count > 0;
                rows = [];
            }
            var elapsed = watch.ElapsedMilliseconds;
            if (rows.Count > 0 && previous is { Count: > 0 } && SameReading(rows, previous))
            {
                Accept(rows, stable: true);
                break;
            }
            if (rows.Count == 0 && previous is null or { Count: 0 } && elapsed >= 250)
            {
                NotFound();
                break;
            }
            if (elapsed >= 450)
            {
                if (rows.Count > 0) Accept(rows, stable: false);
                else NotFound();
                break;
            }
            previous = rows;
            if (ct.WaitHandle.WaitOne(30)) return;
        }
        if (watch.ElapsedMilliseconds > 120) _log($"Liste gelesen in {watch.ElapsedMilliseconds} ms");

        void Accept(IReadOnlyList<MenuRow> rows, bool stable)
        {
            _listVisible = ListVisible;
            _lastReadingStable = stable;
            _notFoundInARow = 0;
            if (stable)
            {
                _readerWorked = true;
                Interlocked.Exchange(ref _lastGoodRead, Environment.TickCount64);
            }
            _screen!.Update(rows, learn: stable);
        }

        void NotFound()
        {
            _listVisible = rejected ? ListRejected : ListNotVisible;
            _lastReadingStable = true;
            _screen!.ListNotFound();
            // Black pictures (fullscreen, HDR) count as "unreadable", not here; a list that is really not found
            // three times in a row is worth a word to the player all the same.
            if (++_notFoundInARow >= 3) ShowNotice();
        }
    }

    private void ShowNotice()
    {
        if (_noticeShown) return;
        _noticeShown = true;
        Notice?.Invoke("Stratagem findet die Stratagem-Liste des Spiels nicht. Spiel im Randlos-Modus starten (Optionen → Anzeige). Bis dahin werden Codes ungeprüft getippt.");
    }

    private static bool SameReading(IReadOnlyList<MenuRow> a, IReadOnlyList<MenuRow> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].State == RowState.Callable != (b[i].State == RowState.Callable)) return false;
            if (!Equals(a[i].Code, b[i].Code)) return false;
        }
        return true;
    }

    /// <summary>Reads the game's list from a picture (diagnosis: uploaded screenshot). Returns the rows found.</summary>
    public IReadOnlyList<MenuRow> ReadPicture(PixelImage image, double scale)
    {
        // Only shown, not used: an old screenshot must not replace the live list or teach the icon memory.
        return MenuReader.Read(image, scale);
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
            // Until the game's list was read once (ever – the last reading is kept), the own list stands in.
            if (UseScreen && _screen!.Current.Count > 0)
            {
                _slots.MinSlots = _settings.Radial.ScreenSlots;
                var arranged = _slots.Arrange(_screen.Current);
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
        // With the list read from the game, the state shown may be old (Reinforce once a teammate died): the fresh
        // reading right before typing decides.
        if (!item.Available && !CanRead)
        {
            Log($"{origin}: {item.Label} ist gerade nicht verfügbar");
            return false;
        }
        var (bindings, timing, _) = Snapshot();
        var steps = CodeSteps(item, bindings, timing, menuOpen: false);
        if (!Runner.TryRun(steps, item.Label, out var done))
        {
            Log($"{origin}: {item.Label} verworfen – es läuft schon ein Stratagem");
            return false;
        }
        Log($"{origin}: {item.Label} ({item.Code.Arrows})");
        RaiseChanged();
        done.ContinueWith(_ => RaiseChanged(), TaskScheduler.Default);
        return true;
    }

    public StateSnapshot GetState()
    {
        lock (_gate)
        {
            var b = _bindings.Bindings;
            var items = _radialItems;
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
                    items.Select((it, i) => new ItemInfo(i, it.IsEmpty ? "" : it.Label, it.Name, it.Code.Letters, it.Code.Arrows, it.Available, it.Icon is not null, it.IsEmpty)).ToList()),
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
        var bindings = ResolveBindings(settings); // outside the lock (reads a file)
        lock (_gate)
        {
            _settings = settings;
            _manual = settings.ToManualItems(_catalog);
            _bindings = bindings;
        }
        Radial.SelectUnavailable = CanRead;
        _screen?.RefreshNames();
        UpdateRadialItems();
        var options = settings.ToRadialOptions();
        if (Radial.Options != options) Radial.Options = options; // setting it closes an open menu
        Router.SetHotkeys(settings.ToHotkeys());
        UpdateEnabled();
        RaiseChanged();
    }

    private readonly object _bindingsReadGate = new();

    private BindingsResult ResolveBindings(AppSettings settings)
    {
        lock (_bindingsReadGate) return ResolveBindingsLocked(settings);
    }

    private BindingsResult ResolveBindingsLocked(AppSettings settings)
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
