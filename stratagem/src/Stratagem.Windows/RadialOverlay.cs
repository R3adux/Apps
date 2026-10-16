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
///
/// Smooth and cheap:
/// - Every look (no highlight, segment 1 highlighted, …) is drawn once and kept as a bitmap; moving the mouse
///   only swaps bitmaps (UpdateLayeredWindow, well under a millisecond). The plain look is drawn in advance
///   whenever the list changes, so the menu appears instantly.
/// - Hold mode: a short tap of the trigger without moving does not flash the menu (it appears after 90 ms,
///   or at once when a segment gets selected).
/// - A selection is confirmed by fading out the highlighted look (window alpha only, nothing is redrawn).
/// - Timers run only while the menu is visible; when hidden, this window costs nothing.
/// Calls arrive from the hook thread and are passed to the UI thread with BeginInvoke (never blocks the hook).
/// </summary>
public sealed class RadialOverlay : Form, IRadialView
{
    private const int ShowDelayMs = 90;
    private const int FadeMs = 140;

    private static readonly Color Accent = Color.FromArgb(255, 231, 16);

    private readonly Func<Rectangle?> _gameBounds;
    private readonly System.Windows.Forms.Timer _showDelay = new() { Interval = ShowDelayMs };
    private readonly System.Windows.Forms.Timer _fade = new() { Interval = 15 };
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 250 };
    private readonly Dictionary<int, Frame> _frames = [];

    private IReadOnlyList<StratagemItem> _items = [];
    private RadialMode _mode;
    private int _sizePx = 420;
    private double _opacity = 0.85;
    private float _scale = 1;
    private Point _origin;
    private int? _highlight;
    private bool _open, _visible;
    private long _fadeStart;
    private Fonts? _fonts;
    private (int Size, double Opacity, IReadOnlyList<StratagemItem> Items)? _pending;

    private sealed record Frame(nint Bitmap, int Size);

    private sealed class Fonts(float scale) : IDisposable
    {
        public readonly float Scale = scale;
        public readonly Font Name = new("Segoe UI Semibold", 13f * scale, GraphicsUnit.Pixel);
        public readonly Font Code = new("Segoe UI Symbol", 14f * scale, GraphicsUnit.Pixel);
        public readonly Font Small = new("Segoe UI", 11f * scale, GraphicsUnit.Pixel);
        public readonly StringFormat Center = new(StringFormatFlags.NoWrap)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
        };

        public void Dispose()
        {
            Name.Dispose();
            Code.Dispose();
            Small.Dispose();
            Center.Dispose();
        }
    }

    /// <param name="gameBounds">Screen rectangle of the game window (the menu is centred on it), null = cursor's screen.</param>
    /// <param name="tick">Called every 250 ms while the menu is visible (closes a forgotten tap-menu).</param>
    public RadialOverlay(Func<Rectangle?> gameBounds, Action? tick = null)
    {
        _gameBounds = gameBounds;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Stratagem Radial";
        _showDelay.Tick += (_, _) => { _showDelay.Stop(); if (_open) Display(); };
        _fade.Tick += (_, _) => FadeStep();
        _tick.Tick += (_, _) => tick?.Invoke();
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

    /// <summary>New size/opacity/list: drops the old bitmaps and draws the plain look in advance. UI thread.</summary>
    public void Configure(int sizePx, double opacity, IReadOnlyList<StratagemItem> items)
    {
        if (_open || _visible)
        {
            _pending = (sizePx, opacity, items); // the menu on screen stays as it is; applied when it closes
            return;
        }
        _pending = null;
        if (sizePx == _sizePx && opacity == _opacity && ReferenceEquals(items, _items)) return;
        _sizePx = sizePx;
        _opacity = opacity;
        _items = items;
        ClearFrames();
        Place();
        GetFrame(null);
    }

    public void Show(IReadOnlyList<StratagemItem> items, RadialMode mode) => Post(() =>
    {
        _fade.Stop();
        if (!ReferenceEquals(items, _items) || mode != _mode)
        {
            _items = items;
            _mode = mode;
            ClearFrames();
        }
        Place();
        _open = true;
        _highlight = null;
        _tick.Start();
        if (mode == RadialMode.HoldRelease && !_visible)
        {
            _showDelay.Stop(); // restart: Start() on a running WinForms timer keeps the old due time
            _showDelay.Start();
        }
        else Display();
    });

    public void Highlight(int? index) => Post(() =>
    {
        _highlight = index;
        if (!_open) return;
        if (!_visible) Display(); // selecting: show right away
        else Present(GetFrame(index), 255);
    });

    public void Hide(bool selected) => Post(() =>
    {
        _open = false;
        _showDelay.Stop();
        _tick.Stop();
        if (!_visible) return;
        if (selected)
        {
            // Confirm: the highlighted look fades out.
            _fadeStart = Environment.TickCount64;
            _fade.Start();
        }
        else HideWindow();
    });

    private void Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) { /* closing */ }
    }

    private void Display()
    {
        _showDelay.Stop();
        Present(GetFrame(_highlight), 255);
        if (_visible) return;
        _visible = true;
        ShowWindow(Handle, SW_SHOWNOACTIVATE);
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void FadeStep()
    {
        var t = (Environment.TickCount64 - _fadeStart) / (double)FadeMs;
        if (t >= 1 || _open)
        {
            _fade.Stop();
            if (!_open) HideWindow();
            return;
        }
        SetAlpha((byte)(255 * (1 - t)));
    }

    private void HideWindow()
    {
        _fade.Stop();
        _visible = false;
        ShowWindow(Handle, SW_HIDE);
        if (_pending is { } p && !_open) Configure(p.Size, p.Opacity, p.Items);
    }

    private void Place()
    {
        var bounds = _gameBounds() ?? Screen.FromPoint(Cursor.Position).Bounds;
        var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        var scale = DpiScale(center);
        if (Math.Abs(scale - _scale) > 0.001f)
        {
            _scale = scale;
            ClearFrames();
        }
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

    // --- bitmaps ----------------------------------------------------------------------------------

    private Frame GetFrame(int? highlight)
    {
        var key = highlight ?? -1;
        if (_frames.TryGetValue(key, out var frame)) return frame;
        var size = Math.Max(1, (int)(_sizePx * _scale));
        if (_fonts is null || Math.Abs(_fonts.Scale - _scale) > 0.001f)
        {
            _fonts?.Dispose();
            _fonts = new Fonts(_scale);
        }
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            Draw(g, size, highlight, _fonts);
        }
        frame = new Frame(bmp.GetHbitmap(Color.FromArgb(0)), size);
        _frames[key] = frame;
        return frame;
    }

    private void ClearFrames()
    {
        foreach (var f in _frames.Values) DeleteObject(f.Bitmap);
        _frames.Clear();
    }

    private void Draw(Graphics g, int size, int? highlight, Fonts fonts)
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
        using var sep = new Pen(Color.FromArgb(alpha, 58, 63, 77), 1f * _scale);

        for (var i = 0; i < n; i++)
        {
            var item = _items[i];
            var mid = (float)RadialLayout.CenterAngle(i, n);
            var sweep = 360f / n;
            var selected = highlight == i;

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
                g.DrawLine(sep, c + (float)Math.Cos(a) * inner, c + (float)Math.Sin(a) * inner, c + (float)Math.Cos(a) * outer, c + (float)Math.Sin(a) * outer);
            }

            if (item.IsEmpty) continue; // free place: keeps the other directions, shows nothing

            var rad = mid * Math.PI / 180;
            var r = (outer + inner) / 2;
            var x = c + (float)Math.Cos(rad) * r;
            var y = c + (float)Math.Sin(rad) * r;
            var width = Math.Min(outer - inner, (float)(2 * Math.PI * r / n)) * 0.92f;
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
                g.DrawString(item.Name ?? "", fonts.Name, brush, new RectangleF(x - width / 2, top - 18 * _scale, width, 18 * _scale), fonts.Center);
            using (var brush = new SolidBrush(codeColor))
                g.DrawString(item.Code.Arrows.Replace(" ", ""), fonts.Code, brush, new RectangleF(x - width / 2, top + 1 * _scale, width, 18 * _scale), fonts.Center);
        }

        using (var hub = new SolidBrush(Color.FromArgb(Math.Min(255, alpha + 20), 26, 29, 36)))
            g.FillEllipse(hub, c - inner, c - inner, inner * 2, inner * 2);
        g.DrawEllipse(rim, c - inner, c - inner, inner * 2, inner * 2);
        using var muted = new SolidBrush(Color.FromArgb(154, 161, 176));
        var hint = _items.All(i => i.IsEmpty) ? "Keine\nStratagems" : _mode == RadialMode.HoldRelease ? "loslassen =\nabbrechen" : "Esc =\nschließen";
        using var twoLines = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(hint, fonts.Small, muted, new RectangleF(c - inner, c - inner, inner * 2, inner * 2), twoLines);
    }

    // --- screen -----------------------------------------------------------------------------------

    private void Present(Frame frame, byte alpha)
    {
        var screen = GetDC(0);
        var mem = CreateCompatibleDC(screen);
        var old = SelectObject(mem, frame.Bitmap);
        try
        {
            var size = new SIZE { cx = frame.Size, cy = frame.Size };
            var src = new POINT();
            var dst = new POINT { X = _origin.X, Y = _origin.Y };
            var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = alpha, AlphaFormat = AC_SRC_ALPHA };
            UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteDC(mem);
            ReleaseDC(0, screen);
        }
    }

    /// <summary>Changes only the window's overall alpha (no redraw, no bitmap transfer).</summary>
    private void SetAlpha(byte alpha)
    {
        var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = alpha, AlphaFormat = AC_SRC_ALPHA };
        UpdateLayeredWindowAlpha(Handle, 0, 0, 0, 0, 0, 0, ref blend, ULW_ALPHA);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _showDelay.Dispose();
            _fade.Dispose();
            _tick.Dispose();
            _fonts?.Dispose();
        }
        ClearFrames();
        base.Dispose(disposing);
    }
}
