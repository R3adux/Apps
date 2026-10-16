using Stratagem.Core.Recognition;

namespace Stratagem.Core.Tests;

/// <summary>Two real screenshots (1920×1080, German UI, list opened with "Drücken"), cropped to the list area.</summary>
public class MenuReaderTests
{
    private static PixelImage Load(string name) =>
        Png.Decode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", name)));

    private static string Describe(IReadOnlyList<MenuRow> rows) =>
        string.Join(" | ", rows.Select(r => r.Code is { } c ? $"{r.State}:{c.Letters}" : r.State.ToString()));

    [Theory]
    [InlineData("menu-1080p-a.png")] // Eagle 500 kg arriving, fire tornado behind the list
    [InlineData("menu-1080p-b.png")] // Eagle 500 kg activating, brighter background
    public void Reads_rows_codes_and_states(string file)
    {
        var rows = MenuReader.Read(Load(file), scale: 1.0);
        // Reinforce (ready, nobody to reinforce) | Resupply (cooldown) | Eagle Rearm ↑↑←↑→ | Eagle 500 kg (in use)
        // | Orbital Laser (cooldown) | Warp pack (cooldown) | MG sentry ↓↑→→↑
        Assert.Equal("Idle | Cooldown | Callable:UULUR | Active | Cooldown | Cooldown | Callable:DURRU", Describe(rows));
    }

    [Fact]
    public void Icons_keep_their_identity_between_pictures()
    {
        // The icons change with the state (cooldown fill, highlight, number badge); the nearest icon in the
        // other picture must still be the same stratagem.
        var a = MenuReader.Read(Load("menu-1080p-a.png"), 1.0);
        var b = MenuReader.Read(Load("menu-1080p-b.png"), 1.0);
        for (var i = 0; i < a.Count; i++)
        {
            var nearest = Enumerable.Range(0, b.Count).MinBy(j => MenuReader.Distance(a[i].IconHash, b[j].IconHash));
            Assert.True(nearest == i, $"Zeile {i}: nächstes Icon ist Zeile {nearest}");
        }
    }

    [Fact]
    public void Icons_are_cut_out_as_png()
    {
        var row = MenuReader.Read(Load("menu-1080p-a.png"), 1.0)[6];
        var icon = Png.Decode(row.Icon);
        Assert.InRange(icon.Width, 40, 56);
        Assert.InRange(icon.Height, 36, 50);
    }

    [Fact]
    public void Scales_with_the_resolution()
    {
        // The same picture at 75 % (≈ 1440×810) must read the same.
        var full = Load("menu-1080p-a.png");
        var small = Downscale(full, 0.75);
        var rows = MenuReader.Read(small, scale: 0.75);
        Assert.Equal("Idle | Cooldown | Callable:UULUR | Active | Cooldown | Cooldown | Callable:DURRU", Describe(rows));
    }

    [Fact]
    public void Empty_picture_gives_no_rows()
    {
        var black = new PixelImage(480, 560, new byte[480 * 560 * 4]);
        Assert.Empty(MenuReader.Read(black, 1.0));
    }

    [Fact]
    public void Png_round_trip()
    {
        var img = Load("menu-1080p-b.png").Crop(10, 20, 33, 17);
        var again = Png.Decode(Png.Encode(img));
        Assert.Equal(img.Bgra, again.Bgra);
    }

    private static PixelImage Downscale(PixelImage src, double f)
    {
        int w = (int)(src.Width * f), h = (int)(src.Height * f);
        var data = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            // box filter over the source pixels this pixel covers
            int x0 = (int)(x / f), x1 = Math.Max(x0 + 1, (int)((x + 1) / f)), y0 = (int)(y / f), y1 = Math.Max(y0 + 1, (int)((y + 1) / f));
            for (var c = 0; c < 4; c++)
            {
                int sum = 0, n = 0;
                for (var yy = y0; yy < y1; yy++) for (var xx = x0; xx < x1; xx++) { sum += src.Bgra[(yy * src.Width + xx) * 4 + c]; n++; }
                data[(y * w + x) * 4 + c] = (byte)(sum / n);
            }
        }
        return new PixelImage(w, h, data);
    }
}
