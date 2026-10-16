using System.Text.Json;

namespace Stratagem.Core.Model;

/// <summary>
/// Known stratagem names and codes. Only used for names (manual list, naming a code read from the screen);
/// the code itself always comes from the game or the player, so a stale catalog never breaks anything.
/// </summary>
public sealed class StratagemCatalog
{
    /// <param name="German">The name in the German game, where known (from screenshots); shown instead of Name.</param>
    public sealed record Entry(string Name, StratagemCode Code, string? German = null)
    {
        public string Display => German ?? Name;
    }

    private readonly List<Entry> _entries;

    public StratagemCatalog(IEnumerable<Entry> entries) => _entries = entries.ToList();

    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>Display name (German where known, else English).</summary>
    public string? NameOf(StratagemCode code) => _entries.FirstOrDefault(e => e.Code.Equals(code))?.Display;

    public Entry? Find(string name) =>
        _entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(e.German, name, StringComparison.OrdinalIgnoreCase));

    private sealed record JsonEntry(string Name, string Code, string? De);

    public static StratagemCatalog FromJson(string json)
    {
        var raw = JsonSerializer.Deserialize<List<JsonEntry>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        return new StratagemCatalog(raw.Select(e => new Entry(e.Name, StratagemCode.Parse(e.Code), e.De)));
    }

    private static readonly Lazy<StratagemCatalog> BuiltIn = new(() =>
    {
        using var stream = typeof(StratagemCatalog).Assembly.GetManifestResourceStream("catalog.json")
            ?? throw new InvalidOperationException("catalog.json fehlt in Stratagem.Core");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    });

    public static StratagemCatalog Default => BuiltIn.Value;
}
