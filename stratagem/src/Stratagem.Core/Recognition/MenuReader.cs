using Stratagem.Core.Model;

namespace Stratagem.Core.Recognition;

/// <summary>What a row of the in-game stratagem list says.</summary>
public enum RowState
{
    /// <summary>Arrows are shown: can be called right now.</summary>
    Callable,
    /// <summary>Title dimmed: cooldown (or blocked).</summary>
    Cooldown,
    /// <summary>Title highlighted: just called, on its way ("Ankunft in …", "Aktivierung").</summary>
    Active,
    /// <summary>Normal title but no arrows, e.g. Reinforce "Bereit" when nobody needs reinforcing.</summary>
    Idle,
}

/// <param name="IconHash">Identity of the icon (stays the same while the stratagem stays the same).</param>
/// <param name="Icon">The icon cut out of the screen (PNG).</param>
public sealed record MenuRow(int Index, RowState State, StratagemCode? Code, ulong IconHash, byte[] Icon);

/// <summary>
/// Where the list is on screen, in pixels of a 1920×1080 picture with 100 % HUD scale. Everything scales with the
/// picture height (the list sits at the left edge, also on wide screens). Measured on real screenshots (Oct 2026).
/// Kept as data so a game patch only needs new numbers.
/// </summary>
public sealed record MenuGeometry(
    double IconLeft = 50, double IconRight = 98,
    double FirstRowSearchTop = 60, double FirstRowSearchBottom = 220,
    double RowPitch = 52.3, double MinIconHeight = 30,
    double TextLeft = 115, double TextRight = 330,
    int MaxRows = 14)
{
    public static MenuGeometry Default { get; } = new();
}

/// <summary>
/// Reads the in-game stratagem list ("TAKTIKAUSRÜSTUNGEN", top left while the list is open) from a picture.
/// Language-independent: no text is read. Per row:
/// - the arrows (light shapes of about 15×15 px; the direction by comparing the shape with an arrow template
///   in all four rotations – robust against the hazy, changing background behind the half-transparent list),
/// - the state (arrows shown = callable; otherwise from the brightness of the title),
/// - the icon (cut out, plus a hash as identity: the game shows no arrows during cooldown, so the code of an
///   icon is remembered once it was seen with arrows).
/// Costs about a millisecond for the list area.
/// </summary>
public static class MenuReader
{
    public static IReadOnlyList<MenuRow> Read(PixelImage image, double scale, MenuGeometry? geometry = null)
    {
        var g = geometry ?? MenuGeometry.Default;
        int S(double v) => (int)Math.Round(v * scale);
        int iconL = S(g.IconLeft), iconR = Math.Min(S(g.IconRight), image.Width - 1);
        if (iconR <= iconL) return [];

        // 1. First row: the first run of "busy" lines in the icon column (icons have structure, the panel is flat).
        var first = -1;
        var iconHeight = 0;
        for (int y = S(g.FirstRowSearchTop), run = 0; y < Math.Min(S(g.FirstRowSearchBottom), image.Height); y++)
        {
            if (Busy(image, y, iconL, iconR)) run++;
            else
            {
                if (run >= S(g.MinIconHeight)) { first = y - run; iconHeight = run; break; }
                run = 0;
            }
        }
        if (first < 0) return [];
        iconHeight = Math.Min(iconHeight, S(g.RowPitch) - S(4));

        // 2. Further rows on the fixed pitch, as long as the icon column is busy there.
        var rows = new List<MenuRow>();
        for (var k = 0; k < g.MaxRows; k++)
        {
            var top = first + (int)Math.Round(k * g.RowPitch * scale);
            var bottom = top + iconHeight;
            if (bottom + S(6) >= image.Height) break;
            var busy = 0;
            for (var y = top + iconHeight / 4; y < bottom - iconHeight / 4; y++) if (Busy(image, y, iconL, iconR)) busy++;
            if (busy < iconHeight / 4) break;
            rows.Add(ReadRow(image, k, top, bottom, iconL, iconR, S(g.TextLeft), Math.Min(S(g.TextRight), image.Width), scale));
        }
        return rows;
    }

    private static MenuRow ReadRow(PixelImage img, int index, int top, int bottom, int iconL, int iconR, int textL, int textR, double scale)
    {
        var h = bottom - top;
        int lineTop = top + (int)(h * 0.52), lineBottom = Math.Min(bottom + (int)Math.Round(4 * scale), img.Height);
        var bg = Median(img, textL, textR, lineTop, lineBottom);

        var blobs = Blobs(img, textL, textR, lineTop, lineBottom, bg + 45);
        double min = 11 * scale, max = 23 * scale;
        var arrows = blobs.Count >= 2 && blobs.All(b => b.Width >= min && b.Height >= min && b.Width <= max && b.Height <= max);

        StratagemCode? code = null;
        RowState state;
        if (arrows)
        {
            code = new StratagemCode(blobs.OrderBy(b => b.Left).Select(b => ArrowDirection(img, b, textL, lineTop, bg)));
            state = RowState.Callable;
        }
        else
        {
            var titleBg = Median(img, textL, textR, top, top + (int)(h * 0.45));
            var contrast = Percentile(img, textL, textR, top, top + (int)(h * 0.45), 0.95) - titleBg;
            state = contrast > 105 ? RowState.Active : contrast < 68 ? RowState.Cooldown : RowState.Idle;
        }

        var icon = img.Crop(iconL, top, iconR - iconL, h);
        return new MenuRow(index, state, code, IconHash(icon), Png.Encode(icon));
    }

