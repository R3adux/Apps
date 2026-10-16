using System.Globalization;
using System.Text;

namespace Stratagem.Core.Game;

/// <summary>
/// Minimal reader for SJSON ("simplified JSON" of the Bitsquid/Stingray engine that Helldivers 2 uses):
/// <c>key = value</c>, objects <c>{ }</c>, arrays <c>[ ]</c>, commas optional, unquoted keys, // and /* */ comments,
/// and an implicit root object. Values become Dictionary/List/string/double/bool/null.
/// Tolerant by design: the game's files are not documented and may change with patches.
/// </summary>
public static class Sjson
{
    public static Dictionary<string, object?> Parse(string text)
    {
        var reader = new Reader(text);
        reader.SkipTrivia();
        Dictionary<string, object?> root;
        if (reader.Peek() == '{')
        {
            reader.Next();
            root = reader.ReadObjectBody('}');
        }
        else root = reader.ReadObjectBody('\0');
        return root;
    }

    private sealed class Reader(string text)
    {
        private int _pos;

        public char Peek() => _pos < text.Length ? text[_pos] : '\0';
        public char Next() => _pos < text.Length ? text[_pos++] : '\0';

        public void SkipTrivia()
        {
            while (_pos < text.Length)
            {
                var c = text[_pos];
                if (char.IsWhiteSpace(c) || c == ',') { _pos++; continue; }
                if (c == '/' && _pos + 1 < text.Length && text[_pos + 1] == '/')
                {
                    while (_pos < text.Length && text[_pos] != '\n') _pos++;
                    continue;
                }
                if (c == '/' && _pos + 1 < text.Length && text[_pos + 1] == '*')
                {
                    var end = text.IndexOf("*/", _pos + 2, StringComparison.Ordinal);
                    _pos = end < 0 ? text.Length : end + 2;
                    continue;
                }
                break;
            }
        }

        public Dictionary<string, object?> ReadObjectBody(char close)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            while (true)
            {
                SkipTrivia();
                var c = Peek();
                if (c == '\0') return result; // also tolerates a missing closing brace
                if (c == close) { Next(); return result; }
                var key = ReadKey();
                SkipTrivia();
                if (Peek() is '=' or ':') Next();
                SkipTrivia();
                result[key] = ReadValue();
            }
        }

        private string ReadKey()
        {
            if (Peek() == '"') return ReadString();
            var sb = new StringBuilder();
            while (_pos < text.Length && !char.IsWhiteSpace(Peek()) && Peek() is not ('=' or ':' or '{' or '}' or '[' or ']' or ','))
                sb.Append(Next());
            if (sb.Length == 0) throw new FormatException($"SJSON: Schlüssel erwartet bei Position {_pos}");
            return sb.ToString();
        }

        private object? ReadValue()
        {
            var c = Peek();
            switch (c)
            {
                case '{':
                    Next();
                    return ReadObjectBody('}');
                case '[':
                    Next();
                    var list = new List<object?>();
                    while (true)
                    {
                        SkipTrivia();
                        if (Peek() == ']') { Next(); return list; }
                        if (Peek() == '\0') return list;
                        list.Add(ReadValue());
                    }
                case '"':
                    return ReadString();
                default:
                    var sb = new StringBuilder();
                    while (_pos < text.Length && !char.IsWhiteSpace(Peek()) && Peek() is not (',' or '}' or ']'))
                        sb.Append(Next());
                    var word = sb.ToString();
                    if (word.Length == 0) { Next(); return null; }
                    return word switch
                    {
                        "true" => true,
                        "false" => false,
                        "null" => null,
                        _ => double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : word,
                    };
            }
        }

        private string ReadString()
        {
            Next(); // opening quote
            var sb = new StringBuilder();
            while (_pos < text.Length)
            {
                var c = Next();
                if (c == '"') break;
                if (c == '\\' && _pos < text.Length)
                {
                    var e = Next();
                    sb.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => e });
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
