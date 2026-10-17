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
    /// <param name="selected">True when closed by a selection (the view may confirm it briefly), false on cancel.</param>
    void Hide(bool selected);
}

public enum RadialState { Idle, Held, TapPressed, Open, Dragging, Chord }

public enum RadialCloseReason
{
    /// <summary>A stratagem was selected (it is activated right after).</summary>
    Selected,
    /// <summary>Trigger (hold mode) or drag button released without a stratagem: in the centre or on an empty place.</summary>
    Released,
    /// <summary>Esc, right click, the trigger tapped again, the timeout, or the menu was switched off.</summary>
    Cancelled,
    /// <summary>Trigger + another key (e.g. Ctrl+W, Ctrl+V): the trigger now belongs to the game.</summary>
    Chord,
}

/// <param name="OpenMs">How long the menu was open.</param>
/// <param name="EverHighlighted">Whether any place was pointed at while it was open.</param>
public readonly record struct RadialClose(RadialCloseReason Reason, long OpenMs, bool EverHighlighted);

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
    /// <summary>Movement past a segment border before the highlight switches (no flicker at the border).</summary>
    public const double BorderHysteresisDegrees = 7;
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
    private bool _everHighlighted;
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

    /// <summary>
    /// True while the menu needs mouse movement and buttons (open). The Windows side installs the mouse hook
    /// only then: during normal play no mouse event passes through this tool.
    /// </summary>
    public bool CapturesMouse => Volatile.Read(ref _capturesMouse);

    /// <summary>Raised when <see cref="CapturesMouse"/> changes, from the hook thread inside the lock: must not block.</summary>
    public event Action<bool>? MouseCaptureChanged;

    /// <summary>The menu appeared (inside the lock: must not block).</summary>
    public event Action? Opened;

    /// <summary>The menu closed, and why (raised before a selected stratagem is activated; inside the lock: must not block).</summary>
    public event Action<RadialClose>? Closed;

    /// <summary>
    /// New items while the menu is open (e.g. the list was just read from the game). The current direction is
    /// applied to the new items at once.
    /// </summary>
    public void Refresh(IReadOnlyList<StratagemItem> items)
    {
        lock (_gate)
        {
            if (_state is not (RadialState.Held or RadialState.Open or RadialState.Dragging) || ReferenceEquals(items, _shown)) return;
            _shown = items;
            _view.Show(items, _options.Mode);
            if (_state is RadialState.Held or RadialState.Dragging)
            {
                _highlight = RadialLayout.Hit(_x, _y, items.Count, _options.DeadzoneCounts);
                if (_highlight is not null)
                {
                    _everHighlighted = true;
                    _view.Highlight(_highlight);
                }
            }
            else _highlight = null;
        }
    }

    private bool _capturesMouse;
    public int? Highlighted { get { lock (_gate) return _highlight; } }

    /// <summary>
    /// Places shown as unavailable can be selected anyway (the list read from the game: whether it is callable is
    /// checked again on a fresh reading right before typing, a remembered state may be old).
    /// </summary>
    public bool SelectUnavailable { get; set; }

    /// <summary>
    /// Asked when a chord starts: true = do not send the trigger to the game (it already did what the trigger does,
    /// e.g. opened the game's list in "press" mode, where a second press would close it again). Inside the lock.
    /// </summary>
    public Func<bool>? KeepTriggerOnChord { get; set; }

    public RadialOptions Options
    {
        get { lock (_gate) return _options; }
        set { lock (_gate) { CancelLocked(); _options = value; } }
    }

    /// <summary>Only active while the game is in the foreground and the tool is not paused.</summary>
    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
        set
        {
            lock (_gate)
            {
                if (!value) CancelLocked();
                // Re-enabled: the hooks were off meanwhile, so remembered downs are stale (their ups were never seen).
                else if (!_enabled) _swallowedDowns.Clear();
                _enabled = value;
            }
        }
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
                    var hit = RadialLayout.Hit(_x, _y, _shown.Count, _options.DeadzoneCounts, _highlight, BorderHysteresisDegrees);
                    if (hit != _highlight)
                    {
                        _highlight = hit;
                        if (hit is not null) _everHighlighted = true;
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
            if (_state is RadialState.Open or RadialState.Dragging && _clockMs() - _openedAt > _options.TimeoutMs) CloseView(RadialCloseReason.Cancelled);
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
        else SetState(RadialState.TapPressed);
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
            if (button == MouseButton.Right) CloseView(RadialCloseReason.Cancelled);
            return true;
        }
        if (!down) return false;
        if (input.Key == KeyNames.Escape)
        {
            Swallow(input);
            CloseView(RadialCloseReason.Cancelled);
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
                CloseView(RadialCloseReason.Cancelled);
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
                    SetState(RadialState.Dragging);
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
            if (button == MouseButton.Right) CloseView(RadialCloseReason.Cancelled);
            return true;
        }
        if (down && input.Key == KeyNames.Escape)
        {
            Swallow(input);
            CloseView(RadialCloseReason.Cancelled);
            return true;
        }
        return false; // other keys (walking) go to the game
    }

    private bool OnChord(bool down, bool isTrigger)
    {
        if (isTrigger && !down) SetState(RadialState.Idle);
        return false; // the game owns the trigger now; its up must arrive there
    }

    private bool StartChord(InputRef other)
    {
        var keepTrigger = KeepTriggerOnChord?.Invoke() == true; // asked first: closing changes what it is based on
        CloseView(RadialCloseReason.Chord);
        SetState(RadialState.Chord);
        _swallowedDowns.Remove(_options.Trigger);
        // The current key arrives before anything we inject, so it is swallowed and re-sent after the trigger.
        if (!keepTrigger) _inject(_options.Trigger, true);
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
        _everHighlighted = false;
        _openedAt = _clockMs();
        SetState(state);
        _view.Show(_shown, _options.Mode);
        Opened?.Invoke();
    }

    private void SelectAndClose()
    {
        var index = _highlight;
        var items = _shown;
        var valid = index is { } i && i < items.Count && !items[i].IsEmpty && (items[i].Available || SelectUnavailable);
        CloseView(valid ? RadialCloseReason.Selected : RadialCloseReason.Released);
        if (valid) _activate(index!.Value, items[index.Value]);
    }

    private void CloseView(RadialCloseReason reason)
    {
        var wasOpen = _state is RadialState.Held or RadialState.Open or RadialState.Dragging;
        SetState(RadialState.Idle);
        _highlight = null;
        if (!wasOpen) return;
        _view.Hide(reason == RadialCloseReason.Selected);
        Closed?.Invoke(new RadialClose(reason, _clockMs() - _openedAt, _everHighlighted));
    }

    private void SetState(RadialState state)
    {
        _state = state;
        var capture = state is RadialState.Held or RadialState.Open or RadialState.Dragging;
        if (capture == _capturesMouse) return;
        // The mouse hook may go away now, so ups of swallowed buttons might never arrive. A stray up in the
        // game is harmless; a fresh press swallowed as "repeat" would cost a shot.
        if (!capture) _swallowedDowns.RemoveWhere(i => i.Mouse is not null && i != _options.Trigger);
        Volatile.Write(ref _capturesMouse, capture);
        MouseCaptureChanged?.Invoke(capture);
    }

    private void CancelLocked()
    {
        if (_state == RadialState.Chord) _inject(_options.Trigger, false);
        CloseView(RadialCloseReason.Cancelled);
        // _swallowedDowns stays: the ups of swallowed downs are still swallowed after a cancel.
    }
}
