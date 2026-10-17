using Stratagem.Core.Input;
using Stratagem.Core.Model;
using Stratagem.Core.Radial;
using Stratagem.Core.Recognition;
using Stratagem.Core.Service;
using Stratagem.Core.Settings;

namespace Stratagem.Core.Tests;

/// <summary>
/// The whole way with the game's list: open it, read it, check, type, close – for every way the radial menu can
/// end, for both list modes of the game and with a list that did not open. The game's bindings are the defaults
/// (list on left Ctrl, arrows on WASD); the "screen" is a real screenshot.
/// </summary>
public class ListFlowTests
{
    private const string Ctrl = "ControlLeft";

    /// <summary>Codes of the stratagems in menu-1080p-a.png, top to bottom (Eagle Rearm and MG sentry show arrows).</summary>
    private static readonly string[] ListA = ["UDRLU", "DDUR", "UULUR", "URDDD", "RDURD", "DLRDLR", "DURRU"];

    private sealed class Rig : IDisposable
    {
        public readonly string Dir = Directory.CreateTempSubdirectory().FullName;
        public readonly FakeOutput Output = new();
        public readonly FakeView View = new();
        public readonly GameWatcherStub Game = new() { IsRunning = true, IsFocused = true };
        public readonly FixtureCapture Capture;
        public readonly List<string> Log = [];
        public readonly StratagemService Service;

        /// <param name="knownIcons">The icons of all rows were seen with arrows before (a later mission).</param>
        public Rig(Action<AppSettings>? configure = null, bool knownIcons = false, string? picture = "menu-1080p-a.png")
        {
            Capture = new FixtureCapture(picture);
            if (knownIcons)
            {
                var memory = new IconMemory(Path.Combine(Dir, "icons.json"));
                var rows = MenuReader.Read(new FixtureCapture("menu-1080p-a.png").CaptureMenuArea(out _)!, 1.0);
                for (var i = 0; i < rows.Count; i++) memory.Learn(rows[i].IconHash, StratagemCode.Parse(ListA[i]));
            }
            var store = new SettingsStore(Path.Combine(Dir, "settings.json"));
            var settings = store.Load();
            settings.Timing = new TimingSettings { KeyHoldMs = 5, KeyGapMs = 5, MenuOpenMs = 5, ListReadMs = 30 };
            settings.Game.ReadBindingsFromGame = false;
            configure?.Invoke(settings);
            Service = new StratagemService(store, settings, new ServicePlatform(Output, View, Game, Capture: Capture, DataDir: Dir),
                log: text => { lock (Log) Log.Add(text); });
        }

        public string Events => string.Join(" ", Output.Events);
        public string LogText { get { lock (Log) return string.Join(" | ", Log); } }

        public Task Idle() => MacroRunnerTests.WaitUntil(() => !Service.Runner.IsRunning, () => $"läuft noch: {Events}");

        public Task ListRead() => MacroRunnerTests.WaitUntil(
            () => Service.RadialItems.Any(i => i.Code.Letters == "DURRU") && !Service.Runner.IsRunning,
            () => $"nicht gelesen: {Events} | {LogText}");

        public bool Key(string name, bool down) =>
            KeyNames.TryParse(name, out var key) ? Service.Router.OnKey(key, down) : throw new ArgumentException(name);

        public void PointAt(string code)
        {
            var items = Service.RadialItems;
            var index = items.ToList().FindIndex(i => i.Code.Letters == code);
            Assert.True(index >= 0, $"{code} nicht im Radialmenü");
            var angle = RadialLayout.CenterAngle(index, items.Count) * Math.PI / 180;
            Service.Router.OnMouseMove((int)(Math.Cos(angle) * 150), (int)(Math.Sin(angle) * 150));
        }

        public void Dispose()
        {
            Service.Dispose();
            try { Directory.Delete(Dir, true); } catch (IOException) { }
        }
    }

    private static string Arrows(string code) => string.Join(" ", code.Select(c =>
    {
        var key = c switch { 'U' => "KeyW", 'D' => "KeyS", 'L' => "KeyA", _ => "KeyD" };
        return $"+{key} -{key}";
    }));

    // --- hold mode (the game's default) -------------------------------------------------------------

    [Fact]
    public async Task Selection_types_only_the_arrows_into_the_open_list_and_closes_it()
    {
        using var rig = new Rig();
        Assert.True(rig.Key(Ctrl, true));
        await rig.ListRead();
        Assert.True(rig.Capture.Calls >= 2, "zwei gleiche Bilder nötig"); // the reading was confirmed by a second picture
        Assert.Equal("+ControlLeft", rig.Events);

        rig.PointAt("DURRU");
        Assert.True(rig.Key(Ctrl, false));
        await rig.Idle();
        Assert.Equal($"+ControlLeft {Arrows("DURRU")} -ControlLeft", rig.Events);
    }

    [Fact]
    public async Task Release_in_the_centre_closes_the_list()
    {
        using var rig = new Rig();
        rig.Key(Ctrl, true);
        await rig.ListRead();
        rig.Key(Ctrl, false);
        await rig.Idle();
        Assert.Equal("+ControlLeft -ControlLeft", rig.Events);
    }

