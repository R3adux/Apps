using Stratagem.Core.Model;
using Stratagem.Core.Radial;

namespace Stratagem.Core.Input;

/// <summary>What a direct hotkey does: call a place of the current list, or a fixed code.</summary>
public sealed record HotkeyAction(int? Slot, StratagemCode? Code)
{
    public static HotkeyAction ForSlot(int slot) => new(slot, null);
    public static HotkeyAction ForCode(StratagemCode code) => new(null, code);
}

/// <summary>
/// Entry point for every player input event (from the Windows hooks). Decides per event whether
/// it is swallowed: macro running → radial menu → direct hotkeys → game.
/// Runs on the hook thread: everything here is in-memory and fast.
/// </summary>
public sealed class InputRouter
{
    private readonly RadialController _radial;
    private readonly MacroRunner _runner;
    private readonly PhysicalKeyState _physical;
    private readonly Func<bool> _active;
    private readonly Action<HotkeyAction> _fire;
    private readonly object _gate = new();
    private Dictionary<InputRef, HotkeyAction> _hotkeys = [];
    private readonly HashSet<InputRef> _heldHotkeys = [];

    public InputRouter(RadialController radial, MacroRunner runner, PhysicalKeyState physical, Func<bool> active, Action<HotkeyAction> fire)
    {
        _radial = radial;
        _runner = runner;
        _physical = physical;
        _active = active;
        _fire = fire;
    }

    public void SetHotkeys(IReadOnlyDictionary<InputRef, HotkeyAction> hotkeys)
    {
        lock (_gate) _hotkeys = new Dictionary<InputRef, HotkeyAction>(hotkeys);
    }

    /// <summary>Player key event (never one of our own injected events). True = swallow.</summary>
    public bool OnKey(Key key, bool down)
    {
        if (_runner.ShouldHoldBack(key))
        {
            _physical.Set(key, down); // the runner presses it again afterwards if still held
            return true;
        }
        var swallow = _radial.OnKey(key, down) || OnHotkey(InputRef.Of(key), down);
        if (!swallow) _physical.Set(key, down);
        return swallow;
    }

    public bool OnMouseButton(MouseButton button, bool down) =>
        _radial.OnMouseButton(button, down) || OnHotkey(InputRef.Of(button), down);

    public bool OnMouseMove(int dx, int dy) => _radial.OnMouseMove(dx, dy);

    private bool OnHotkey(InputRef input, bool down)
    {
        lock (_gate)
        {
            if (!down) return _heldHotkeys.Remove(input);
            if (_heldHotkeys.Contains(input)) return true; // key repeat
            if (!_active() || !_hotkeys.TryGetValue(input, out var action)) return false;
            _heldHotkeys.Add(input);
            _fire(action);
            return true;
        }
    }
}
