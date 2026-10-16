using System.Text;

namespace Stratagem.Server;

/// <summary>
/// Minimal QR code encoder (byte mode, error correction level M, versions 1–10, up to 213 bytes):
/// enough for the phone link. Follows ISO/IEC 18004; structure after Project Nayuki's reference encoder.
/// </summary>
public sealed class QrCode
{
    private static readonly int[] EccPerBlock = [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26]; // level M
    private static readonly int[] Blocks = [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5]; // level M
    private const int MaxVersion = 10;
    private const int FormatLevelM = 0;

    private readonly bool[,] _dark;
    private readonly bool[,] _function;

    public int Version { get; }
    public int Size { get; }
    public int Mask { get; private set; }

    public bool this[int x, int y] => _dark[y, x];

    private QrCode(int version, byte[] codewords, int? mask)
    {
        Version = version;
        Size = version * 4 + 17;
        _dark = new bool[Size, Size];
        _function = new bool[Size, Size];
        DrawFunctionPatterns();
        DrawCodewords(AddEccAndInterleave(codewords));

        if (mask is { } fixedMask) Mask = fixedMask;
        else
        {
            var best = int.MaxValue;
            for (var m = 0; m < 8; m++)
            {
                ApplyMask(m);
                DrawFormatBits(m);
                var penalty = Penalty();
                if (penalty < best) { best = penalty; Mask = m; }
                ApplyMask(m); // XOR again = undo
            }
        }
        ApplyMask(Mask);
        DrawFormatBits(Mask);
    }

    public static QrCode Encode(string text, int? mask = null)
    {
        var data = Encoding.UTF8.GetBytes(text);
        for (var version = 1; version <= MaxVersion; version++)
        {
            var capacity = DataCodewords(version) * 8;
            var countBits = version <= 9 ? 8 : 16;
            if (4 + countBits + data.Length * 8 > capacity) continue;

            var bits = new List<bool>(capacity);
            Append(bits, 0b0100, 4); // byte mode
            Append(bits, data.Length, countBits);
            foreach (var b in data) Append(bits, b, 8);
            Append(bits, 0, Math.Min(4, capacity - bits.Count)); // terminator
            Append(bits, 0, (8 - bits.Count % 8) % 8);
            for (var pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) Append(bits, pad, 8);

            var codewords = new byte[bits.Count / 8];
            for (var i = 0; i < bits.Count; i++)
                if (bits[i]) codewords[i >> 3] |= (byte)(0x80 >> (i & 7));
            return new QrCode(version, codewords, mask);
        }
        throw new ArgumentException($"Text zu lang für einen QR-Code ({data.Length} Bytes)", nameof(text));
    }

