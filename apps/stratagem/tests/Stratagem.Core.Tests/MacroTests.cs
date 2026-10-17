using Stratagem.Core.Input;
using Stratagem.Core.Model;
using Stratagem.Core.Radial;

namespace Stratagem.Core.Tests;

public class MacroPlannerTests
{
    private static string Describe(IEnumerable<MacroStep> steps) => string.Join(" ", steps.Select(s => s switch
    {
        MacroStep.Press p => "+" + p.Key.Name,
        MacroStep.Release r => "-" + r.Key.Name,
        MacroStep.Wait w => w.Ms.ToString(),
        _ => "?",
    }));

    private static readonly MacroTiming Timing = new(KeyHoldMs: 30, KeyGapMs: 20, MenuOpenMs: 50);

    [Fact]
    public void Hold_mode_keeps_menu_pressed()
    {
        var steps = MacroPlanner.Plan(StratagemCode.Parse("URD"), GameBindings.Default, Timing);
        Assert.Equal("+ControlLeft 50 +KeyW 30 -KeyW 20 +KeyD 30 -KeyD 20 +KeyS 30 -KeyS 20 -ControlLeft 20", Describe(steps));
    }

    [Fact]
    public void Press_mode_taps_menu_once()
    {
        var bindings = GameBindings.Default with { MenuMode = MenuMode.Press, Up = KeyNames.ArrowUp, Right = KeyNames.ArrowRight };
        var steps = MacroPlanner.Plan(StratagemCode.Parse("UR"), bindings, Timing);
        Assert.Equal("+ControlLeft 30 -ControlLeft 50 +ArrowUp 30 -ArrowUp 20 +ArrowRight 30 -ArrowRight 20", Describe(steps));
    }

    [Fact]
    public void Menu_already_open_types_the_code_and_releases_the_held_key()
    {
        var steps = MacroPlanner.Plan(StratagemCode.Parse("UD"), GameBindings.Default, Timing, menuAlreadyOpen: true);
        Assert.Equal("+KeyW 30 -KeyW 20 +KeyS 30 -KeyS 20 -ControlLeft 20", Describe(steps)); // hold mode: the held key is released after the code
    }

    [Fact]
    public void Timing_is_clamped()
    {
        var steps = MacroPlanner.Plan(StratagemCode.Parse("U"), GameBindings.Default, new MacroTiming(0, 99999, -5));
        Assert.Contains(steps, s => s is MacroStep.Wait { Ms: 5 });
        Assert.Contains(steps, s => s is MacroStep.Wait { Ms: 500 });
        Assert.Contains(steps, s => s is MacroStep.Wait { Ms: 0 });
    }
}

public class MacroRunnerTests
{
    private static IReadOnlyList<MacroStep> Steps(params MacroStep[] steps) => steps;

