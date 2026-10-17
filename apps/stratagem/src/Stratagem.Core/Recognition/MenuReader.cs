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
    /// <summary>
    /// Something arrow-like that could not be read safely (half hidden by a glow behind the list), or nothing at all
    /// on the line. Neither a code nor proof that the stratagem is not callable.
    /// </summary>
    Unclear,
}

/// <param name="IconHash">Identity of the icon (stays the same while the stratagem stays the same).</param>
/// <param name="IconImage">The icon cut out of the screen.</param>
public sealed record MenuRow(int Index, RowState State, StratagemCode? Code, ulong IconHash, PixelImage IconImage)
{
    private byte[]? _icon;

    /// <summary>The icon as PNG, made on first use (most readings change nothing and never need it).</summary>
    public byte[] Icon => _icon ??= Png.Encode(IconImage);
}

/// <summary>
/// Where the list is on screen, in pixels of a 1920×1080 picture with 100 % HUD scale. Everything scales with the
/// HUD (the list sits at the left edge, also on wide screens). Measured on real screenshots (Oct 2026).
/// Kept as data so a game patch only needs new numbers.
/// </summary>
public sealed record MenuGeometry(
    double IconLeft = 50, double IconRight = 98,
    double FirstRowSearchTop = 60, double FirstRowSearchBottom = 220,
    double RowPitch = 52.3, double IconHeight = 44,
    double TextLeft = 105, double TextRight = 330,
    double ArrowSize = 14.5, double ArrowPitch = 21.5, double ArrowStartMax = 42,
    int MinArrows = 3, int MaxRows = 14,
    double MinArrowMatch = 0.7, double MinArrowMargin = 0.04)
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
/// The HUD size is taken from the picture itself (the rows repeat at a fixed pitch), so other aspect ratios and
/// resolutions work without a setting; <paramref name="scale"/> is only the first guess.
/// Costs one to two milliseconds for the list area.
/// </summary>
public static class MenuReader
{
    public static IReadOnlyList<MenuRow> Read(PixelImage image, double scale, MenuGeometry? geometry = null)
    {
        var g = geometry ?? MenuGeometry.Default;
        var rows = ReadAt(image, scale, g);
        // A HUD that is a few percent larger or smaller (other aspect ratio, odd resolution) shifts the lower rows
        // off the expected pitch: measure the pitch and read again if it differs.
        if (MeasureScale(image, scale, g) is { } measured && Math.Abs(measured / scale - 1) > 0.012)
        {
            var again = ReadAt(image, measured, g);
            if (Quality(again) > Quality(rows)) return again;
        }
        return rows;
    }

    /// <summary>More rows, and above all more rows with a clean arrow code, mean the geometry fits better.</summary>
    private static int Quality(IReadOnlyList<MenuRow> rows) => rows.Sum(r => r.Code is null ? 1 : 3);

    /// <summary>
    /// The HUD scale from the picture: the icon column is "busy" on the icons and flat in the gaps between them,
    /// so its line profile repeats with the row pitch. Null if no clear repetition is found.
    /// </summary>
    internal static double? MeasureScale(PixelImage image, double scale, MenuGeometry g)
    {
        int x0 = (int)Math.Round(g.IconLeft * scale), x1 = Math.Min((int)Math.Round(g.IconRight * scale), image.Width - 1);
        int y0 = (int)Math.Round(g.FirstRowSearchTop * scale), y1 = image.Height;
        var pitch = g.RowPitch * scale;
        int minLag = (int)Math.Floor(pitch * 0.75), maxLag = (int)Math.Ceiling(pitch * 1.35);
        if (x1 - x0 < 8 || y1 - y0 < maxLag * 3) return null;

        var profile = new double[y1 - y0];
        for (var y = y0; y < y1; y++) profile[y - y0] = Math.Min(StdDev(image, y, x0, x1), 40);
        var mean = profile.Average();
        for (var i = 0; i < profile.Length; i++) profile[i] -= mean;

        var corr = new double[maxLag + 2];
        for (var lag = minLag - 1; lag <= maxLag + 1; lag++)
        {
            double num = 0, a2 = 0, b2 = 0;
            for (var i = 0; i + lag < profile.Length; i++)
            {
                num += profile[i] * profile[i + lag];
                a2 += profile[i] * profile[i];
                b2 += profile[i + lag] * profile[i + lag];
            }
            corr[lag] = num / Math.Max(1e-9, Math.Sqrt(a2 * b2));
        }
        var best = minLag;
        for (var lag = minLag; lag <= maxLag; lag++) if (corr[lag] > corr[best]) best = lag;
        if (corr[best] < 0.35 || best == minLag || best == maxLag) return null;
        // Sub-pixel: the top of a parabola through the best lag and its neighbours.
        double l = corr[best - 1], c = corr[best], r = corr[best + 1];
        var denominator = l - 2 * c + r;
        var offset = Math.Abs(denominator) < 1e-9 ? 0 : Math.Clamp(0.5 * (l - r) / denominator, -0.5, 0.5);
        return (best + offset) / g.RowPitch;
    }

