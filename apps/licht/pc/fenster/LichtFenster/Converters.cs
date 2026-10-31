using System;
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Licht.Fenster;

namespace LichtFenster
{
    /// <summary>true → visible; with ConverterParameter "not": false → visible.</summary>
    public sealed class VisibleIf : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var on = value is bool b && b;
            if (parameter as string == "not") on = !on;
            return on ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>Visible when there is something to show: a non-empty text, a non-empty list, any other object.</summary>
    public sealed class VisibleIfAny : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
        {
            null => Visibility.Collapsed,
            string s => s.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
            ICollection c => c.Count > 0 ? Visibility.Visible : Visibility.Collapsed,
            _ => Visibility.Visible,
        };

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public sealed class Not : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
    }

    /// <summary>A lamp colour (<see cref="Rgb"/>) as brush or colour.</summary>
    public sealed class RgbBrush : IValueConverter
    {
        public static Color ToColor(Rgb rgb, byte alpha = 255) => Color.FromArgb(alpha, rgb.R, rgb.G, rgb.B);

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var colour = value is Rgb rgb ? ToColor(rgb) : Colors.Transparent;
            if (targetType == typeof(Color)) return colour;
            var brush = new SolidColorBrush(colour);
            brush.Freeze();
            return brush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>
    /// The soft light in the corner of a lamp card: [LampColor, Glow] → brush (like the card background in the
    /// phone app).
    /// </summary>
    public sealed class GlowBrush : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2 || !(values[0] is Rgb rgb) || !(values[1] is double glow) || glow <= 0) return Brushes.Transparent;
            var alpha = (byte)Math.Round(Math.Min(1, glow) * 0.28 * 255);
            var brush = new RadialGradientBrush
            {
                Center = new Point(0, 0),
                GradientOrigin = new Point(0, 0),
                RadiusX = 1.2,
                RadiusY = 0.9,
                GradientStops =
                {
                    new GradientStop(RgbBrush.ToColor(rgb, alpha), 0),
                    new GradientStop(RgbBrush.ToColor(rgb, 0), 0.6),
                },
            };
            brush.Freeze();
            return brush;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>The track of the saturation slider: white to the full colour of the hue.</summary>
    public sealed class SaturationTrack : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var end = value is Rgb rgb ? RgbBrush.ToColor(rgb) : Colors.Red;
            var brush = new LinearGradientBrush(Colors.White, end, 0);
            brush.Freeze();
            return brush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>How far the bulb glows: Glow (0 … about 1) → blur radius.</summary>
    public sealed class GlowRadius : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is double glow ? 8 + glow * 22 : 0.0;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>Lit → shadow opacity of the bulb glow.</summary>
    public sealed class GlowOpacity : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b && b ? 0.65 : 0.0;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>"Helligkeit 70 %" style numbers.</summary>
    public sealed class Percent : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is int i ? $"{i} %" : "";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