    [Fact]
    public async Task Runs_steps_in_order()
    {
        var output = new FakeOutput();
        using var runner = new MacroRunner(output, new PhysicalKeyState());
        var plan = MacroPlanner.Plan(StratagemCode.Parse("UD"), GameBindings.Default, new MacroTiming(5, 5, 5));
        Assert.True(runner.TryRun(plan, "test", out var done));
        Assert.True(await done.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(["+ControlLeft", "+KeyW", "-KeyW", "+KeyS", "-KeyS", "-ControlLeft"], output.Events);
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task Second_macro_is_dropped_while_busy()
    {
        var output = new FakeOutput();
        using var runner = new MacroRunner(output, new PhysicalKeyState());
        Assert.True(runner.TryRun(Steps(new MacroStep.Press(KeyNames.KeyW), new MacroStep.Wait(200), new MacroStep.Release(KeyNames.KeyW)), "a", out var first));
        Assert.False(runner.TryRun(Steps(new MacroStep.Press(KeyNames.KeyS)), "b", out _));
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(runner.TryRun(Steps(new MacroStep.Press(KeyNames.KeyS), new MacroStep.Release(KeyNames.KeyS)), "c", out var third));
        Assert.True(await third.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Cancel_releases_pressed_keys()
    {
        var output = new FakeOutput();
        using var runner = new MacroRunner(output, new PhysicalKeyState());
        runner.TryRun(Steps(new MacroStep.Press(KeyNames.ControlLeft), new MacroStep.Press(KeyNames.KeyW), new MacroStep.Wait(5000)), "long", out var done);
        await WaitUntil(() => output.Events.Count == 2);
        Assert.True(runner.ShouldHoldBack(KeyNames.KeyW));
        runner.Cancel();
        Assert.False(await done.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("-ControlLeft", output.Events);
        Assert.Contains("-KeyW", output.Events);
        Assert.False(runner.ShouldHoldBack(KeyNames.KeyW));
    }

    [Fact]
    public async Task Held_walking_key_is_released_first_and_pressed_again()
    {
        var output = new FakeOutput();
        var physical = new PhysicalKeyState();
        physical.Set(KeyNames.KeyW, true);
        using var runner = new MacroRunner(output, physical);
        runner.TryRun(Steps(new MacroStep.Press(KeyNames.KeyW), new MacroStep.Release(KeyNames.KeyW)), "w", out var done);
        Assert.True(await done.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(["-KeyW", "+KeyW", "-KeyW", "+KeyW"], output.Events);
    }

    [Fact]
    public async Task Key_released_during_macro_is_not_pressed_again()
    {
        var output = new FakeOutput();
        var physical = new PhysicalKeyState();
        physical.Set(KeyNames.KeyW, true);
        using var runner = new MacroRunner(output, physical);
        runner.TryRun(Steps(new MacroStep.Press(KeyNames.KeyW), new MacroStep.Wait(100), new MacroStep.Release(KeyNames.KeyW)), "w", out var done);
        await WaitUntil(() => output.Events.Count >= 2);
        physical.Set(KeyNames.KeyW, false);
        Assert.True(await done.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(["-KeyW", "+KeyW", "-KeyW"], output.Events);
    }

    internal static async Task WaitUntil(Func<bool> condition, Func<string>? describe = null)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), describe?.Invoke());
    }
}

public class InputRouterTests
{
    private readonly FakeOutput _output = new();
    private readonly List<HotkeyAction> _fired = [];
    private bool _active = true;

    private (InputRouter Router, MacroRunner Runner, PhysicalKeyState Physical) Create()
    {
        var physical = new PhysicalKeyState();
        var runner = new MacroRunner(_output, physical);
        var radial = new RadialController(new FakeView(), () => Items.Four, (_, _) => { }, (_, _) => { }) { Enabled = true };
        var router = new InputRouter(radial, runner, physical, () => _active, _fired.Add);
        router.SetHotkeys(new Dictionary<InputRef, HotkeyAction> { [InputRef.Parse("Numpad1")] = HotkeyAction.ForSlot(0) });
        return (router, runner, physical);
    }

    [Fact]
    public void Hotkey_fires_once_and_swallows_repeat_and_release()
    {
        var (router, runner, _) = Create();
        using var _r = runner;
        var num1 = InputRef.Parse("Numpad1").Key!.Value;
        Assert.True(router.OnKey(num1, true));
        Assert.True(router.OnKey(num1, true));
        Assert.True(router.OnKey(num1, false));
        Assert.Single(_fired);
    }

    [Fact]
    public void Hotkey_passes_when_inactive()
    {
        var (router, runner, _) = Create();
        using var _r = runner;
        _active = false;
        Assert.False(router.OnKey(InputRef.Parse("Numpad1").Key!.Value, true));
        Assert.Empty(_fired);
    }

    [Fact]
    public async Task Player_presses_of_macro_keys_are_held_back()
    {
        var (router, runner, physical) = Create();
        using var _r = runner;
        runner.TryRun([new MacroStep.Press(KeyNames.KeyW), new MacroStep.Wait(300), new MacroStep.Release(KeyNames.KeyW)], "w", out var done);
        await MacroRunnerTests.WaitUntil(() => runner.ShouldHoldBack(KeyNames.KeyW));
        Assert.True(router.OnKey(KeyNames.KeyW, true));
        Assert.True(physical.IsDown(KeyNames.KeyW));
        Assert.False(router.OnKey(KeyNames.KeyD, true));
        Assert.True(await done.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("+KeyW", _output.Events[^1]); // still held: handed back to the game
    }

    [Fact]
    public async Task Trigger_reaches_the_radial_menu_while_a_macro_uses_the_same_key()
    {
        // Ctrl is the radial trigger and the game's list key: the release must close the menu even while
        // a macro is opening the list with Ctrl (fast tap on a fast PC).
        var physical = new PhysicalKeyState();
        using var runner = new MacroRunner(_output, physical);
        var view = new FakeView();
        var radial = new RadialController(view, () => Items.Four, (_, _) => { }, (_, _) => { }) { Enabled = true };
        var router = new InputRouter(radial, runner, physical, () => true, _fired.Add);
        runner.TryRun([new MacroStep.Press(KeyNames.ControlLeft), new MacroStep.Wait(300), new MacroStep.Release(KeyNames.ControlLeft)], "open", out var done);
        await MacroRunnerTests.WaitUntil(() => runner.ShouldHoldBack(KeyNames.ControlLeft));
        Assert.True(router.OnKey(KeyNames.ControlLeft, true));
        Assert.True(view.Visible);
        Assert.True(router.OnKey(KeyNames.ControlLeft, false));
        Assert.False(view.Visible);
        await done;
    }

    [Fact]
    public void Swallowed_keys_are_not_counted_as_held()
    {
        var (router, runner, physical) = Create();
        using var _r = runner;
        Assert.True(router.OnKey(KeyNames.ControlLeft, true)); // opens the radial menu
        Assert.False(physical.IsDown(KeyNames.ControlLeft));
        Assert.False(router.OnKey(KeyNames.KeyW, false));
    }
}
