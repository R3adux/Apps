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
        Assert.Equal("Machine Gun Sentry", source.Current[1].Name);
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
        Assert.Equal("Resupply", resupply.Name);
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

public sealed class FixtureCapture(string file) : IScreenCapture
{
    public int Calls;
    public PixelImage? CaptureMenuArea(out double scale)
    {
        Interlocked.Increment(ref Calls);
        scale = 1.0;
        return Png.Decode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", file)));
    }
}

public class ScreenFlowTests
{
    [Fact]
    public async Task Radial_opens_the_game_list_reads_it_and_types_only_the_arrows()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var output = new FakeOutput();
        var view = new FakeView();
        var game = new GameWatcherStub { IsRunning = true, IsFocused = true };
        var capture = new FixtureCapture("menu-1080p-a.png");
        var store = new SettingsStore(Path.Combine(dir, "settings.json"));
        var settings = store.Load();
        settings.List.Source = ListSource.Screen;
        settings.Timing = new TimingSettings { KeyHoldMs = 5, KeyGapMs = 5, MenuOpenMs = 5, ListReadMs = 30 };
        using var service = new StratagemService(store, settings, new ServicePlatform(output, view, game, Capture: capture, DataDir: dir));
        try
        {
            // Hold Ctrl: the radial menu opens, the tool opens the game's list (hold mode: keeps the key down) and reads it.
            Assert.True(service.Router.OnKey(KeyNames.ControlLeft, true));
            await MacroRunnerTests.WaitUntil(() => service.RadialItems.Any(i => !i.IsEmpty));
            Assert.Equal(1, capture.Calls);
            Assert.Equal(["+ControlLeft"], output.Events);
            Assert.Equal(8, service.RadialItems.Count);          // fixed places
            Assert.Contains(view.Items, i => i.Code.Letters == "DURRU"); // the open menu was refreshed

            // Move to the MG sentry's place and release: only the arrows, then the held list key is released.
            var mg = service.RadialItems.ToList().FindIndex(i => i.Code.Letters == "DURRU");
            var angle = Radial.RadialLayout.CenterAngle(mg, 8) * Math.PI / 180;
            service.Router.OnMouseMove((int)(Math.Cos(angle) * 150), (int)(Math.Sin(angle) * 150));
            Assert.True(service.Router.OnKey(KeyNames.ControlLeft, false));
            await MacroRunnerTests.WaitUntil(() => !service.Runner.IsRunning && output.Events.Count >= 12);
            Assert.Equal(
                ["+ControlLeft", "+KeyS", "-KeyS", "+KeyW", "-KeyW", "+KeyD", "-KeyD", "+KeyD", "-KeyD", "+KeyW", "-KeyW", "-ControlLeft"],
                output.Events);
            Assert.True(File.Exists(Path.Combine(dir, "icons.json")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Cancelling_closes_the_game_list_again()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var output = new FakeOutput();
        var store = new SettingsStore(Path.Combine(dir, "settings.json"));
        var settings = store.Load();
        settings.List.Source = ListSource.Screen;
        settings.Timing = new TimingSettings { KeyHoldMs = 5, KeyGapMs = 5, MenuOpenMs = 5, ListReadMs = 30 };
        using var service = new StratagemService(store, settings,
            new ServicePlatform(output, new FakeView(), new GameWatcherStub { IsRunning = true, IsFocused = true }, Capture: new FixtureCapture("menu-1080p-b.png")));
        try
        {
            service.Router.OnKey(KeyNames.ControlLeft, true);
            service.Router.OnKey(KeyNames.ControlLeft, false); // released in the centre = cancel
            await MacroRunnerTests.WaitUntil(() => !service.Runner.IsRunning && output.Events.Count == 2);
            Assert.Equal(["+ControlLeft", "-ControlLeft"], output.Events);
        }
        finally { Directory.Delete(dir, true); }
    }
}