    private static IReadOnlyList<MenuRow> ReadAt(PixelImage image, double scale, MenuGeometry g)
    {
        int S(double v) => (int)Math.Round(v * scale);
        int iconL = S(g.IconLeft), iconR = Math.Min(S(g.IconRight), image.Width - 1);
        if (iconR <= iconL) return [];

        // Icons have structure, the gaps between them are flat panel. Static things (the panel's edge, a lamp behind
        // the half-transparent list) make every line equally "busy", so rows are found by the contrast between an
        // icon and the gap below it, not by a fixed threshold.
        var profile = new double[image.Height];
        for (var y = 0; y < image.Height; y++) profile[y] = StdDev(image, y, iconL, iconR);
        var pitch = g.RowPitch * scale;
        var iconHeight = Math.Max(4, S(g.IconHeight));
        var gap = Math.Max(2, (int)Math.Round(pitch) - iconHeight);

        // 1. First row: the best icon/gap contrast in the search range.
        int first = -1;
        var bestContrast = 0.0;
        for (var t = S(g.FirstRowSearchTop); t <= S(g.FirstRowSearchBottom) && t + iconHeight + gap < image.Height; t++)
        {
            var c = RowContrast(profile, t, iconHeight, gap);
            if (c > bestContrast) { bestContrast = c; first = t; }
        }
        if (first < 0 || bestContrast < MinRowContrast) return [];

        // 2. Further rows on the pitch (re-centred by a pixel or two against rounding), while they look like rows.
        var rows = new List<MenuRow>();
        var expected = (double)first;
        for (var k = 0; k < g.MaxRows; k++)
        {
            var guess = (int)Math.Round(expected);
            var top = guess;
            var contrast = double.MinValue;
            for (var t = guess - 2; t <= guess + 2; t++)
            {
                if (t < 0 || t + iconHeight + gap >= image.Height) continue;
                var c = RowContrast(profile, t, iconHeight, gap);
                if (c > contrast) { contrast = c; top = t; }
            }
            if (contrast < MinRowContrast) break;
            var bottom = top + iconHeight;
            rows.Add(ReadRow(image, k, top, bottom, iconL, iconR, Math.Max(0, S(g.TextLeft)), Math.Min(S(g.TextRight), image.Width), scale, g));
            expected = top + pitch;
        }
        return rows;
    }

