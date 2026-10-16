using Stratagem.Core.Input;
using Stratagem.Core.Model;
using Stratagem.Core.Radial;

namespace Stratagem.Core.Tests;

public class RadialControllerTests
{
    private static readonly Key Ctrl = KeyNames.ControlLeft;
    private static readonly Key V = new(0x2F, false);
    private static readonly Key W = KeyNames.KeyW;

    private readonly FakeView _view = new();
    private readonly List<string> _activated = [];
    private readonly List<string> _injected = [];
    private long _now;

    private RadialController Create(RadialMode mode = RadialMode.HoldRelease, IReadOnlyList<StratagemItem>? items = null)
    {
        var c = new RadialController(_view, () => items ?? Items.Four, (_, item) => _activated.Add(item.Label),
            (input, down) => _injected.Add($"{(down ? "+" : "-")}{input.Name}"), () => _now)
        {
            Options = RadialOptions.Default with { Mode = mode },
            Enabled = true,
        };
        return c;
    }

    // --- hold & release ---------------------------------------------------------------------------

    [Fact]
    public void Hold_move_release_selects()
    {
        var c = Create();
        Assert.True(c.OnKey(Ctrl, true));
        Assert.True(_view.Visible);
        Assert.True(c.OnMouseMove(5, 30));
        Assert.Null(_view.Highlighted);            // still in the dead zone
        Assert.True(c.OnMouseMove(0, 40));
        Assert.Equal(2, _view.Highlighted);         // down = Eagle
        Assert.True(c.OnKey(Ctrl, true));           // key repeat is swallowed
        Assert.True(c.OnKey(Ctrl, false));
        Assert.False(_view.Visible);
        Assert.Equal(["Eagle 500kg Bomb"], _activated);
        Assert.Equal(RadialState.Idle, c.State);
    }

    [Fact]
    public void Release_in_dead_zone_cancels()
    {
        var c = Create();
        c.OnKey(Ctrl, true);
        c.OnMouseMove(10, 10);
        Assert.True(c.OnKey(Ctrl, false));
        Assert.Empty(_activated);
    }

    [Fact]
    public void Clamped_vector_allows_quick_change_of_direction()
    {
        var c = Create();
        c.OnKey(Ctrl, true);
        c.OnMouseMove(0, 5000);      // far down, clamped to 160
        Assert.Equal(2, _view.Highlighted);
        c.OnMouseMove(0, -400);      // a short way up is enough
        Assert.Equal(0, _view.Highlighted);
    }

    [Fact]
    public void Item_on_cooldown_is_not_activated()
    {
        var c = Create();
        c.OnKey(Ctrl, true);
        c.OnMouseMove(-100, 0);      // left = Laser, unavailable
        Assert.Equal(3, _view.Highlighted);
        c.OnKey(Ctrl, false);
        Assert.Empty(_activated);
    }

    [Fact]
    public void Chord_hands_trigger_to_game_before_the_other_key()
    {
        var c = Create();
        Assert.True(c.OnKey(Ctrl, true));
        Assert.True(c.OnKey(V, true));              // swallowed, re-sent after Ctrl
        Assert.Equal(["+ControlLeft", "+KeyV"], _injected);
        Assert.False(_view.Visible);
        Assert.Equal(RadialState.Chord, c.State);
        Assert.False(c.OnKey(V, false));            // the game got our V down, so the real up passes
        Assert.False(c.OnKey(Ctrl, true));          // repeat passes
        Assert.False(c.OnKey(Ctrl, false));         // the game must see Ctrl go up
        Assert.Equal(RadialState.Idle, c.State);
        Assert.Empty(_activated);
    }

    [Fact]
    public void Escape_cancels_and_swallows_the_trigger_release()
    {
        var c = Create();
        c.OnKey(Ctrl, true);
        c.OnMouseMove(0, 100);
        Assert.True(c.OnKey(KeyNames.Escape, true));
        Assert.False(_view.Visible);
        Assert.True(c.OnKey(KeyNames.Escape, false));
        Assert.True(c.OnKey(Ctrl, false));
        Assert.Empty(_activated);
    }

    [Fact]
    public void Right_click_cancels_and_never_eats_a_later_click()
    {
        var c = Create();
        c.OnKey(Ctrl, true);
        c.OnMouseMove(0, 100);
        Assert.True(c.OnMouseButton(MouseButton.Right, true));
        Assert.False(_view.Visible);
        // The mouse hook goes away with the menu, so the release may never be seen: it is not waited for.
        // A stray release in the game is harmless; the next real click must reach the game.
        Assert.False(c.OnMouseButton(MouseButton.Right, false));
        Assert.False(c.OnMouseButton(MouseButton.Right, true));
        Assert.True(c.OnKey(Ctrl, false));
        Assert.Empty(_activated);
    }

    [Fact]
    public void Button_held_before_opening_is_released_in_the_game()
    {
        var c = Create();
        Assert.False(c.OnMouseButton(MouseButton.Left, true)); // firing
        c.OnKey(Ctrl, true);
        Assert.False(c.OnMouseButton(MouseButton.Left, false)); // the game must see the release, or it keeps firing
    }

