using System.Text;

namespace Stratagem.Core.Model;

public enum Direction { Up, Down, Left, Right }

/// <summary>
/// The arrow sequence of a stratagem, e.g. ↑ → ↓ ↓ ↓. Value type semantics, stored as "URDDD".
/// </summary>
public sealed class StratagemCode : IEquatable<StratagemCode>
{
    private readonly Direction[] _directions;

    public StratagemCode(IEnumerable<Direction> directions)
    {
        _directions = directions.ToArray();
        if (_directions.Length == 0) throw new ArgumentException("A stratagem code needs at least one direction.");
    }

    public IReadOnlyList<Direction> Directions => _directions;
    public int Length => _directions.Length;

    /// <summary>Compact form "URDL" (also used in settings and the API).</summary>
    public string Letters => string.Concat(_directions.Select(d => d switch
    {
        Direction.Up => 'U',
        Direction.Down => 'D',
        Direction.Left => 'L',
        _ => 'R',
    }));

    public string Arrows => string.Join(' ', _directions.Select(d => d switch
    {
        Direction.Up => "↑",
        Direction.Down => "↓",
        Direction.Left => "←",
        _ => "→",
    }));

    /// <summary>
    /// Accepts "URDDD", "↑→↓↓↓" or "up right down" (also mixed, separated or not).
    /// No WASD: "D" would be ambiguous (Down vs. Right).
    /// </summary>
    public static bool TryParse(string? text, out StratagemCode code)
    {
        code = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var list = new List<Direction>();
        foreach (var token in Tokenize(text))
        {
            Direction? d = token.ToLowerInvariant() switch
            {
                "u" or "↑" or "up" or "oben" => Direction.Up,
                "d" or "↓" or "down" or "unten" => Direction.Down,
                "l" or "←" or "left" or "links" => Direction.Left,
                "r" or "→" or "right" or "rechts" => Direction.Right,
                _ => null,
            };
            if (d is null) return false;
            list.Add(d.Value);
        }
        if (list.Count == 0) return false;
        code = new StratagemCode(list);
        return true;
    }

    public static StratagemCode Parse(string text) =>
        TryParse(text, out var code) ? code : throw new FormatException($"Kein gültiger Stratagem-Code: '{text}'");

    // Words ("up", "right") stay together, everything else is split per character.
    private static IEnumerable<string> Tokenize(string text)
    {
        var word = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || ch is ',' or ';' or '-' or '_')
            {
                if (word.Length > 0) { foreach (var t in Split(word.ToString())) yield return t; word.Clear(); }
                continue;
            }
            word.Append(ch);
        }
        if (word.Length > 0) foreach (var t in Split(word.ToString())) yield return t;
    }

    private static readonly string[] Words = ["oben", "unten", "links", "rechts", "up", "down", "left", "right"];

    private static IEnumerable<string> Split(string word)
    {
        var lower = word.ToLowerInvariant();
        if (Words.Contains(lower)) { yield return lower; yield break; }
        foreach (var ch in word) yield return ch.ToString();
    }

    public bool Equals(StratagemCode? other) => other is not null && _directions.AsSpan().SequenceEqual(other._directions);
    public override bool Equals(object? obj) => Equals(obj as StratagemCode);
    public override int GetHashCode() => Letters.GetHashCode(StringComparison.Ordinal);
    public override string ToString() => Arrows;
}
