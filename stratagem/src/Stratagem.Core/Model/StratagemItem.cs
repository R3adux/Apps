namespace Stratagem.Core.Model;

/// <summary>
/// One stratagem the player can call right now (one entry of the in-game list).
/// </summary>
/// <param name="Code">The arrow sequence.</param>
/// <param name="Name">Display name, null if unknown (e.g. read from the screen and not in the catalog).</param>
/// <param name="Available">False while on cooldown or blocked; shown greyed out, never triggered.</param>
/// <param name="Icon">PNG of the icon, if known (cut out of the screen later). Null = show the arrows.</param>
public sealed record StratagemItem(StratagemCode Code, string? Name, bool Available = true, byte[]? Icon = null)
{
    public string Label => Name ?? Code.Arrows;
}