    [Fact]
    public void Walking_keys_pass_only_as_release_while_open()
    {
        var c = Create();
        Assert.False(c.OnKey(W, true));
        c.OnKey(Ctrl, true);
        Assert.False(c.OnKey(W, false));
    }

    [Fact]
    public void Mouse_moves_pass_when_closed()
    {
        var c = Create();
        Assert.False(c.OnMouseMove(10, 10));
    }

    [Fact]
    public void Disabled_passes_everything_and_cancels()
    {
        var c = Create();
        c.OnKey(Ctrl, true);
        c.Enabled = false;
        Assert.False(_view.Visible);
        Assert.True(c.OnKey(Ctrl, false));          // the down was swallowed: its up too
        Assert.False(c.OnKey(Ctrl, true));
        Assert.False(c.OnMouseMove(5, 5));
        Assert.Empty(_activated);
    }

    [Fact]
    public void Losing_focus_during_chord_releases_the_trigger_in_the_game()
    {
        var c = Create();
        c.OnKey(Ctrl, true);
        c.OnKey(V, true);
        _injected.Clear();
        c.Enabled = false;
        Assert.Equal(["-ControlLeft"], _injected);
    }

    [Fact]
    public void Items_are_frozen_while_open()
    {
        var list = new List<StratagemItem>(Items.Four);
        var c = new RadialController(_view, () => list.ToList(), (_, item) => _activated.Add(item.Label), (_, _) => { }, () => _now)
        { Enabled = true };
        c.OnKey(Ctrl, true);
        list.Insert(0, Items.Laser);
        c.OnMouseMove(0, -100);
        c.OnKey(Ctrl, false);
        Assert.Equal(["Reinforce"], _activated);
    }

    [Fact]
    public void Mouse_button_as_trigger()
    {
        var c = Create();
        c.Options = c.Options with { Trigger = InputRef.Of(MouseButton.X1) };
        Assert.True(c.OnMouseButton(MouseButton.X1, true));
        c.OnMouseMove(100, 0);
        Assert.True(c.OnMouseButton(MouseButton.X1, false));
        Assert.Equal(["Resupply"], _activated);
    }

    // --- tap & drag -------------------------------------------------------------------------------

    [Fact]
    public void Tap_opens_drag_selects()
    {
        var c = Create(RadialMode.TapDrag);
        Assert.True(c.OnKey(Ctrl, true));
        Assert.False(_view.Visible);
        Assert.True(c.OnKey(Ctrl, false));
        Assert.True(_view.Visible);
        Assert.True(c.OnMouseMove(0, 100));          // camera frozen, but no selection without the button
        Assert.Null(_view.Highlighted);
        Assert.True(c.OnMouseButton(MouseButton.Left, true));
        c.OnMouseMove(100, 0);
        Assert.Equal(1, _view.Highlighted);
        Assert.True(c.OnMouseButton(MouseButton.Left, false));
        Assert.Equal(["Resupply"], _activated);
        Assert.False(_view.Visible);
    }

    [Fact]
    public void Tap_again_closes_and_swallows_its_release()
    {
        var c = Create(RadialMode.TapDrag);
        c.OnKey(Ctrl, true);
        c.OnKey(Ctrl, false);
        Assert.True(c.OnKey(Ctrl, true));
        Assert.False(_view.Visible);
        Assert.True(c.OnKey(Ctrl, false));
        Assert.Equal(RadialState.Idle, c.State);
    }

    [Fact]
    public void Drag_released_in_center_cancels()
    {
        var c = Create(RadialMode.TapDrag);
        c.OnKey(Ctrl, true);
        c.OnKey(Ctrl, false);
        c.OnMouseButton(MouseButton.Left, true);
        c.OnMouseMove(5, 5);
        Assert.True(c.OnMouseButton(MouseButton.Left, false));
        Assert.Empty(_activated);
        Assert.False(_view.Visible);
    }

    [Fact]
    public void Tap_menu_times_out()
    {
        var c = Create(RadialMode.TapDrag);
        c.OnKey(Ctrl, true);
        c.OnKey(Ctrl, false);
        _now += 4000;
        c.OnTimer();
        Assert.True(_view.Visible);
        _now += 2000;
        c.OnTimer();
        Assert.False(_view.Visible);
    }

    [Fact]
    public void Walking_works_while_tap_menu_is_open()
    {
        var c = Create(RadialMode.TapDrag);
        c.OnKey(Ctrl, true);
        c.OnKey(Ctrl, false);
        Assert.False(c.OnKey(W, true));
        Assert.False(c.OnKey(W, false));
        Assert.True(_view.Visible);
    }

    [Fact]
    public void Chord_during_tap_press()
    {
        var c = Create(RadialMode.TapDrag);
        c.OnKey(Ctrl, true);
        Assert.True(c.OnKey(V, true));
        Assert.Equal(["+ControlLeft", "+KeyV"], _injected);
        Assert.False(c.OnKey(Ctrl, false));
        Assert.False(_view.Visible);
    }
}