    private static MenuRow ReadRow(PixelImage img, int index, int top, int bottom, int iconL, int iconR, int textL, int textR, double scale, MenuGeometry g)
    {
        var h = bottom - top;
        int lineTop = top + (int)(h * 0.52), lineBottom = Math.Min(bottom + (int)Math.Round(4 * scale), img.Height);
        var bg = Median(img, textL, textR, lineTop, lineBottom);

        // The arrows are near white; on a bright background (explosion behind the half-transparent list) the
        // threshold moves up with it, but always stays clearly above the background.
        // A dark picture (HDR, dim gamma) lowers the arrows too: never ask more than half of what the brightest
        // marks on the line reach, or arrows break into small pieces that look like text.
        var peak = Percentile(img, textL, textR, lineTop, lineBottom, 0.99);
        var threshold = bg + Math.Clamp(Math.Min((255 - bg) / 2, (peak - bg) / 2), 20, 45);
        var blobs = Blobs(img, textL, textR, lineTop, lineBottom, threshold);
        var chain = ArrowChain(blobs, scale, g);
        var titleTop = top;
        var titleBottom = top + (int)(h * 0.45);
        var titleBg = Median(img, textL, textR, titleTop, titleBottom);
        var titleContrast = Percentile(img, textL, textR, titleTop, titleBottom, 0.95) - titleBg;

        // A code is typed into the game, so it must be the whole code: every shape must really look like the game's
        // arrow and clearly point one way, the row must start where the title starts (a lost first arrow would
        // otherwise give a shorter, wrong code), and nothing bright may sit on the line right before or after it
        // (a lost arrow under a glow).
        StratagemCode? code = null;
        RowState state;
        var arrows = chain?.Select(b => ArrowDirection(img, b, textL, lineTop, bg)).ToList();
        var whole = chain is not null
                    && arrows!.All(a => a.Match >= g.MinArrowMatch && a.Margin >= g.MinArrowMargin)
                    && StartsWithTitle(img, chain[0].Left + textL, textL, textR, titleTop, titleBottom, titleBg, titleContrast, scale, g)
                    && !BrightNextTo(chain, blobs, scale, g);
        if (whole)
        {
            code = new StratagemCode(arrows!.Select(a => a.Direction));
            state = RowState.Callable;
        }
        else
        {
            // Only a line with text on it (a cooldown, "Bereit", "Ankunft in …") proves "not callable now".
            var arrowSized = g.ArrowSize * 0.7 * scale;
            var looksLikeText = blobs.Count(b => b.Width < arrowSized || b.Height < arrowSized) >= 3;
            state = !looksLikeText ? RowState.Unclear
                : titleContrast > 105 ? RowState.Active : titleContrast < 68 ? RowState.Cooldown : RowState.Idle;
        }

        var icon = img.Crop(iconL, top, iconR - iconL, h);
        return new MenuRow(index, state, code, IconHash(icon), icon);
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

    /// <summary>Per direction the template, also moved by one cell each way: small arrows (720p) are cut out a little
    /// off-centre, and a fair comparison must not punish that.</summary>
    private static readonly (Direction Direction, double[][] Variants)[] Templates = BuildTemplates();

    private static (Direction, double[][])[] BuildTemplates()
    {
        var up = UpTemplate;
        var right = Rotate(up);
        var down = Rotate(right);
        var left = Rotate(down);
        return [(Direction.Up, Shifts(up)), (Direction.Right, Shifts(right)), (Direction.Down, Shifts(down)), (Direction.Left, Shifts(left))];

        static double[][] Shifts(double[] t)
        {
            var result = new List<double[]>();
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var shifted = new double[N * N];
                for (var y = 0; y < N; y++)
                for (var x = 0; x < N; x++)
                {
                    int sx = x - dx, sy = y - dy;
                    shifted[y * N + x] = sx < 0 || sy < 0 || sx >= N || sy >= N ? 0 : t[sy * N + sx];
                }
                result.Add(shifted);
            }
            return result.ToArray();
        }

        // 90° clockwise
        static double[] Rotate(double[] m)
        {
            var r = new double[N * N];
            for (var y = 0; y < N; y++) for (var x = 0; x < N; x++) r[y * N + x] = m[(N - 1 - x) * N + y];
            return r;
        }
    }

    /// <summary>
    /// The blob's brightness, scaled to N×N, compared with the template in all four directions.
    /// Match: correlation with the best direction (1 = identical); Margin: how much better than the second best.
    /// </summary>
    internal static (Direction Direction, double Match, double Margin) ArrowDirection(PixelImage img, Blob blob, int offsetX, int offsetY, int bg)
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
        double bestScore = double.MinValue, second = double.MinValue;
        foreach (var (direction, variants) in Templates)
        {
            var score = variants.Max(t => Correlation(m, t));
            if (score > bestScore) { second = bestScore; bestScore = score; best = direction; }
            else if (score > second) second = score;
        }
        return (best, bestScore, bestScore - second);
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

    /// <summary>
    /// The game draws all arrows alike: one that is much larger than the others has merged with something bright
    /// (a glow) and cannot be trusted.
    /// </summary>
    private static bool SameSize(List<Blob> chain)
    {
        var widths = chain.Select(b => b.Width).Order().ToList();
        var heights = chain.Select(b => b.Height).Order().ToList();
        double w = widths[widths.Count / 2], h = heights[heights.Count / 2];
        return chain.All(b => Math.Abs(b.Width - w) <= Math.Max(2, w * 0.35) && Math.Abs(b.Height - h) <= Math.Max(2, h * 0.35));
    }

