using Stratagem.Core.Input;
using Stratagem.Core.Model;

namespace Stratagem.Core.Radial;

public enum RadialMode
{
    /// <summary>Hold the trigger, move the mouse, release the trigger = select. Release in the centre = cancel.</summary>
    HoldRelease,
    /// <summary>Tap the trigger = open; press the drag button, move, release = select. Tap again / Esc / timeout = close.</summary>
    TapDrag,
}

public sealed record RadialOptions(
    RadialMode Mode,
    InputRef Trigger,
    MouseButton DragButton = MouseButton.Left,
    int DeadzoneCounts = 40,
    int ClampCounts = 160,
    int TimeoutMs = 5000)
{
    public static RadialOptions Default { get; } = new(RadialMode.HoldRelease, InputRef.Of(KeyNames.ControlLeft));
}

/// <summary>Draws the radial menu. Calls come from the hook thread: implementations must only queue work.</summary>
public interface IRadialView
{
    void Show(IReadOnlyList<StratagemItem> items, RadialMode mode);
    void Highlight(int? index);
    void Hide();
}

public enum RadialState { Idle, Held, TapPressed, Open, Dragging, Chord }

/// <summary>
/// The radial menu as a state machine, without any Windows code. The input hooks feed every player event
/// in; the return value says whether the event is swallowed (the game never sees it).
///
/// Rules that keep the game consistent:
/// - The "up" of an event is swallowed exactly when its "down" was swallowed. Otherwise the game would
///   think a key/button is still held (e.g. keep firing).
/// - Trigger + another key (e.g. Ctrl+V in the chat) is a "chord": the menu closes and the trigger is
///   handed to the game after all, before the other key, so shortcuts keep working.
/// </summary>
public sealed class RadialController
{
    private readonly IRadialView _view;
    private readonly Func<IReadOnlyList<StratagemItem>> _items;
    private readonly Action<int, StratagemItem> _activate;
    private readonly Action<InputRef, bool> _inject;
    private readonly Func<long> _clockMs;
    private readonly object _gate = new();
    private readonly HashSet<InputRef> _swallowedDowns = [];

    private RadialOptions _options = RadialOptions.Default;
    private bool _enabled;
    private RadialState _state = RadialState.Idle;
    private IReadOnlyList<StratagemItem> _shown = [];
    private double _x, _y;
    private int? _highlight;
    private long _openedAt;

    /// <param name="activate">Called with the selected item (and its index) when a selection is made.</param>
    /// <param name="inject">Sends a key/button to the game as our own event (only for chords). Must not block.</param>
    public RadialController(
        IRadialView view,
        Func<IReadOnlyList<StratagemItem>> items,
        Action<int, StratagemItem> activate,
        Action<InputRef, bool> inject,
        Func<long>? clockMs = null)
    {
        _view = view;
        _items = items;
        _activate = activate;
        _inject = inject;
        _clockMs = clockMs ?? (() => Environment.TickCount64);
    }

    public RadialState State { get { lock (_gate) return _state; } }
    public int? Highlighted { get { lock (_gate) return _highlight; } }

    public RadialOptions Options
    {
        get { lock (_gate) return _options; }
        set { lock (_gate) { CancelLocked(); _options = value; } }
    }

