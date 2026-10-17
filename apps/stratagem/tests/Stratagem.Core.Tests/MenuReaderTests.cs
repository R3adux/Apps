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

    // --- robustness: what matters is which rows are callable and their codes -------------------------

    private const string Callables = "- - UULUR - - - DURRU";

    private static string Functional(IReadOnlyList<MenuRow> rows) =>
        string.Join(" ", rows.Select(r => r.State == RowState.Callable ? r.Code!.Letters : "-"));

    public static TheoryData<string, string> Variants() => new()
    {
        { "menu-1080p-a.png", "540p" }, { "menu-1080p-a.png", "720p" }, { "menu-1080p-a.png", "1440p" }, { "menu-1080p-a.png", "4k" },
        { "menu-1080p-b.png", "540p" }, { "menu-1080p-b.png", "720p" }, { "menu-1080p-b.png", "1440p" }, { "menu-1080p-b.png", "4k" },
        { "menu-1080p-a.png", "brighter" }, { "menu-1080p-a.png", "darker" }, { "menu-1080p-a.png", "more contrast" }, { "menu-1080p-a.png", "less contrast" },
        { "menu-1080p-b.png", "brighter" }, { "menu-1080p-b.png", "darker" }, { "menu-1080p-b.png", "more contrast" }, { "menu-1080p-b.png", "less contrast" },
        { "menu-1080p-a.png", "gamma 0.8" }, { "menu-1080p-a.png", "gamma 1.25" }, { "menu-1080p-b.png", "gamma 0.8" }, { "menu-1080p-b.png", "gamma 1.25" },
        { "menu-1080p-a.png", "noise" }, { "menu-1080p-b.png", "noise" },
        { "menu-1080p-a.png", "shifted right" }, { "menu-1080p-a.png", "shifted left" }, { "menu-1080p-a.png", "shifted down" }, { "menu-1080p-a.png", "shifted up" },
        { "menu-1080p-b.png", "shifted right" }, { "menu-1080p-b.png", "shifted left" }, { "menu-1080p-b.png", "shifted down" }, { "menu-1080p-b.png", "shifted up" },
    };

    /// <summary>Other resolutions, HDR/gamma/brightness settings, a list a bit off its usual place.</summary>
    [Theory]
    [MemberData(nameof(Variants))]
    public void Reads_the_codes_in_other_conditions(string file, string variant)
    {
        var image = Load(file);
        var rnd = new Random(5);
        var (picture, scale) = variant switch
        {
            "540p" => (Resize(image, 0.5), 0.5),
            "720p" => (Resize(image, 2 / 3.0), 2 / 3.0),
            "1440p" => (Resize(image, 4 / 3.0), 4 / 3.0),
            "4k" => (Resize(image, 2.0), 2.0),
            "brighter" => (Map(image, v => v + 40), 1.0),
            "darker" => (Map(image, v => v - 40), 1.0),
            "more contrast" => (Map(image, v => (int)((v - 128) * 1.2 + 128)), 1.0),
            "less contrast" => (Map(image, v => (int)((v - 128) * 0.8 + 128)), 1.0),
            "gamma 0.8" => (Map(image, v => (int)(255 * Math.Pow(v / 255.0, 0.8))), 1.0),
            "gamma 1.25" => (Map(image, v => (int)(255 * Math.Pow(v / 255.0, 1.25))), 1.0),
            "noise" => (Map(image, v => v + rnd.Next(-12, 13)), 1.0),
            "shifted right" => (Shift(image, 12, 0), 1.0),
            "shifted left" => (Shift(image, -8, 0), 1.0),
            "shifted down" => (Shift(image, 0, 20), 1.0),
            "shifted up" => (Shift(image, 0, -15), 1.0),
            _ => throw new ArgumentException(variant),
        };
        Assert.Equal(Callables, Functional(MenuReader.Read(picture, scale)));
    }

    /// <summary>The HUD is a bit larger or smaller than assumed (other aspect ratio, odd resolution): measured from the picture.</summary>
    [Theory]
    [InlineData(0.8)]
    [InlineData(0.9)]
    [InlineData(0.95)]
    [InlineData(1.05)]
    [InlineData(1.1)]
    [InlineData(1.2)]
    [InlineData(1.3)]
    public void Finds_the_hud_size_itself(double assumed)
    {
        Assert.Equal(Callables, Functional(MenuReader.Read(Load("menu-1080p-a.png"), assumed)));
        Assert.Equal(Callables, Functional(MenuReader.Read(Load("menu-1080p-b.png"), assumed)));
    }

    /// <summary>
    /// Parts of the same screenshots without the list (mission objectives with icons and text, HUD, scenery):
    /// a code is typed into the game, so nothing here may ever look like one.
    /// </summary>
    [Theory]
    [InlineData("nolist-1.png")]
    [InlineData("nolist-2.png")]
    [InlineData("nolist-3.png")]
    [InlineData("nolist-4.png")]
    [InlineData("nolist-5.png")]
    [InlineData("nolist-6.png")]
    [InlineData("nolist-7.png")]
    [InlineData("nolist-8.png")]
    public void Sees_no_code_where_there_is_no_list(string file)
    {
        var image = Load(file);
        foreach (var scale in new[] { 2 / 3.0, 1.0, 4 / 3.0 })
            Assert.DoesNotContain(MenuReader.Read(image, scale), r => r.Code is not null);
    }

    /// <summary>
    /// A glow behind the list (explosion, flare) swallows the first or last arrow of "Eagle Rearm" (↑↑←↑→).
    /// A shorter code would be another stratagem or none: the row must then give no code at all – and not count as
    /// "on cooldown" either, because it might well be callable.
    /// </summary>
    [Theory]
    [InlineData(126, 8, 160)]
    [InlineData(126, 8, 120)]
    [InlineData(126, 8, 80)]
    [InlineData(212, 8, 160)]
    [InlineData(212, 10, 160)]
    [InlineData(169, 8, 160)]
    public void A_half_hidden_code_is_never_read_short(int x, double sigma, int strength)
    {
        var image = Load("menu-1080p-a.png");
        var data = (byte[])image.Bgra.Clone();
        const int y = 220;
        for (var py = 0; py < image.Height; py++)
        for (var px = 0; px < image.Width; px++)
        {
            var d2 = (px - x) * (px - x) + (py - y) * (py - y);
            var add = strength * Math.Exp(-d2 / (2 * sigma * sigma));
            if (add < 1) continue;
            for (var c = 0; c < 3; c++)
            {
                var i = (py * image.Width + px) * 4 + c;
                data[i] = (byte)Math.Min(255, data[i] + add);
            }
        }
        var row = MenuReader.Read(new PixelImage(image.Width, image.Height, data), 1.0)[2];
        Assert.True(row.Code is null || row.Code.Letters == "UULUR", $"falscher Code {row.Code?.Letters}");
        Assert.True(row.Code is not null || row.State == RowState.Unclear, $"Zustand {row.State}");
    }

    private static PixelImage Map(PixelImage src, Func<int, int> f)
    {
        var d = (byte[])src.Bgra.Clone();
        for (var i = 0; i < d.Length; i++) if (i % 4 != 3) d[i] = (byte)Math.Clamp(f(d[i]), 0, 255);
        return new PixelImage(src.Width, src.Height, d);
    }

    private static PixelImage Shift(PixelImage src, int dx, int dy)
    {
        var d = new byte[src.Bgra.Length];
        for (var y = 0; y < src.Height; y++)
        for (var x = 0; x < src.Width; x++)
        {
            int sx = Math.Clamp(x - dx, 0, src.Width - 1), sy = Math.Clamp(y - dy, 0, src.Height - 1);
            Array.Copy(src.Bgra, (sy * src.Width + sx) * 4, d, (y * src.Width + x) * 4, 4);
        }
        return new PixelImage(src.Width, src.Height, d);
    }

    /// <summary>Bilinear up, area average down.</summary>
    private static PixelImage Resize(PixelImage src, double f)
    {
        if (f < 1) return Downscale(src, f);
        int w = (int)Math.Round(src.Width * f), h = (int)Math.Round(src.Height * f);
        var dst = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            double sx = (x + 0.5) / f - 0.5, sy = (y + 0.5) / f - 0.5;
            int x0 = Math.Clamp((int)Math.Floor(sx), 0, src.Width - 1), y0 = Math.Clamp((int)Math.Floor(sy), 0, src.Height - 1);
            int x1 = Math.Min(x0 + 1, src.Width - 1), y1 = Math.Min(y0 + 1, src.Height - 1);
            double ax = Math.Clamp(sx - x0, 0, 1), ay = Math.Clamp(sy - y0, 0, 1);
            for (var c = 0; c < 4; c++)
            {
                double P(int xx, int yy) => src.Bgra[(yy * src.Width + xx) * 4 + c];
                var v = P(x0, y0) * (1 - ax) * (1 - ay) + P(x1, y0) * ax * (1 - ay) + P(x0, y1) * (1 - ax) * ay + P(x1, y1) * ax * ay;
                dst[(y * w + x) * 4 + c] = (byte)Math.Clamp(Math.Round(v), 0, 255);
            }
        }
        return new PixelImage(w, h, dst);
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
