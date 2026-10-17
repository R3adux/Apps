using Stratagem.Core.Input;
using Stratagem.Core.Model;
using Stratagem.Core.Recognition;
using Stratagem.Core.Service;
using Stratagem.Core.Settings;

namespace Stratagem.Core.Tests;

public class RadialSlotsTests
{
    private static StratagemItem I(string code) => new(StratagemCode.Parse(code), code);

    private static string Layout(IReadOnlyList<StratagemItem> items) =>
        string.Join(",", items.Select(i => i.IsEmpty ? "_" : i.Code.Letters));

    [Fact]
    public void A_new_stratagem_does_not_move_the_others()
    {
        var slots = new RadialSlots(8);
        Assert.Equal("UDRLU,DDUR,URDDD,RDURD,_,_,_,_", Layout(slots.Arrange([I("UDRLU"), I("DDUR"), I("URDDD"), I("RDURD")])));
        // Eagle Rearm appears in the game's list between Resupply and the 500 kg bomb.
        Assert.Equal("UDRLU,DDUR,URDDD,RDURD,UULUR,_,_,_", Layout(slots.Arrange([I("UDRLU"), I("DDUR"), I("UULUR"), I("URDDD"), I("RDURD")])));
        // … and disappears again: its place stays free, nothing moves.
        Assert.Equal("UDRLU,DDUR,URDDD,RDURD,_,_,_,_", Layout(slots.Arrange([I("UDRLU"), I("DDUR"), I("URDDD"), I("RDURD")])));
    }

    [Fact]
    public void Places_of_gone_stratagems_are_reused_when_full()
    {
        var slots = new RadialSlots(2);
        slots.Arrange([I("UU"), I("DD")]);
        Assert.Equal("RR,DD", Layout(slots.Arrange([I("RR"), I("DD")])));
        Assert.Equal("RR,DD,LL", Layout(slots.Arrange([I("RR"), I("DD"), I("LL")])));
    }

    [Fact]
    public void Zero_minimum_means_as_many_as_stratagems()
    {
        var slots = new RadialSlots(0);
        Assert.Equal("UU,DD", Layout(slots.Arrange([I("UU"), I("DD")])));
    }
}