    [Fact]
    public async Task A_quick_tap_closes_the_list_after_reading()
    {
        using var rig = new Rig();
        rig.Key(Ctrl, true);
        rig.Key(Ctrl, false);
        await rig.Idle();
        Assert.Equal("+ControlLeft -ControlLeft", rig.Events);
    }

    [Fact]
    public async Task Escape_closes_the_list()
    {
        using var rig = new Rig();
        rig.Key(Ctrl, true);
        await rig.ListRead();
        Assert.True(rig.Key("Escape", true));
        Assert.True(rig.Key("Escape", false));
        rig.Key(Ctrl, false);
        await rig.Idle();
        Assert.Equal("+ControlLeft -ControlLeft", rig.Events);
    }

    [Fact]
    public async Task Typing_a_code_by_hand_keeps_the_list_open_for_the_player()
    {
        // Hold Ctrl, then W: the player types the code themselves. The list must stay open (the held list key is
        // theirs now) and their arrow must arrive.
        using var rig = new Rig();
        rig.Key(Ctrl, true);
        await rig.ListRead();
        Assert.True(rig.Key("KeyW", true));   // re-sent after the trigger
        Assert.False(rig.Key("KeyW", false));
        Assert.False(rig.Key(Ctrl, false));   // the game sees the release: the list closes as without the tool
        await rig.Idle();
        Assert.Equal("+ControlLeft +ControlLeft +KeyW", rig.Events);

        // Leaving the game later must not release a key the tool no longer owns.
        rig.Game.IsFocused = false;
        rig.Game.Raise();
        await rig.Idle();
        Assert.Equal("+ControlLeft +ControlLeft +KeyW", rig.Events);
    }

    [Fact]
    public async Task Leaving_the_game_releases_the_list_key()
    {
        using var rig = new Rig();
        rig.Key(Ctrl, true);
        await rig.ListRead();
        rig.Game.IsFocused = false;
        rig.Game.Raise();
        await rig.Idle();
        Assert.Equal("+ControlLeft -ControlLeft", rig.Events);
    }

    // --- press mode ("Open Stratagem List: Press") ------------------------------------------------

    private static void PressMode(AppSettings s) => s.Game.MenuMode = MenuMode.Press;

    [Fact]
    public async Task Press_mode_quick_tap_leaves_the_games_own_list_open()
    {
        using var rig = new Rig(PressMode);
        rig.Key(Ctrl, true);
        rig.Key(Ctrl, false);
        await rig.Idle();
        Assert.Equal("+ControlLeft -ControlLeft", rig.Events); // opened once, not toggled shut
    }

    [Fact]
    public async Task Press_mode_cancel_closes_the_list_again()
    {
        using var rig = new Rig(PressMode);
        rig.Key(Ctrl, true);
        await rig.ListRead();
        await Task.Delay(350);
        rig.Key(Ctrl, false);
        await rig.Idle();
        Assert.Equal("+ControlLeft -ControlLeft +ControlLeft -ControlLeft", rig.Events);
    }

    [Fact]
    public async Task Press_mode_selection_lets_the_game_close_the_list()
    {
        using var rig = new Rig(PressMode);
        rig.Key(Ctrl, true);
        await rig.ListRead();
        rig.PointAt("UULUR");
        rig.Key(Ctrl, false);
        await rig.Idle();
        Assert.Equal($"+ControlLeft -ControlLeft {Arrows("UULUR")}", rig.Events);
    }

    [Fact]
    public async Task Press_mode_never_opens_a_list_it_could_not_see()
    {
        // Pressing the list key again only closes a list that is open; otherwise it would open it.
        using var rig = new Rig(PressMode);
        rig.Key(Ctrl, true);
        await rig.ListRead();
        await Task.Delay(350);
        rig.Key(Ctrl, false);
        await rig.Idle();
        rig.Output.Clear();

        rig.Capture.File = "black"; // e.g. in a vehicle: the list does not open
        rig.Key(Ctrl, true);
        await rig.Idle();
        await Task.Delay(350);
        rig.Key(Ctrl, false);
        await rig.Idle();
        Assert.Equal("+ControlLeft -ControlLeft", rig.Events);
    }

    // --- checked before typing --------------------------------------------------------------------

    [Fact]
    public async Task A_callable_code_is_typed_after_reading()
    {
        using var rig = new Rig(knownIcons: true);
        Assert.True(rig.Service.Activate(StratagemCode.Parse("DURRU"), "Test"));
        await rig.Idle();
        Assert.Equal($"+ControlLeft {Arrows("DURRU")} -ControlLeft", rig.Events);
    }

    [Fact]
    public async Task A_code_on_cooldown_is_not_typed()
    {
        using var rig = new Rig(knownIcons: true);
        rig.Service.Activate(StratagemCode.Parse("RDURD"), "Test"); // Orbital Laser, cooldown in the picture
        await rig.Idle();
        Assert.Equal("+ControlLeft -ControlLeft", rig.Events);
        Assert.Contains("nicht verfügbar", rig.LogText);
    }

