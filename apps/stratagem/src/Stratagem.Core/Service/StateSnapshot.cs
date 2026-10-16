namespace Stratagem.Core.Service;

/// <summary>Everything the web UI, the phone deck and the Stream Deck plugin need. Sent as JSON (GET /api/state, WebSocket).</summary>
public sealed record StateSnapshot(
    int ApiVersion,
    string AppVersion,
    bool Paused,
    bool Active,
    bool Busy,
    GameInfo Game,
    BindingsInfo Bindings,
    ListInfo List,
    IReadOnlyList<EventInfo> Events);

public sealed record GameInfo(bool Running, bool Focused, string WindowTitle, bool OnlyWhenFocused);

public sealed record BindingsInfo(string Menu, string MenuMode, string Up, string Down, string Left, string Right, string? SourceFile, IReadOnlyList<string> Notes,
    IReadOnlyList<ConflictInfo> Conflicts);

/// <summary>A key/button of this tool that the game also uses. By: "radial" (trigger) or "hotkey".</summary>
public sealed record ConflictInfo(string Input, string By, string Section, string Action, string? Trigger);

public sealed record ListInfo(string Source, string? Problem, IReadOnlyList<ItemInfo> Items);

/// <param name="Empty">A free place (list read from the game): keeps the other places where they are.</param>
public sealed record ItemInfo(int Index, string Label, string? Name, string Code, string Arrows, bool Available, bool HasIcon, bool Empty);

public sealed record EventInfo(DateTimeOffset Time, string Text);
