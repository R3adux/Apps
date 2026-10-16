namespace Stratagem.Core.Recognition;

/// <summary>A captured image, 32-bit BGRA (the layout Windows screen captures deliver), rows without padding.</summary>
public sealed class PixelImage
{
    public PixelImage(int width, int height, byte[] bgra)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
            throw new ArgumentException("Bildgröße passt nicht zu den Daten.");
        Width = width;
        Height = height;
        Bgra = bgra;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }

    /// <summary>Perceived brightness 0..255.</summary>
    public int Luma(int x, int y)
    {
        var i = (y * Width + x) * 4;
        return (Bgra[i] * 29 + Bgra[i + 1] * 150 + Bgra[i + 2] * 77) >> 8;
    }

    public (byte R, byte G, byte B) Rgb(int x, int y)
    {
        var i = (y * Width + x) * 4;
        return (Bgra[i + 2], Bgra[i + 1], Bgra[i]);
    }

    public PixelImage Crop(int x, int y, int width, int height)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        width = Math.Clamp(width, 1, Width - x);
        height = Math.Clamp(height, 1, Height - y);
        var data = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(Bgra, ((y + row) * Width + x) * 4, data, row * width * 4, width * 4);
        return new PixelImage(width, height, data);
    }
}
