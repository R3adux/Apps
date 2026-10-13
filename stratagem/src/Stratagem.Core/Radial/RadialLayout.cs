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
    public static int? Hit(double dx, double dy, int count, double deadzone)
    {
        if (count <= 0) return null;
        if (dx * dx + dy * dy < deadzone * deadzone) return null;
        if (count == 1) return 0;
        var angle = Math.Atan2(dy, dx) * 180.0 / Math.PI; // -180..180, 0 = right, 90 = down
        var fromTop = angle + 90.0 + 180.0 / count;      // shift so segment 0 starts at 0
        var normalized = ((fromTop % 360.0) + 360.0) % 360.0;
        return (int)(normalized / (360.0 / count)) % count;
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
