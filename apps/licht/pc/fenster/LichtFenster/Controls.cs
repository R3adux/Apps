using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LichtFenster
{
    /// <summary>A button that shows a state (the on/off switch, the selected half of "Weiß | Farbe").</summary>
    public class StateButton : Button
    {
        public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
            nameof(IsActive), typeof(bool), typeof(StateButton), new FrameworkPropertyMetadata(false));

        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }
    }

    /// <summary>Settings for the templates in Styles.xaml.</summary>
    public static class Look
    {
        public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
            "Radius", typeof(CornerRadius), typeof(Look), new FrameworkPropertyMetadata(new CornerRadius(10)));

        public static CornerRadius GetRadius(DependencyObject o) => (CornerRadius)o.GetValue(RadiusProperty);
        public static void SetRadius(DependencyObject o, CornerRadius value) => o.SetValue(RadiusProperty, value);

        /// <summary>The icon of an icon button (a geometry drawn in the text colour).</summary>
        public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
            "Icon", typeof(Geometry), typeof(Look), new FrameworkPropertyMetadata(null));

        public static Geometry? GetIcon(DependencyObject o) => (Geometry?)o.GetValue(IconProperty);
        public static void SetIcon(DependencyObject o, Geometry? value) => o.SetValue(IconProperty, value);
    }

    /// <summary>
    /// The lamp cards: as many columns of at least <see cref="MinColumnWidth"/> as fit; a row is as high as its
    /// highest card, each card as high as its content.
    /// </summary>
    public sealed class CardPanel : Panel
    {
        public double MinColumnWidth { get; set; } = 320;
        public double Gap { get; set; } = 14;

        private int Columns(double width) => Math.Max(1, (int)Math.Floor((width + Gap) / (MinColumnWidth + Gap)));

        protected override Size MeasureOverride(Size available)
        {
            var width = double.IsInfinity(available.Width) ? MinColumnWidth : available.Width;
            var columns = Columns(width);
            var columnWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);
            double height = 0;
            for (var start = 0; start < InternalChildren.Count; start += columns)
            {
                double row = 0;
                for (var i = start; i < Math.Min(start + columns, InternalChildren.Count); i++)
                {
                    InternalChildren[i].Measure(new Size(columnWidth, double.PositiveInfinity));
                    row = Math.Max(row, InternalChildren[i].DesiredSize.Height);
                }
                height += (start > 0 ? Gap : 0) + row;
            }
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size final)
        {
            var columns = Columns(final.Width);
            var columnWidth = Math.Max(0, (final.Width - Gap * (columns - 1)) / columns);
            double y = 0;
            for (var start = 0; start < InternalChildren.Count; start += columns)
            {
                var end = Math.Min(start + columns, InternalChildren.Count);
                double row = 0;
                for (var i = start; i < end; i++) row = Math.Max(row, InternalChildren[i].DesiredSize.Height);
                // Each card keeps its own height (a lamp that is off is short), the next row starts below the highest.
                for (var i = start; i < end; i++)
                    InternalChildren[i].Arrange(new Rect((i - start) * (columnWidth + Gap), y, columnWidth, InternalChildren[i].DesiredSize.Height));
                y += row + Gap;
            }
            return final;
        }
    }

    /// <summary>
    /// A QR code, drawn sharp: every module is a whole number of screen pixels, with the white quiet zone around
    /// it that scanners need.
    /// </summary>
    public sealed class QrView : FrameworkElement
    {
        private const int QuietZone = 4;

        public static readonly DependencyProperty MatrixProperty = DependencyProperty.Register(
            nameof(Matrix), typeof(bool[,]), typeof(QrView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

        public bool[,]? Matrix
        {
            get => (bool[,]?)GetValue(MatrixProperty);
            set => SetValue(MatrixProperty, value);
        }

        /// <summary>Largest size in device-independent pixels.</summary>
        public double MaxSize { get; set; } = 300;

        public QrView()
        {
            SnapsToDevicePixels = true;
            RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        }

        protected override Size MeasureOverride(Size available)
        {
            var size = Math.Min(MaxSize, double.IsInfinity(available.Width) ? MaxSize : available.Width);
            return Matrix == null ? new Size(0, 0) : new Size(size, size);
        }

        protected override void OnRender(DrawingContext dc)
        {
            var matrix = Matrix;
            if (matrix == null) return;
            var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var modules = matrix.GetLength(0) + 2 * QuietZone;
            var available = Math.Floor(Math.Min(ActualWidth, ActualHeight) * scale);
            var pixels = Math.Max(1, Math.Floor(available / modules)); // whole device pixels per module
            var unit = pixels / scale;
            var size = unit * modules;
            var left = Math.Round((ActualWidth - size) / 2 * scale) / scale;
            var top = Math.Round((ActualHeight - size) / 2 * scale) / scale;

            dc.DrawRectangle(Brushes.White, null, new Rect(left, top, size, size));
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                var n = matrix.GetLength(0);
                for (var y = 0; y < n; y++)
                {
                    var x = 0;
                    while (x < n)
                    {
                        if (!matrix[y, x])
                        {
                            x++;
                            continue;
                        }
                        var run = x;
                        while (run < n && matrix[y, run]) run++;
                        var rx = left + (x + QuietZone) * unit;
                        var ry = top + (y + QuietZone) * unit;
                        var rw = (run - x) * unit;
                        g.BeginFigure(new Point(rx, ry), true, true);
                        g.PolyLineTo(new List<Point> { new Point(rx + rw, ry), new Point(rx + rw, ry + unit), new Point(rx, ry + unit) }, false, false);
                        x = run;
                    }
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(Brushes.Black, null, geometry);
        }
    }

    /// <summary>A small turning arc: something is on its way.</summary>
    public sealed class Spinner : FrameworkElement
    {
        private readonly RotateTransform _turn = new RotateTransform();

        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
            nameof(Stroke), typeof(Brush), typeof(Spinner), new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush Stroke
        {
            get => (Brush)GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        public double Size { get; set; } = 18;

        public Spinner()
        {
            SetResourceReference(StrokeProperty, "Accent");
            RenderTransform = _turn;
            RenderTransformOrigin = new Point(0.5, 0.5);
            VerticalAlignment = VerticalAlignment.Center;
            Loaded += (_, _) =>
            {
                if (MainView.Animations)
                    _turn.BeginAnimation(RotateTransform.AngleProperty,
                        new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
            };
            Unloaded += (_, _) => _turn.BeginAnimation(RotateTransform.AngleProperty, null);
        }

        protected override Size MeasureOverride(Size available) => new Size(Size, Size);

        protected override void OnRender(DrawingContext dc)
        {
            var thickness = Size >= 16 ? 2.5 : 2;
            var r = (Size - thickness) / 2;
            var c = Size / 2;
            var arc = new StreamGeometry();
            using (var g = arc.Open())
            {
                g.BeginFigure(new Point(c, c - r), false, false);
                g.ArcTo(new Point(c - r, c), new Size(r, r), 0, true, SweepDirection.Clockwise, true, false);
            }
            arc.Freeze();
            dc.DrawGeometry(null, new Pen(Stroke, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
        }
    }
}
