using Stratagem.Core.Model;

namespace Stratagem.Core.Input;

/// <summary>How the game opens the stratagem list (in-game option "Open Stratagem List").</summary>
public enum MenuMode
{
    /// <summary>List is open while the key is held (game default).</summary>
    Hold,
    /// <summary>A press opens the list; it closes by itself after a code was entered.</summary>
    Press,
}

/// <summary>The game's key bindings that matter for stratagems.</summary>
public sealed record GameBindings(Key Menu, MenuMode MenuMode, Key Up, Key Down, Key Left, Key Right)
{
    public static GameBindings Default { get; } =
        new(KeyNames.ControlLeft, MenuMode.Hold, KeyNames.KeyW, KeyNames.KeyS, KeyNames.KeyA, KeyNames.KeyD);

    public Key KeyFor(Direction d) => d switch
    {
        Direction.Up => Up,
        Direction.Down => Down,
        Direction.Left => Left,
        _ => Right,
    };

    public IEnumerable<Key> DirectionKeys => [Up, Down, Left, Right];
}

/// <summary>
/// Timing of generated input in milliseconds. The game polls input once per frame:
/// every press must last longer than one frame (16.7 ms at 60 fps, 33 ms at 30 fps), or it gets lost.
/// </summary>
public sealed record MacroTiming(int KeyHoldMs = 35, int KeyGapMs = 35, int MenuOpenMs = 60)
{
    public MacroTiming Clamped() => new(Math.Clamp(KeyHoldMs, 5, 500), Math.Clamp(KeyGapMs, 5, 500), Math.Clamp(MenuOpenMs, 0, 2000));
}

public abstract record MacroStep
{
    public sealed record Press(Key Key) : MacroStep;
    public sealed record Release(Key Key) : MacroStep;
    public sealed record Wait(int Ms) : MacroStep;
}

public static class MacroPlanner
{
    /// <summary>
    /// Builds the key sequence for one stratagem.
    /// <paramref name="menuAlreadyOpen"/>: the caller opened the list (e.g. to read it) and closes it itself.
    /// </summary>
    public static IReadOnlyList<MacroStep> Plan(StratagemCode code, GameBindings bindings, MacroTiming timing, bool menuAlreadyOpen = false)
    {
        var t = timing.Clamped();
        var steps = new List<MacroStep>();
        var holdMenu = bindings.MenuMode == MenuMode.Hold && !menuAlreadyOpen;

        if (!menuAlreadyOpen)
        {
            steps.Add(new MacroStep.Press(bindings.Menu));
            if (bindings.MenuMode == MenuMode.Press)
            {
                steps.Add(new MacroStep.Wait(t.KeyHoldMs));
                steps.Add(new MacroStep.Release(bindings.Menu));
            }
            steps.Add(new MacroStep.Wait(t.MenuOpenMs));
        }

        for (var i = 0; i < code.Length; i++)
        {
            var key = bindings.KeyFor(code.Directions[i]);
            steps.Add(new MacroStep.Press(key));
            steps.Add(new MacroStep.Wait(t.KeyHoldMs));
            steps.Add(new MacroStep.Release(key));
            if (i < code.Length - 1 || holdMenu) steps.Add(new MacroStep.Wait(t.KeyGapMs));
        }

        if (holdMenu) steps.Add(new MacroStep.Release(bindings.Menu));
        return steps;
    }
}