public class IconMemoryTests
{
    [Fact]
    public void Learns_finds_and_stores()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = Path.Combine(dir, "icons.json");
            var memory = new IconMemory(path);
            Assert.True(memory.Learn(0b1011_0000UL, StratagemCode.Parse("URDDD")));
            Assert.False(memory.Learn(0b1011_0001UL, StratagemCode.Parse("URDDD"))); // nearly the same icon
            Assert.Equal("URDDD", memory.Find(0b1011_0011UL)?.Letters);
            Assert.Null(memory.Find(ulong.MaxValue));
            Assert.Equal("URDDD", new IconMemory(path).Find(0b1011_0000UL)?.Letters);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class ScreenStratagemSourceTests
{
    private static IReadOnlyList<MenuRow> Rows(string file) =>
        MenuReader.Read(Png.Decode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", file))), 1.0);

    [Fact]
    public void Callable_rows_become_items_and_cooldowns_need_a_learned_code()
    {
        var source = new ScreenStratagemSource(new IconMemory());
        Assert.NotNull(source.Problem);
        Assert.True(source.Update(Rows("menu-1080p-a.png")));
        Assert.Null(source.Problem);
        // Only the two rows with arrows are known so far: Eagle Rearm and the MG sentry.
        Assert.Equal(["UULUR", "DURRU"], source.Current.Select(i => i.Code.Letters));
        Assert.All(source.Current, i => Assert.True(i.Available));
        Assert.All(source.Current, i => Assert.NotNull(i.Icon));
        Assert.Equal("MG-Geschütz", source.Current[1].Name);
    }

    [Fact]
    public void A_code_seen_once_is_known_during_the_cooldown()
    {
        var memory = new IconMemory();
        var source = new ScreenStratagemSource(memory);
        var rows = Rows("menu-1080p-a.png");
        // Pretend Resupply (row 1, on cooldown here) had been seen with its arrows before.
        memory.Learn(rows[1].IconHash, StratagemCode.Parse("DDUR"));
        source.Update(rows);
        var resupply = Assert.Single(source.Current, i => i.Code.Letters == "DDUR");
        Assert.False(resupply.Available);
        Assert.Equal("Nachschub", resupply.Name);
    }

    [Fact]
    public void Nothing_found_keeps_the_last_list_and_reports()
    {
        var source = new ScreenStratagemSource(new IconMemory());
        source.Update(Rows("menu-1080p-a.png"));
        Assert.False(source.Update([]));
        Assert.Equal(2, source.Current.Count);
        Assert.Contains("nicht gefunden", source.Problem);
    }
}

public class MacroQueueTests
{
    [Fact]
    public async Task Held_key_survives_until_the_next_job_and_cancel_releases_it()
    {
        var output = new FakeOutput();
        using var runner = new MacroRunner(output, new PhysicalKeyState());
        var ctrl = KeyNames.ControlLeft;
        Assert.True(await runner.Enqueue([new MacroStep.Press(ctrl), new MacroStep.Wait(5)], "open", new HashSet<Key> { ctrl }));
        Assert.Equal(["+ControlLeft"], output.Events);
        runner.Cancel();
        await MacroRunnerTests.WaitUntil(() => output.Events.Count == 2);
        Assert.Equal("-ControlLeft", output.Events[^1]);
    }

    [Fact]
    public async Task Cancel_drops_waiting_jobs()
    {
        var output = new FakeOutput();
        using var runner = new MacroRunner(output, new PhysicalKeyState());
        var first = runner.Enqueue([new MacroStep.Wait(300)], "wait");
        var second = runner.Enqueue([new MacroStep.Press(KeyNames.KeyW), new MacroStep.Release(KeyNames.KeyW)], "later");
        runner.Cancel();
        Assert.False(await first);
        Assert.False(await second);
        Assert.DoesNotContain("+KeyW", output.Events);
    }
}

/// <summary>"Screen capture" from a test picture; the picture can be switched (list closed = black, capture blocked = null).</summary>
public sealed class FixtureCapture : IScreenCapture
{
    private static readonly Dictionary<string, PixelImage> Cache = [];
    private volatile string? _file;
    public int Calls;

    public FixtureCapture(string? file) => _file = file;

    /// <summary>A picture from tests/data; "black" = a black screen (the list is not open); null = capture fails.</summary>
    public string? File { get => _file; set => _file = value; }

    public PixelImage? CaptureMenuArea(out double scale)
    {
        Interlocked.Increment(ref Calls);
        scale = 1.0;
        var file = _file;
        if (file is null) return null;
        if (file == "black") return new PixelImage(480, 560, new byte[480 * 560 * 4]);
        lock (Cache)
        {
            if (!Cache.TryGetValue(file, out var image))
                Cache[file] = image = Png.Decode(System.IO.File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", file)));
            return image;
        }
    }
}

public class NamesAndPlacesTests
{
    /// <summary>Opens and closes the radial menu once: the game's list (the test picture) is read on the way.</summary>
    internal static async Task ReadOnce(StratagemService service)
    {
        service.Router.OnKey(KeyNames.ControlLeft, true);
        await MacroRunnerTests.WaitUntil(() => service.RadialItems.Any(i => i.Code.Letters == "DURRU") && !service.Runner.IsRunning);
        service.Router.OnKey(KeyNames.ControlLeft, false);
        await MacroRunnerTests.WaitUntil(() => !service.Runner.IsRunning);
    }

    private static StratagemService Start(string dir)
    {
        var store = new SettingsStore(Path.Combine(dir, "settings.json"));
        var settings = store.Load();
        settings.List.Source = ListSource.Screen;
        settings.Timing = new TimingSettings { KeyHoldMs = 5, KeyGapMs = 5, MenuOpenMs = 5, ListReadMs = 30 };
        return new StratagemService(store, settings, new ServicePlatform(new FakeOutput(), new FakeView(),
            new GameWatcherStub { IsRunning = true, IsFocused = true }, Capture: new FixtureCapture("menu-1080p-a.png")));
    }

    [Fact]
    public async Task Own_names_win_and_show_at_once()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using var service = Start(dir);
            await ReadOnce(service);
            Assert.Contains(service.Items, i => i.Name == "MG-Geschütz");

            var s = service.Settings;
            s.List.Names["DURRU"] = "MG";
            service.UpdateSettings(s);
            Assert.Contains(service.Items, i => i.Name == "MG");
            Assert.Contains(service.RadialItems, i => i.Name == "MG");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Places_are_stable_and_free_places_are_reported()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using var service = Start(dir);
            await ReadOnce(service);
            var state = service.GetState().List.Items;
            Assert.Equal(8, state.Count);
            Assert.Equal(["UULUR", "DURRU"], state.Where(i => !i.Empty).Select(i => i.Code));
            Assert.False(service.Activate(7, "Test")); // free place
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_test_screenshot_does_not_change_the_live_list()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using var service = Start(dir);
            var before = service.RadialItems;
            var rows = service.ReadPicture(Png.Decode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "menu-1080p-a.png"))), 1.0);
            Assert.Equal(7, rows.Count);
            Assert.Same(before, service.RadialItems);
            Assert.False(File.Exists(Path.Combine(dir, "icons.json")));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class LastListTests
{
    [Fact]
    public void Last_reading_is_shown_after_a_restart()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = Path.Combine(dir, "lastlist.json");
            var first = new ScreenStratagemSource(new IconMemory(), lastPath: path);
            first.Update(MenuReader.Read(Png.Decode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "menu-1080p-a.png"))), 1.0));
            Assert.True(File.Exists(path));

            var again = new ScreenStratagemSource(new IconMemory(), lastPath: path);
            Assert.Equal(["UULUR", "DURRU"], again.Current.Select(i => i.Code.Letters));
            Assert.All(again.Current, i => Assert.NotNull(i.Icon));
            Assert.Contains("letzten Lesen", again.Problem);
        }
        finally { Directory.Delete(dir, true); }
    }
}