    /// <summary>
    /// 64-bit difference hash of the icon: per row of a 9×8 grid, is the next cell brighter? During a cooldown the
    /// game lays a half-transparent white fill over the icon (rising with the remaining time); that lifts whole rows
    /// evenly and keeps the left/right order, so the hash stays. The number badge (bottom right) and the bars
    /// below the icon change while playing and are left out.
    /// </summary>
    internal static ulong IconHash(PixelImage icon)
    {
        int ox = (int)(icon.Width * 0.08), oy = (int)(icon.Height * 0.08);
        int w = (int)(icon.Width * 0.84), h = (int)(icon.Height * 0.58);
        Span<int> cells = stackalloc int[9 * 8];
        for (var cy = 0; cy < 8; cy++)
        for (var cx = 0; cx < 9; cx++)
        {
            int sum = 0, n = 0;
            for (var y = oy + cy * h / 8; y < oy + (cy + 1) * h / 8; y++)
            for (var x = ox + cx * w / 9; x < ox + (cx + 1) * w / 9; x++) { sum += icon.Luma(x, y); n++; }
            cells[cy * 9 + cx] = n == 0 ? 0 : sum / n;
        }
        ulong hash = 0;
        for (var cy = 0; cy < 8; cy++)
        for (var cx = 0; cx < 8; cx++)
            if (cells[cy * 9 + cx + 1] > cells[cy * 9 + cx]) hash |= 1UL << (cy * 8 + cx);
        return hash;
    }

    private const int N = 12;

    /// <summary>
    /// The game's arrow glyph pointing up, 12×12, averaged from real screenshots (1080p).
    /// The other directions are rotations of it.
    /// </summary>
    private static readonly double[] UpTemplate =
    [
        0.004, 0.003, 0.003, 0.012, 0.187, 0.601, 0.609, 0.205, 0.006, 0.002, 0.004, 0.004,
        0.004, 0.003, 0.004, 0.106, 0.489, 0.846, 0.839, 0.509, 0.114, 0.002, 0.002, 0.004,
        0.004, 0.003, 0.041, 0.348, 0.788, 0.937, 0.926, 0.789, 0.398, 0.078, 0.001, 0.004,
        0.005, 0.032, 0.268, 0.684, 0.930, 0.945, 0.946, 0.925, 0.731, 0.316, 0.036, 0.002,
        0.072, 0.382, 0.778, 0.930, 0.943, 0.945, 0.945, 0.947, 0.939, 0.789, 0.408, 0.085,
        0.278, 0.685, 0.915, 0.943, 0.941, 0.943, 0.947, 0.945, 0.945, 0.912, 0.716, 0.330,
        0.690, 0.921, 0.942, 0.943, 0.941, 0.942, 0.946, 0.946, 0.947, 0.941, 0.930, 0.730,
        0.670, 0.709, 0.735, 0.847, 0.939, 0.945, 0.946, 0.944, 0.934, 0.830, 0.726, 0.669,
        0.016, 0.015, 0.108, 0.543, 0.938, 0.946, 0.942, 0.935, 0.895, 0.496, 0.073, 0.017,
        0.003, 0.003, 0.096, 0.539, 0.938, 0.947, 0.944, 0.935, 0.889, 0.479, 0.053, 0.002,
        0.004, 0.004, 0.100, 0.542, 0.939, 0.948, 0.946, 0.935, 0.887, 0.477, 0.051, 0.002,
        0.005, 0.005, 0.106, 0.506, 0.849, 0.862, 0.858, 0.847, 0.806, 0.436, 0.046, 0.001
    ];

    private static readonly (Direction Direction, double[] Template)[] Templates = BuildTemplates();

    private static (Direction, double[])[] BuildTemplates()
    {
        var up = UpTemplate;
        var right = Rotate(up);
        var down = Rotate(right);
        var left = Rotate(down);
        return [(Direction.Up, up), (Direction.Right, right), (Direction.Down, down), (Direction.Left, left)];

        // 90° clockwise
        static double[] Rotate(double[] m)
        {
            var r = new double[N * N];
            for (var y = 0; y < N; y++) for (var x = 0; x < N; x++) r[y * N + x] = m[(N - 1 - x) * N + y];
            return r;
        }
    }