    /// <summary>SVG with a white quiet zone of 4 modules; scales to any size.</summary>
    public string ToSvg()
    {
        const int border = 4;
        var full = Size + border * 2;
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {full} {full}\" shape-rendering=\"crispEdges\">");
        sb.Append($"<rect width=\"{full}\" height=\"{full}\" fill=\"#fff\"/><path fill=\"#000\" d=\"");
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
                if (_dark[y, x]) sb.Append($"M{x + border},{y + border}h1v1h-1z");
        sb.Append("\"/></svg>");
        return sb.ToString();
    }

    private static void Append(List<bool> bits, int value, int count)
    {
        for (var i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
    }

    private static int RawDataModules(int version)
    {
        var result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            var align = version / 7 + 2;
            result -= (25 * align - 10) * align - 55;
            if (version >= 7) result -= 36;
        }
        return result;
    }

    private static int DataCodewords(int version) => RawDataModules(version) / 8 - EccPerBlock[version] * Blocks[version];

    // --- error correction -------------------------------------------------------------------------

    private byte[] AddEccAndInterleave(byte[] data)
    {
        var blocks = Blocks[Version];
        var eccLen = EccPerBlock[Version];
        var raw = RawDataModules(Version) / 8;
        var shortBlocks = blocks - raw % blocks;
        var shortLen = raw / blocks;
        var divisor = RsDivisor(eccLen);

        var all = new List<byte[]>();
        for (int i = 0, k = 0; i < blocks; i++)
        {
            var len = shortLen - eccLen + (i < shortBlocks ? 0 : 1);
            var dat = data.AsSpan(k, len).ToArray();
            k += len;
            var ecc = RsRemainder(dat, divisor);
            var block = new byte[shortLen + 1];
            dat.CopyTo(block, 0);
            ecc.CopyTo(block, block.Length - eccLen); // short blocks keep a gap before the ECC
            all.Add(block);
        }

        var result = new List<byte>(raw);
        for (var i = 0; i < shortLen + 1; i++)
            for (var j = 0; j < all.Count; j++)
                if (i != shortLen - eccLen || j >= shortBlocks) result.Add(all[j][i]);
        return result.ToArray();
    }

    private static byte[] RsDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        var root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = Multiply(root, 0x02);
        }
        return result;
    }

    private static byte[] RsRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            var factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < result.Length; i++) result[i] ^= Multiply(divisor[i], factor);
        }
        return result;
    }

    private static byte Multiply(int x, int y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }

    // --- drawing ----------------------------------------------------------------------------------

    private void SetFunction(int x, int y, bool dark)
    {
        _dark[y, x] = dark;
        _function[y, x] = true;
    }

    private void DrawFunctionPatterns()
    {
        for (var i = 0; i < Size; i++)
        {
            SetFunction(6, i, i % 2 == 0);
            SetFunction(i, 6, i % 2 == 0);
        }
        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);

        var align = AlignmentPositions();
        for (var i = 0; i < align.Length; i++)
            for (var j = 0; j < align.Length; j++)
                if (!((i == 0 && j == 0) || (i == 0 && j == align.Length - 1) || (i == align.Length - 1 && j == 0)))
                    DrawAlignment(align[i], align[j]);

        DrawFormatBits(0); // reserves the area; the real bits follow after masking
        DrawVersion();
    }

    private void DrawFinder(int cx, int cy)
    {
        for (var dy = -4; dy <= 4; dy++)
            for (var dx = -4; dx <= 4; dx++)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || x >= Size || y < 0 || y >= Size) continue;
                var dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                SetFunction(x, y, dist != 2 && dist != 4);
            }
    }

    private void DrawAlignment(int cx, int cy)
    {
        for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
                SetFunction(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    private int[] AlignmentPositions()
    {
        if (Version == 1) return [];
        var count = Version / 7 + 2;
        var step = (Version * 8 + count * 3 + 5) / (count * 4 - 4) * 2;
        var result = new int[count];
        result[0] = 6;
        for (int i = count - 1, pos = Size - 7; i >= 1; i--, pos -= step) result[i] = pos;
        return result;
    }

    private void DrawFormatBits(int mask)
    {
        var data = FormatLevelM << 3 | mask;
        var rem = data;
        for (var i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        var bits = (data << 10 | rem) ^ 0x5412;
        bool Bit(int i) => ((bits >> i) & 1) != 0;

        for (var i = 0; i <= 5; i++) SetFunction(8, i, Bit(i));
        SetFunction(8, 7, Bit(6));
        SetFunction(8, 8, Bit(7));
        SetFunction(7, 8, Bit(8));
        for (var i = 9; i < 15; i++) SetFunction(14 - i, 8, Bit(i));

        for (var i = 0; i < 8; i++) SetFunction(Size - 1 - i, 8, Bit(i));
        for (var i = 8; i < 15; i++) SetFunction(8, Size - 15 + i, Bit(i));
        SetFunction(8, Size - 8, true); // always dark
    }

    private void DrawVersion()
    {
        if (Version < 7) return;
        var rem = Version;
        for (var i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        var bits = Version << 12 | rem;
        for (var i = 0; i < 18; i++)
        {
            var bit = ((bits >> i) & 1) != 0;
            int a = Size - 11 + i % 3, b = i / 3;
            SetFunction(a, b, bit);
            SetFunction(b, a, bit);
        }
    }

    private void DrawCodewords(byte[] data)
    {
        var i = 0;
        for (var right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5; // skip the vertical timing line
            for (var vert = 0; vert < Size; vert++)
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? Size - 1 - vert : vert;
                    if (_function[y, x] || i >= data.Length * 8) continue;
                    _dark[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                    i++;
                }
        }
    }

    private void ApplyMask(int mask)
    {
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                if (_function[y, x]) continue;
                var invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (invert) _dark[y, x] = !_dark[y, x];
            }
    }

    // Penalty rules 1, 2 and 4 of the standard (rule 3, finder-like runs, only fine-tunes the choice;
    // every mask is valid for a reader).
    private int Penalty()
    {
        var result = 0;
        for (var a = 0; a < Size; a++)
        {
            int runRow = 1, runCol = 1;
            for (var b = 1; b < Size; b++)
            {
                if (_dark[a, b] == _dark[a, b - 1]) { runRow++; if (runRow == 5) result += 3; else if (runRow > 5) result++; }
                else runRow = 1;
                if (_dark[b, a] == _dark[b - 1, a]) { runCol++; if (runCol == 5) result += 3; else if (runCol > 5) result++; }
                else runCol = 1;
            }
        }
        for (var y = 0; y < Size - 1; y++)
            for (var x = 0; x < Size - 1; x++)
            {
                var c = _dark[y, x];
                if (c == _dark[y, x + 1] && c == _dark[y + 1, x] && c == _dark[y + 1, x + 1]) result += 3;
            }
        var dark = 0;
        foreach (var d in _dark) if (d) dark++;
        var total = Size * Size;
        var k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        result += Math.Max(0, k) * 10;
        return result;
    }
}
