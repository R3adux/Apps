using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Stratagem.Core.Recognition;

/// <summary>
/// Minimal PNG reading and writing (no image library needed): writes the stratagem icons cut out of the
/// screen, reads test pictures and pictures the player uploads for diagnosis. 8-bit RGB/RGBA, not interlaced.
/// </summary>
public static class Png
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] Encode(PixelImage image)
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < image.Height; y++)
        {
            raw.WriteByte(0); // filter: none
            for (var x = 0; x < image.Width; x++)
            {
                var i = (y * image.Width + x) * 4;
                raw.WriteByte(image.Bgra[i + 2]);
                raw.WriteByte(image.Bgra[i + 1]);
                raw.WriteByte(image.Bgra[i]);
                raw.WriteByte(image.Bgra[i + 3]);
            }
        }
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            raw.WriteTo(z);

        using var png = new MemoryStream();
        png.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    public static PixelImage Decode(byte[] data)
    {
        if (data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(Signature)) throw new FormatException("Keine PNG-Datei.");
        int width = 0, height = 0, colorType = 0;
        using var idat = new MemoryStream();
        var pos = 8;
        while (pos + 8 <= data.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
            var type = Encoding.ASCII.GetString(data, pos + 4, 4);
            var body = data.AsSpan(pos + 8, length);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(body);
                height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                if (body[8] != 8 || body[12] != 0) throw new FormatException("Nur 8-Bit-PNG ohne Interlacing.");
                colorType = body[9];
                if (colorType is not (2 or 6)) throw new FormatException("Nur RGB- oder RGBA-PNG.");
            }
            else if (type == "IDAT") idat.Write(body);
            else if (type == "IEND") break;
            pos += 12 + length;
        }
        var bpp = colorType == 6 ? 4 : 3;
        var stride = width * bpp;
        var raw = new byte[(stride + 1) * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            z.ReadExactly(raw);

        var bgra = new byte[width * height * 4];
        var prev = new byte[stride];
        var line = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            Array.Copy(raw, y * (stride + 1) + 1, line, 0, stride);
            for (var i = 0; i < stride; i++)
            {
                int a = i >= bpp ? line[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                line[i] = (byte)(line[i] + filter switch
                {
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => 0,
                });
            }
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                bgra[o] = line[x * bpp + 2];
                bgra[o + 1] = line[x * bpp + 1];
                bgra[o + 2] = line[x * bpp];
                bgra[o + 3] = bpp == 4 ? line[x * bpp + 3] : (byte)255;
            }
            (prev, line) = (line, prev);
        }
        return new PixelImage(width, height, bgra);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void Chunk(Stream s, string type, byte[] body)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, body.Length);
        s.Write(buf);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(body);
        var crc = Crc32(Crc32(0xFFFFFFFFu, typeBytes), body) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buf, crc);
        s.Write(buf);
    }
}