    /// <summary>The blob's brightness, scaled to N×N, compared with the template in all four directions.</summary>
    private static Direction ArrowDirection(PixelImage img, Blob blob, int offsetX, int offsetY, int bg)
    {
        int l = offsetX + blob.Left, t = offsetY + blob.Top, w = blob.Width, h = blob.Height;
        var peak = 0;
        foreach (var (x, y) in blob.Points) peak = Math.Max(peak, img.Luma(offsetX + x, offsetY + y));
        double range = Math.Max(1, peak - bg);
        var m = new double[N * N];
        for (var j = 0; j < N; j++)
        for (var i = 0; i < N; i++)
        {
            int xa = l + i * w / N, xb = Math.Max(xa + 1, l + (i + 1) * w / N);
            int ya = t + j * h / N, yb = Math.Max(ya + 1, t + (j + 1) * h / N);
            double sum = 0;
            var n = 0;
            for (var y = ya; y < yb; y++)
            for (var x = xa; x < xb; x++)
            {
                sum += Math.Clamp((img.Luma(Math.Min(x, l + w - 1), Math.Min(y, t + h - 1)) - bg) / range, 0, 1);
                n++;
            }
            m[j * N + i] = sum / n;
        }
        var best = Direction.Up;
        var bestScore = double.MinValue;
        foreach (var (direction, template) in Templates)
        {
            var score = Correlation(m, template);
            if (score > bestScore) { bestScore = score; best = direction; }
        }
        return best;
    }

    private static double Correlation(double[] a, double[] b)
    {
        double ma = a.Average(), mb = b.Average(), num = 0, da = 0, db = 0;
        for (var i = 0; i < a.Length; i++)
        {
            num += (a[i] - ma) * (b[i] - mb);
            da += (a[i] - ma) * (a[i] - ma);
            db += (b[i] - mb) * (b[i] - mb);
        }
        return num / Math.Max(1e-9, Math.Sqrt(da * db));
    }

    public static int Distance(ulong a, ulong b) => System.Numerics.BitOperations.PopCount(a ^ b);

    private static bool Busy(PixelImage img, int y, int x0, int x1)
    {
        long sum = 0, sq = 0;
        var n = x1 - x0;
        for (var x = x0; x < x1; x++)
        {
            var l = img.Luma(x, y);
            sum += l;
            sq += l * l;
        }
        var variance = (sq - sum * sum / (double)n) / n;
        return variance > 64; // standard deviation > 8
    }

    private static int Median(PixelImage img, int x0, int x1, int y0, int y1)
    {
        var values = new List<int>();
        for (var y = y0; y < y1; y += 2) for (var x = x0; x < x1; x += 3) values.Add(img.Luma(x, y));
        if (values.Count == 0) return 0;
        values.Sort();
        return values[values.Count / 2];
    }

    private static int Percentile(PixelImage img, int x0, int x1, int y0, int y1, double p)
    {
        var values = new List<int>();
        for (var y = y0; y < y1; y += 2) for (var x = x0; x < x1; x += 2) values.Add(img.Luma(x, y));
        if (values.Count == 0) return 0;
        values.Sort();
        return values[(int)((values.Count - 1) * p)];
    }

    private sealed class Blob
    {
        public List<(int X, int Y)> Points { get; } = [];
        public int Left, Right, Top, Bottom;
        public int Width => Right - Left + 1;
        public int Height => Bottom - Top + 1;
    }

    private static List<Blob> Blobs(PixelImage img, int x0, int x1, int y0, int y1, int threshold)
    {
        int w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0) return [];
        var on = new bool[w * h];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) on[y * w + x] = img.Luma(x0 + x, y0 + y) > threshold;
        var seen = new bool[w * h];
        var result = new List<Blob>();
        var stack = new Stack<int>();
        for (var start = 0; start < on.Length; start++)
        {
            if (!on[start] || seen[start]) continue;
            var blob = new Blob { Left = int.MaxValue, Top = int.MaxValue, Right = -1, Bottom = -1 };
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                int px = p % w, py = p / w;
                blob.Points.Add((px, py));
                blob.Left = Math.Min(blob.Left, px); blob.Right = Math.Max(blob.Right, px);
                blob.Top = Math.Min(blob.Top, py); blob.Bottom = Math.Max(blob.Bottom, py);
                if (px > 0) Visit(p - 1);
                if (px < w - 1) Visit(p + 1);
                if (py > 0) Visit(p - w);
                if (py < h - 1) Visit(p + w);
            }
            if (blob.Points.Count >= 6) result.Add(blob);
        }
        return result;

        void Visit(int q)
        {
            if (!on[q] || seen[q]) return;
            seen[q] = true;
            stack.Push(q);
        }
    }
}