    [Fact]
    public async Task A_code_that_is_not_in_the_list_is_not_typed()
    {
        using var rig = new Rig(knownIcons: true);
        rig.Service.Activate(StratagemCode.Parse("URDR"), "Test"); // Eagle Airstrike: not in this loadout
        await rig.Idle();
        Assert.Equal("+ControlLeft -ControlLeft", rig.Events);
        Assert.Contains("nicht (mehr) in der Liste", rig.LogText);
    }

    [Fact]
    public async Task With_unknown_rows_a_missing_code_is_still_typed()
    {
        // First mission: the rows on cooldown were never seen with arrows, the code may be one of them.
        using var rig = new Rig();
        rig.Service.Activate(StratagemCode.Parse("URDR"), "Test");
        await rig.Idle();
        Assert.Equal($"+ControlLeft {Arrows("URDR")} -ControlLeft", rig.Events);
    }

    [Fact]
    public async Task Nothing_is_typed_when_the_list_did_not_open()
    {
        using var rig = new Rig(knownIcons: true);
        rig.Service.Activate(StratagemCode.Parse("DURRU"), "Test"); // proves the list can be read here
        await rig.Idle();
        rig.Output.Clear();

        rig.Capture.File = "black";
        rig.Service.Activate(StratagemCode.Parse("DURRU"), "Test");
        await rig.Idle();
        Assert.Equal("+ControlLeft -ControlLeft", rig.Events); // no WASD: the player would have walked
        Assert.Contains("nicht aufgegangen", rig.LogText);
    }

    [Fact]
    public async Task Without_a_picture_the_code_is_typed_blind()
    {
        using var rig = new Rig(picture: null);
        rig.Service.Activate(StratagemCode.Parse("URDR"), "Test");
        await rig.Idle();
        Assert.Equal($"+ControlLeft {Arrows("URDR")} -ControlLeft", rig.Events);
    }

    // --- tap mode: the list is only opened briefly ------------------------------------------------

    [Fact]
    public async Task Tap_mode_reads_with_a_short_look_and_holds_walking_back()
    {
        using var rig = new Rig(s => s.Radial.Mode = RadialMode.TapDrag);
        rig.Key(Ctrl, true);
        rig.Key(Ctrl, false); // menu open; the list is opened, read and closed
        await MacroRunnerTests.WaitUntil(() => rig.Output.Events.Count >= 1);
        Assert.True(rig.Key("KeyW", true)); // walking on: held back while the list is open
        await rig.ListRead();
        Assert.Equal("+ControlLeft -ControlLeft +KeyW", rig.Events); // and handed back afterwards
        Assert.False(rig.Key("KeyW", false));
        Assert.Equal(RadialState.Open, rig.Service.Radial.State);
    }

    [Fact]
    public async Task Tap_mode_selection_opens_reads_types_and_closes()
    {
        using var rig = new Rig(s => s.Radial.Mode = RadialMode.TapDrag);
        rig.Key(Ctrl, true);
        rig.Key(Ctrl, false);
        await rig.ListRead();
        rig.Output.Clear();

        rig.Service.Router.OnMouseButton(MouseButton.Left, true);
        rig.PointAt("DURRU");
        rig.Service.Router.OnMouseButton(MouseButton.Left, false);
        await rig.Idle();
        Assert.Equal($"+ControlLeft {Arrows("DURRU")} -ControlLeft", rig.Events);
    }

    // --- without any setting ----------------------------------------------------------------------

    [Fact]
    public async Task Until_the_first_reading_the_own_list_stands_in()
    {
        using var rig = new Rig(picture: "black");
        Assert.Equal(ListSource.Screen, rig.Service.Current.List.Source); // the default
        Assert.Equal("UDRLU", rig.Service.RadialItems[0].Code.Letters);
        Assert.Equal(ScreenStratagemSource.NotReadYet, rig.Service.GetState().List.Problem);

        rig.Capture.File = "menu-1080p-a.png";
        rig.Key(Ctrl, true);
        await rig.ListRead();
        Assert.Equal(8, rig.Service.RadialItems.Count); // the game's list on fixed places
        Assert.Null(rig.Service.GetState().List.Problem);
        rig.Key(Ctrl, false);
        await rig.Idle();
    }

    [Fact]
    public void Old_settings_switch_to_reading_from_the_game_once()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, """{ "version": 1, "list": { "source": "Manual" } }""");
            var settings = new SettingsStore(path).Load();
            Assert.Equal(ListSource.Screen, settings.List.Source);
            Assert.Contains("\"version\": 2", File.ReadAllText(path)); // saved: not migrated again

            settings.List.Source = ListSource.Manual; // a deliberate choice stays
            new SettingsStore(path).Save(settings);
            Assert.Equal(ListSource.Manual, new SettingsStore(path).Load().List.Source);
        }
        finally { Directory.Delete(dir, true); }
    }
}
