using Stratagem.Core.Model;

namespace Stratagem.Core.Service;

/// <summary>Is the game running / in the foreground? (Windows: window title of the foreground window.)</summary>
public interface IGameWatcher
{
    bool IsRunning { get; }
    bool IsFocused { get; }
    event Action? Changed;
}

/// <summary>
/// Where the list of callable stratagems comes from. Today: the manual list. Planned: the screen reader,
/// which reads the in-game list (icons + arrows) — then no list has to be maintained at all.
/// </summary>
public interface IStratagemSource
{
    string Name { get; }
    IReadOnlyList<StratagemItem> Current { get; }
    /// <summary>Why the list is empty or may be wrong (shown in the web UI), null if all is fine.</summary>
    string? Problem { get; }
    event Action? Changed;
}

public sealed class GameWatcherStub : IGameWatcher
{
    public bool IsRunning { get; set; }
    public bool IsFocused { get; set; }
    public event Action? Changed;
    public void Raise() => Changed?.Invoke();
}