    /// <summary>Only active while the game is in the foreground and the tool is not paused.</summary>
    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
        set { lock (_gate) { if (!value) CancelLocked(); _enabled = value; } }
    }

    public bool OnKey(Key key, bool down) => OnInput(InputRef.Of(key), down);

    public bool OnMouseButton(MouseButton button, bool down) => OnInput(InputRef.Of(button), down);

    public bool OnMouseMove(int dx, int dy)
    {
        lock (_gate)
        {
            if (!_enabled) return false;
            switch (_state)
            {
                case RadialState.Held or RadialState.Dragging:
                    (_x, _y) = RadialLayout.Clamp(_x + dx, _y + dy, _options.ClampCounts);
                    var hit = RadialLayout.Hit(_x, _y, _shown.Count, _options.DeadzoneCounts);
                    if (hit != _highlight)
                    {
                        _highlight = hit;
                        _view.Highlight(hit);
                    }
                    return true;
                case RadialState.Open:
                    return true; // the camera must not move while the menu is open
                default:
                    return false;
            }
        }
    }

    /// <summary>Call a few times per second (closes a forgotten tap-menu).</summary>
    public void OnTimer()
    {
        lock (_gate)
        {
            if (_state is RadialState.Open or RadialState.Dragging && _clockMs() - _openedAt > _options.TimeoutMs) CloseView();
        }
    }

    public void Cancel()
    {
        lock (_gate) CancelLocked();
    }

    private bool OnInput(InputRef input, bool down)
    {
        lock (_gate)
        {
            // Key repeat of a swallowed key: swallow too. Otherwise holding Ctrl after Esc/close would reopen the menu.
            if (down && _swallowedDowns.Contains(input)) return true;
            var swallowedUp = !down && _swallowedDowns.Remove(input);
            if (!_enabled) return swallowedUp;
            var isTrigger = input == _options.Trigger;
            var result = _state switch
            {
                RadialState.Idle => OnIdle(input, down, isTrigger),
                RadialState.Held => OnHeld(input, down, isTrigger),
                RadialState.TapPressed => OnTapPressed(input, down, isTrigger),
                RadialState.Open or RadialState.Dragging => OnTapOpen(input, down, isTrigger),
                RadialState.Chord => OnChord(down, isTrigger),
                _ => false,
            };
            return result || swallowedUp;
        }
    }

    private bool OnIdle(InputRef input, bool down, bool isTrigger)
    {
        if (!isTrigger || !down) return false;
        Swallow(input);
        if (_options.Mode == RadialMode.HoldRelease) OpenView(RadialState.Held);
        else _state = RadialState.TapPressed;
        return true;
    }

    private bool OnHeld(InputRef input, bool down, bool isTrigger)
    {
        if (isTrigger)
        {
            if (down) return true; // key repeat
            SelectAndClose();
            return true;
        }
        if (input.Mouse is { } button)
        {
            if (!down) return false;
            Swallow(input);
            if (button == MouseButton.Right) CloseView();
            return true;
        }
        if (!down) return false;
        if (input.Key == KeyNames.Escape)
        {
            Swallow(input);
            CloseView();
            return true;
        }
        return StartChord(input);
    }

    private bool OnTapPressed(InputRef input, bool down, bool isTrigger)
    {
        if (isTrigger)
        {
            if (!down) OpenView(RadialState.Open);
            return true;
        }
        if (down && input.Key is not null) return StartChord(input);
        return false;
    }

    private bool OnTapOpen(InputRef input, bool down, bool isTrigger)
    {
        if (isTrigger)
        {
            if (down)
            {
                Swallow(input);
                CloseView();
            }
            return true;
        }
        if (input.Mouse is { } button)
        {
            if (button == _options.DragButton)
            {
                if (down && _state == RadialState.Open)
                {
                    Swallow(input);
                    _state = RadialState.Dragging;
                    _x = _y = 0;
                    return true;
                }
                if (!down && _state == RadialState.Dragging)
                {
                    SelectAndClose();
                    return true;
                }
                return down && Swallow(input);
            }
            if (!down) return false;
            Swallow(input);
            if (button == MouseButton.Right) CloseView();
            return true;
        }
        if (down && input.Key == KeyNames.Escape)
        {
            Swallow(input);
            CloseView();
            return true;
        }
        return false; // other keys (walking) go to the game
    }

    private bool OnChord(bool down, bool isTrigger)
    {
        if (isTrigger && !down) _state = RadialState.Idle;
        return false; // the game owns the trigger now; its up must arrive there
    }

    private bool StartChord(InputRef other)
    {
        CloseView();
        _state = RadialState.Chord;
        _swallowedDowns.Remove(_options.Trigger);
        // The current key arrives before anything we inject, so it is swallowed and re-sent after the trigger.
        _inject(_options.Trigger, true);
        _inject(other, true);
        return true;
    }

    private bool Swallow(InputRef input)
    {
        _swallowedDowns.Add(input);
        return true;
    }

    private void OpenView(RadialState state)
    {
        _shown = _items();
        _x = _y = 0;
        _highlight = null;
        _openedAt = _clockMs();
        _state = state;
        _view.Show(_shown, _options.Mode);
    }

    private void SelectAndClose()
    {
        var index = _highlight;
        var items = _shown;
        CloseView();
        if (index is { } i && i < items.Count && items[i].Available) _activate(i, items[i]);
    }

    private void CloseView()
    {
        var wasOpen = _state is RadialState.Held or RadialState.Open or RadialState.Dragging;
        _state = RadialState.Idle;
        _highlight = null;
        if (wasOpen) _view.Hide();
    }

    private void CancelLocked()
    {
        if (_state == RadialState.Chord) _inject(_options.Trigger, false);
        CloseView();
        // _swallowedDowns stays: the ups of swallowed downs are still swallowed after a cancel.
    }
}