    /// <summary>Does the arrow row start where the title above it starts (both are left-aligned in the game)?</summary>
    private static bool StartsWithTitle(PixelImage img, int firstArrowX, int textL, int textR, int top, int bottom, int titleBg, int titleContrast, double scale, MenuGeometry g)
    {
        // Looked for from a little less than one arrow step before the first arrow: if that arrow were lost, the
        // title would already be running there and start "too early".
        var threshold = titleBg + Math.Max(25, titleContrast / 2);
        var from = Math.Max(textL, firstArrowX - (int)Math.Round(g.ArrowPitch * 0.9 * scale));
        var limit = Math.Min(textR, firstArrowX + (int)Math.Round(g.ArrowPitch * scale));
        for (var x = from; x < limit; x++)
        {
            var hits = 0;
            for (var y = top; y < bottom && hits < 2; y++) if (img.Luma(x, y) >= threshold) hits++;
            if (hits >= 2) return Math.Abs(x - firstArrowX) <= g.ArrowPitch * 0.45 * scale;
        }
        return false;
    }

    /// <summary>Something bright of some size on the arrow line within an arrow step before the first or after the last arrow.</summary>
    private static bool BrightNextTo(List<Blob> chain, List<Blob> blobs, double scale, MenuGeometry g)
    {
        var reach = g.ArrowPitch * 1.3 * scale;
        var minSize = g.ArrowSize * 0.35 * scale;
        int lineTop = chain.Min(b => b.Top), lineBottom = chain.Max(b => b.Bottom);
        Blob first = chain[0], last = chain[^1];
        foreach (var b in blobs)
        {
            if (chain.Contains(b) || b.Bottom < lineTop || b.Top > lineBottom) continue;
            if (b.Width < minSize || b.Height < minSize) continue; // a thin edge (icon frame), not a lost arrow
            // Overlap counts: a lost arrow merged with a glow can touch its neighbour.
            if (b.Left < first.Left && b.Right >= first.Left - reach) return true;
            if (b.Right > last.Right && b.Left <= last.Left + reach) return true;
        }
        return false;
    }

    /// <summary>
    /// The arrows of a row: arrow-sized shapes in a regular row – starting at the beginning of the line, one after
    /// the other at the arrow pitch, on one baseline. Anything else that is bright (text, a spark of the background
    /// behind the half-transparent list) is ignored instead of spoiling the row. Null if there is no such row.
    /// </summary>
    private static List<Blob>? ArrowChain(List<Blob> blobs, double scale, MenuGeometry g)
    {
        double min = g.ArrowSize * 0.7 * scale, max = g.ArrowSize * 1.6 * scale;
        var candidates = blobs
            .Where(b => b.Width >= min && b.Height >= min && b.Width <= max && b.Height <= max
                        && b.Width <= b.Height * 1.6 && b.Height <= b.Width * 1.6
                        && b.Points.Count >= b.Width * b.Height * 0.3)
            .OrderBy(b => b.Left)
            .ToList();
        double pitchMin = g.ArrowPitch * 0.75 * scale, pitchMax = g.ArrowPitch * 1.3 * scale, baseline = Math.Max(2, 3 * scale);
        foreach (var start in candidates)
        {
            if (start.Left > g.ArrowStartMax * scale) break;
            var chain = new List<Blob> { start };
            while (true)
            {
                var last = chain[^1];
                var next = candidates.FirstOrDefault(b =>
                    b.Left - last.Left >= pitchMin && b.Left - last.Left <= pitchMax && Math.Abs(b.Top - last.Top) <= baseline);
                if (next is null) break;
                chain.Add(next);
            }
            if (chain.Count >= g.MinArrows && SameSize(chain)) return chain;
        }
        return null;
    }

    /// <summary>How much more structure the icon lines have than the gap lines below them (std. deviation units).</summary>
    private const double MinRowContrast = 9;

    private static double RowContrast(double[] profile, int top, int iconHeight, int gap)
    {
        var margin = Math.Max(1, iconHeight / 10);
        double icon = 0, gapSum = 0;
        int n = 0, m = 0;
        for (var y = top + margin; y < top + iconHeight - margin; y++) { icon += Math.Min(profile[y], 60); n++; }
        for (var y = top + iconHeight + 1; y < top + iconHeight + gap; y++) { gapSum += profile[y]; m++; }
        if (n == 0 || m == 0) return 0;
        return icon / n - gapSum / m;
    }

    private static double StdDev(PixelImage img, int y, int x0, int x1)
    {
        long sum = 0, sq = 0;
        var n = x1 - x0;
        if (n <= 0) return 0;
        for (var x = x0; x < x1; x++)
        {
            var l = img.Luma(x, y);
            sum += l;
            sq += l * l;
        }
        return Math.Sqrt(Math.Max(0, (sq - sum * sum / (double)n) / n));
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

    internal sealed class Blob
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
