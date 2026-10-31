using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Licht.Fenster
{
    /// <summary>
    /// Small, strict JSON reader and writer for the messages with Licht (no extra library needed).
    /// Objects are <see cref="Dictionary{TKey,TValue}"/> (string → object?), arrays <see cref="List{T}"/> of object?,
    /// numbers double, plus string, bool and null.
    /// </summary>
    public static class Json
    {
        public static object? Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var reader = new Reader(text);
            reader.SkipSpace();
            var value = reader.Value(0);
            reader.SkipSpace();
            if (!reader.AtEnd) throw reader.Error("Text nach dem Ende");
            return value;
        }

        public static string Write(object? value)
        {
            var builder = new StringBuilder();
            WriteValue(builder, value, 0);
            return builder.ToString();
        }

        private static void WriteValue(StringBuilder b, object? value, int depth)
        {
            if (depth > 64) throw new FormatException("JSON zu tief verschachtelt");
            switch (value)
            {
                case null: b.Append("null"); break;
                case string s: WriteString(b, s); break;
                case bool flag: b.Append(flag ? "true" : "false"); break;
                case double d: WriteNumber(b, d); break;
                case float f: WriteNumber(b, f); break;
                case int or long or short or byte or uint or ushort or ulong or sbyte:
                    b.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
                case decimal m: b.Append(m.ToString(CultureInfo.InvariantCulture)); break;
                case IDictionary<string, object?> map:
                    b.Append('{');
                    var first = true;
                    foreach (var pair in map)
                    {
                        if (!first) b.Append(',');
                        first = false;
                        WriteString(b, pair.Key);
                        b.Append(':');
                        WriteValue(b, pair.Value, depth + 1);
                    }
                    b.Append('}');
                    break;
                case IEnumerable list:
                    b.Append('[');
                    var firstItem = true;
                    foreach (var item in list)
                    {
                        if (!firstItem) b.Append(',');
                        firstItem = false;
                        WriteValue(b, item, depth + 1);
                    }
                    b.Append(']');
                    break;
                default: throw new FormatException($"Nicht als JSON darstellbar: {value.GetType().Name}");
            }
        }

        private static void WriteNumber(StringBuilder b, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) throw new FormatException("Zahl nicht als JSON darstellbar");
            b.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder b, string s)
        {
            b.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    case '\b': b.Append("\\b"); break;
                    case '\f': b.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029) b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else b.Append(c);
                        break;
                }
            }
            b.Append('"');
        }

        private sealed class Reader
        {
            private readonly string _text;
            private int _pos;

            public Reader(string text) => _text = text;

            public bool AtEnd => _pos >= _text.Length;

            public FormatException Error(string what) => new FormatException($"Ungültiges JSON ({what}) an Stelle {_pos}");

            public void SkipSpace()
            {
                while (_pos < _text.Length && (_text[_pos] == ' ' || _text[_pos] == '\t' || _text[_pos] == '\n' || _text[_pos] == '\r')) _pos++;
            }

            public object? Value(int depth)
            {
                if (depth > 64) throw Error("zu tief verschachtelt");
                if (AtEnd) throw Error("Ende");
                var c = _text[_pos];
                switch (c)
                {
                    case '{': return Object(depth);
                    case '[': return Array(depth);
                    case '"': return String();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return Number();
                        throw Error($"unerwartetes Zeichen '{c}'");
                }
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(_text, _pos, word, 0, word.Length) != 0) throw Error($"erwartet {word}");
                _pos += word.Length;
            }

            private Dictionary<string, object?> Object(int depth)
            {
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                _pos++; // {
                SkipSpace();
                if (!AtEnd && _text[_pos] == '}')
                {
                    _pos++;
                    return map;
                }
                while (true)
                {
                    SkipSpace();
                    if (AtEnd || _text[_pos] != '"') throw Error("Name erwartet");
                    var key = String();
                    SkipSpace();
                    if (AtEnd || _text[_pos] != ':') throw Error("':' erwartet");
                    _pos++;
                    SkipSpace();
                    map[key] = Value(depth + 1);
                    SkipSpace();
                    if (AtEnd) throw Error("Ende im Objekt");
                    if (_text[_pos] == ',') { _pos++; continue; }
                    if (_text[_pos] == '}') { _pos++; return map; }
                    throw Error("',' oder '}' erwartet");
                }
            }

            private List<object?> Array(int depth)
            {
                var list = new List<object?>();
                _pos++; // [
                SkipSpace();
                if (!AtEnd && _text[_pos] == ']')
                {
                    _pos++;
                    return list;
                }
                while (true)
                {
                    SkipSpace();
                    list.Add(Value(depth + 1));
                    SkipSpace();
                    if (AtEnd) throw Error("Ende in der Liste");
                    if (_text[_pos] == ',') { _pos++; continue; }
                    if (_text[_pos] == ']') { _pos++; return list; }
                    throw Error("',' oder ']' erwartet");
                }
            }

            private string String()
            {
                _pos++; // "
                var b = new StringBuilder();
                while (true)
                {
                    if (AtEnd) throw Error("Ende im Text");
                    var c = _text[_pos++];
                    if (c == '"') return b.ToString();
                    if (c < 0x20) throw Error("Steuerzeichen im Text");
                    if (c != '\\')
                    {
                        b.Append(c);
                        continue;
                    }
                    if (AtEnd) throw Error("Ende im Text");
                    var e = _text[_pos++];
                    switch (e)
                    {
                        case '"': b.Append('"'); break;
                        case '\\': b.Append('\\'); break;
                        case '/': b.Append('/'); break;
                        case 'b': b.Append('\b'); break;
                        case 'f': b.Append('\f'); break;
                        case 'n': b.Append('\n'); break;
                        case 'r': b.Append('\r'); break;
                        case 't': b.Append('\t'); break;
                        case 'u':
                            if (_pos + 4 > _text.Length) throw Error("\\u unvollständig");
                            if (!int.TryParse(_text.Substring(_pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                                throw Error("\\u ungültig");
                            b.Append((char)code);
                            _pos += 4;
                            break;
                        default: throw Error($"unbekanntes Escape \\{e}");
                    }
                }
            }

            private double Number()
            {
                var start = _pos;
                if (_text[_pos] == '-') _pos++;
                if (AtEnd) throw Error("Zahl unvollständig");
                if (_text[_pos] == '0') _pos++;
                else if (_text[_pos] >= '1' && _text[_pos] <= '9') while (!AtEnd && char.IsDigit(_text[_pos])) _pos++;
                else throw Error("Zahl erwartet");
                if (!AtEnd && _text[_pos] == '.')
                {
                    _pos++;
                    if (AtEnd || !char.IsDigit(_text[_pos])) throw Error("Ziffer erwartet");
                    while (!AtEnd && char.IsDigit(_text[_pos])) _pos++;
                }
                if (!AtEnd && (_text[_pos] == 'e' || _text[_pos] == 'E'))
                {
                    _pos++;
                    if (!AtEnd && (_text[_pos] == '+' || _text[_pos] == '-')) _pos++;
                    if (AtEnd || !char.IsDigit(_text[_pos])) throw Error("Exponent erwartet");
                    while (!AtEnd && char.IsDigit(_text[_pos])) _pos++;
                }
                return double.Parse(_text.Substring(start, _pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>Reading values out of parsed JSON objects without casts everywhere.</summary>
    public static class JsonRead
    {
        public static string? Str(this IDictionary<string, object?> o, string key) =>
            o.TryGetValue(key, out var v) ? v as string ?? (v is double d ? d.ToString(CultureInfo.InvariantCulture) : null) : null;

        public static double? Num(this IDictionary<string, object?> o, string key) =>
            o.TryGetValue(key, out var v) && v is double d ? d : (double?)null;

        public static int Int(this IDictionary<string, object?> o, string key, int fallback = 0) =>
            o.Num(key) is double d ? (int)Math.Round(d) : fallback;

        public static bool Bool(this IDictionary<string, object?> o, string key, bool fallback = false) =>
            o.TryGetValue(key, out var v) && v is bool b ? b : fallback;

        public static IDictionary<string, object?>? Obj(this IDictionary<string, object?> o, string key) =>
            o.TryGetValue(key, out var v) ? v as IDictionary<string, object?> : null;

        public static IList<object?>? Arr(this IDictionary<string, object?> o, string key) =>
            o.TryGetValue(key, out var v) ? v as IList<object?> : null;
    }
}
