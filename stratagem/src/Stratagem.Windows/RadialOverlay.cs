using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;
using Stratagem.Core.Model;
using Stratagem.Core.Radial;
using static Stratagem.Windows.Native;

namespace Stratagem.Windows;

/// <summary>
/// The radial menu on screen: a click-through, never-focused, always-on-top layered window with per-pixel alpha.
/// It never takes the focus, so the game keeps receiving input. Only visible over games in borderless/windowed
/// mode (exclusive fullscreen hides every other window).
/// Calls arrive from the hook thread and are passed to the UI thread with BeginInvoke (never blocks the hook).
/// </summary>
public sealed class RadialOverlay : Form, IRadialView
{
    private readonly Func<Rectangle?> _gameBounds;
    private IReadOnlyList<StratagemItem> _items = [];
    private RadialMode _mode;
    private int? _highlight;
    private bool _visible;
    private int _sizePx = 420;
    private double _opacity = 0.85;
    private Point _origin;
    private float _scale = 1;

    private static readonly Color Accent = Color.FromArgb(255, 231, 16);

    public RadialOverlay(Func<Rectangle?> gameBounds)
    {
        _gameBounds = gameBounds;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Stratagem Radial";
        _ = Handle; // create the window now, on the UI thread
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    public void Configure(int sizePx, double opacity)
    {
        Post(() =>
        {
            _sizePx = sizePx;
            _opacity = opacity;
            if (_visible) Render();
        });
    }

    public void Show(IReadOnlyList<StratagemItem> items, RadialMode mode) => Post(() =>
    {
        _items = items;
        _mode = mode;
        _highlight = null;
        Place();
        Render();
        _visible = true;
        ShowWindow(Handle, SW_SHOWNOACTIVATE);
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    });

    public void Highlight(int? index) => Post(() =>
    {
        _highlight = index;
        if (_visible) Render();
    });

    public new void Hide() => Post(() =>
    {
        _visible = false;
        ShowWindow(Handle, SW_HIDE);
    });

    private void Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) { /* closing */ }
    }

    private void Place()
    {
        var bounds = _gameBounds() ?? Screen.FromPoint(Cursor.Position).Bounds;
        var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        _scale = DpiScale(center);
        var size = (int)(_sizePx * _scale);
        _origin = new Point(center.X - size / 2, center.Y - size / 2);
    }

    private static float DpiScale(Point p)
    {
        try
        {
            var monitor = MonitorFromPoint(new POINT { X = p.X, Y = p.Y }, MONITOR_DEFAULTTONEAREST);
            return GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96f : 1f;
        }
        catch (Exception) { return 1f; }
    }

    private void Render()
    {
        var size = (int)(_sizePx * _scale);
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            Draw(g, size);
        }
        Present(bmp);
    }

    private void Draw(Graphics g, int size)
    {
        var alpha = (int)(255 * _opacity);
        var c = size / 2f;
        var outer = c - 2 * _scale;
        var inner = outer * 0.28f;
        var n = _items.Count;

        using (var bg = new SolidBrush(Color.FromArgb(alpha, 12, 13, 16)))
            g.FillEllipse(bg, c - outer, c - outer, outer * 2, outer * 2);
        using var rim = new Pen(Color.FromArgb(alpha, 70, 75, 90), 1.5f * _scale);
        g.DrawEllipse(rim, c - outer, c - outer, outer * 2, outer * 2);

        using var nameFont = new Font("Segoe UI Semibold", 13f * _scale, GraphicsUnit.Pixel);
        using var codeFont = new Font("Segoe UI Symbol", 14f * _scale, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 11f * _scale, GraphicsUnit.Pixel);
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        for (var i = 0; i < n; i++)
        {
            var item = _items[i];
            var mid = (float)RadialLayout.CenterAngle(i, n);
            var sweep = 360f / n;
            var selected = _highlight == i;

            if (selected)
            {
                using var path = new GraphicsPath();
                path.AddArc(c - outer, c - outer, outer * 2, outer * 2, mid - sweep / 2, sweep);
                path.AddArc(c - inner, c - inner, inner * 2, inner * 2, mid + sweep / 2, -sweep);
                path.CloseFigure();
                using var fill = new SolidBrush(Color.FromArgb(Math.Min(255, alpha + 30), item.Available ? Accent : Color.FromArgb(110, 110, 110)));
                g.FillPath(fill, path);
            }
            if (n > 1)
            {
                var a = (mid - sweep / 2) * Math.PI / 180;
                using var sep = new Pen(Color.FromArgb(alpha, 58, 63, 77), 1f * _scale);
                g.DrawLine(sep, c + (float)Math.Cos(a) * inner, c + (float)Math.Sin(a) * inner, c + (float)Math.Cos(a) * outer, c + (float)Math.Sin(a) * outer);
            }

            var rad = mid * Math.PI / 180;
            var r = (outer + inner) / 2;
            var x = c + (float)Math.Cos(rad) * r;
            var y = c + (float)Math.Sin(rad) * r;
            var width = Math.Min(outer - inner, (float)(2 * Math.PI * r / n)) * 0.9f;
            var textColor = selected ? Color.FromArgb(20, 20, 10) : item.Available ? Color.FromArgb(241, 242, 245) : Color.FromArgb(110, 113, 125);
            var codeColor = selected ? Color.FromArgb(20, 20, 10) : item.Available ? Accent : Color.FromArgb(110, 113, 125);

            var iconSize = 30 * _scale;
            var top = y - (item.Icon is null ? 0 : iconSize / 2);
            if (item.Icon is not null)
            {
                try
                {
                    using var ms = new MemoryStream(item.Icon);
                    using var icon = Image.FromStream(ms);
                    g.DrawImage(icon, x - iconSize / 2, top - iconSize - 6 * _scale, iconSize, iconSize);
                }
                catch (Exception) { /* broken icon: text only */ }
            }
            using (var brush = new SolidBrush(textColor))
                g.DrawString(item.Name ?? "", nameFont, brush, new RectangleF(x - width / 2, top - 18 * _scale, width, 18 * _scale), center);
            using (var brush = new SolidBrush(codeColor))
                g.DrawString(item.Code.Arrows.Replace(" ", ""), codeFont, brush, new RectangleF(x - width / 2, top + 1 * _scale, width, 18 * _scale), center);
        }

        using (var hub = new SolidBrush(Color.FromArgb(Math.Min(255, alpha + 20), 26, 29, 36)))
            g.FillEllipse(hub, c - inner, c - inner, inner * 2, inner * 2);
        g.DrawEllipse(rim, c - inner, c - inner, inner * 2, inner * 2);
        using var muted = new SolidBrush(Color.FromArgb(154, 161, 176));
        var hint = n == 0 ? "Liste leer –\nin der App füllen" : _mode == RadialMode.HoldRelease ? "loslassen =\nabbrechen" : "Esc =\nschließen";
        g.DrawString(hint, small, muted, new RectangleF(c - inner, c - inner, inner * 2, inner * 2), center);
    }

    private void Present(Bitmap bmp)
    {
        var screen = GetDC(0);
        var mem = CreateCompatibleDC(screen);
        var hbitmap = bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(mem, hbitmap);
        try
        {
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT();
            var dst = new POINT { X = _origin.X, Y = _origin.Y };
            var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
            UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(hbitmap);
            DeleteDC(mem);
            ReleaseDC(0, screen);
        }
    }
}
