namespace Stratagem.Core.Radial;

/// <summary>
/// Geometry of the radial menu. Screen coordinates (y points down). Segment 0 is at the top,
/// the others follow clockwise; every segment is centred on its direction.
/// </summary>
public static class RadialLayout
{
    /// <summary>Centre angle of segment <paramref name="index"/> in degrees, clockwise from "right" (GDI+ convention).</summary>
    public static double CenterAngle(int index, int count) => -90.0 + index * 360.0 / count;

    /// <summary>
    /// Segment for a movement vector, or null inside the dead zone (= cancel).
    /// </summary>
    public static int? Hit(double dx, double dy, int count, double deadzone) => Hit(dx, dy, count, deadzone, null, 0);

    /// <summary>
    /// Like <see cref="Hit(double, double, int, double)"/>, but sticky: the <paramref name="current"/> segment stays
    /// until the direction is <paramref name="hysteresisDegrees"/> past its border, and it only falls back to the
    /// centre below 75 % of the dead zone. Hand jitter at a border or at the dead zone edge never makes it flicker.
    /// </summary>
    public static int? Hit(double dx, double dy, int count, double deadzone, int? current, double hysteresisDegrees)
    {
        if (count <= 0) return null;
        var distanceSq = dx * dx + dy * dy;
        var inner = current is null ? deadzone : deadzone * 0.75;
        if (distanceSq < inner * inner) return null;
        if (count == 1) return 0;
        var sweep = 360.0 / count;
        var angle = Math.Atan2(dy, dx) * 180.0 / Math.PI; // -180..180, 0 = right, 90 = down
        var fromTop = Normalize(angle + 90.0 + sweep / 2); // shift so segment 0 starts at 0
        var hit = (int)(fromTop / sweep) % count;
        if (current is { } c && c != hit && c < count)
        {
            // Distance of the direction from the current segment's centre, in degrees (0..180).
            var offset = Math.Abs(Normalize(fromTop - (c * sweep + sweep / 2) + 180) - 180);
            if (offset <= sweep / 2 + Math.Min(hysteresisDegrees, sweep * 0.25)) return c;
        }
        return hit;

        static double Normalize(double degrees) => ((degrees % 360.0) + 360.0) % 360.0;
    }

    /// <summary>
    /// Limits the accumulated movement to <paramref name="radius"/>, keeping the direction.
    /// Works like a virtual stick: changing direction never needs a long way back.
    /// </summary>
    public static (double X, double Y) Clamp(double x, double y, double radius)
    {
        var length = Math.Sqrt(x * x + y * y);
        if (length <= radius || length == 0) return (x, y);
        var f = radius / length;
        return (x * f, y * f);
    }
}