public class RadialControllerRepeatTests
{
    [Fact]
    public void Holding_the_trigger_after_cancel_does_not_reopen()
    {
        var view = new FakeView();
        var c = new RadialController(view, () => Items.Four, (_, _) => { }, (_, _) => { }) { Enabled = true };
        c.OnKey(KeyNames.ControlLeft, true);
        c.OnKey(KeyNames.Escape, true);
        Assert.False(view.Visible);
        Assert.True(c.OnKey(KeyNames.ControlLeft, true)); // repeat
        Assert.False(view.Visible);
        Assert.Equal(1, view.ShowCount);
        Assert.True(c.OnKey(KeyNames.ControlLeft, false));
    }
}

public class RadialSmoothnessTests
{
    [Fact]
    public void Highlight_is_sticky_at_segment_borders()
    {
        // 4 segments: border between top (0) and right (1) at -45°.
        var justPastBorder = (X: Math.Cos(-44 * Math.PI / 180) * 100, Y: Math.Sin(-44 * Math.PI / 180) * 100);
        Assert.Equal(1, RadialLayout.Hit(justPastBorder.X, justPastBorder.Y, 4, 10));
        Assert.Equal(0, RadialLayout.Hit(justPastBorder.X, justPastBorder.Y, 4, 10, current: 0, hysteresisDegrees: 7));
        var farPast = (X: Math.Cos(-30 * Math.PI / 180) * 100, Y: Math.Sin(-30 * Math.PI / 180) * 100);
        Assert.Equal(1, RadialLayout.Hit(farPast.X, farPast.Y, 4, 10, current: 0, hysteresisDegrees: 7));
    }

    [Fact]
    public void Highlight_is_sticky_at_the_dead_zone_edge()
    {
        Assert.Null(RadialLayout.Hit(0, -35, 4, 40));
        Assert.Equal(0, RadialLayout.Hit(0, -35, 4, 40, current: 0, hysteresisDegrees: 7));
        Assert.Null(RadialLayout.Hit(0, -25, 4, 40, current: 0, hysteresisDegrees: 7));
    }

    [Fact]
    public void Hysteresis_wraps_around_at_the_top()
    {
        // 8 segments, current = 7 (up-left); a direction slightly right of its border with 0 stays 7.
        var a = -90 - 22.5 + 3; // 3° into segment 0
        Assert.Equal(7, RadialLayout.Hit(Math.Cos(a * Math.PI / 180) * 100, Math.Sin(a * Math.PI / 180) * 100, 8, 10, 7, 7));
    }

    [Fact]
    public void Mouse_capture_follows_the_open_menu()
    {
        var view = new FakeView();
        var changes = new List<bool>();
        var c = new RadialController(view, () => Items.Four, (_, _) => { }, (_, _) => { }) { Enabled = true };
        c.MouseCaptureChanged += changes.Add;
        Assert.False(c.CapturesMouse);
        c.OnKey(KeyNames.ControlLeft, true);
        Assert.True(c.CapturesMouse);
        c.OnMouseMove(0, 100);
        c.OnKey(KeyNames.ControlLeft, false);
        Assert.False(c.CapturesMouse);
        Assert.Equal([true, false], changes);
        Assert.True(view.LastHideSelected);
    }

    [Fact]
    public void Tap_mode_captures_only_once_open()
    {
        var changes = new List<bool>();
        var c = new RadialController(new FakeView(), () => Items.Four, (_, _) => { }, (_, _) => { })
        { Options = RadialOptions.Default with { Mode = RadialMode.TapDrag }, Enabled = true };
        c.MouseCaptureChanged += changes.Add;
        c.OnKey(KeyNames.ControlLeft, true);
        Assert.Empty(changes);
        c.OnKey(KeyNames.ControlLeft, false);
        Assert.Equal([true], changes);
        c.Enabled = false;
        Assert.Equal([true, false], changes);
    }

    [Fact]
    public void Cancel_reports_not_selected()
    {
        var view = new FakeView();
        var c = new RadialController(view, () => Items.Four, (_, _) => { }, (_, _) => { }) { Enabled = true };
        c.OnKey(KeyNames.ControlLeft, true);
        c.OnKey(KeyNames.ControlLeft, false);
        Assert.False(view.LastHideSelected);
    }

    [Fact]
    public void Re_enabling_forgets_downs_whose_ups_were_never_seen()
    {
        var view = new FakeView();
        var c = new RadialController(view, () => Items.Four, (_, _) => { }, (_, _) => { }) { Enabled = true };
        c.OnKey(KeyNames.ControlLeft, true);  // swallowed …
        c.Enabled = false;                    // … focus lost, hooks off, the up is never seen
        c.Enabled = true;
        Assert.True(c.OnKey(KeyNames.ControlLeft, true));
        Assert.True(view.Visible);            // opens again instead of counting as a key repeat
    }
}
