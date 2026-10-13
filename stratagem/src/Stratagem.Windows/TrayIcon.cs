using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Stratagem.Windows;

/// <summary>Icon in the notification area: click = open the web UI, right-click = pause, autostart, quit.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _pause;
    private readonly ToolStripMenuItem _autostart;
    private readonly Icon _active = Draw(paused: false);
    private readonly Icon _paused = Draw(paused: true);

    public TrayIcon(Action open, Action<bool> setPaused, Action quit)
    {
        _pause = new ToolStripMenuItem("Pausieren", null, (_, _) => setPaused(!_pause!.Checked));
        _autostart = new ToolStripMenuItem("Mit Windows starten", null, (_, _) =>
        {
            Autostart.Enabled = !Autostart.Enabled;
            _autostart!.Checked = Autostart.Enabled;
        });
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Stratagem öffnen", null, (_, _) => open()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(_pause);
        menu.Items.Add(_autostart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Beenden", null, (_, _) => quit()));
        menu.Opening += (_, _) => _autostart.Checked = Autostart.Enabled;

        _icon = new NotifyIcon { Icon = _active, Text = "Stratagem", ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) open(); };
    }

    /// <summary>Called on the UI thread with the current status.</summary>
    public void Update(bool paused, bool gameFocused, int count)
    {
        _pause.Checked = paused;
        _icon.Icon = paused ? _paused : _active;
        var status = paused ? "pausiert" : gameFocused ? "aktiv" : "wartet aufs Spiel";
        _icon.Text = $"Stratagem – {status} ({count} Stratagems)";
    }

    public void Notify(string text) => _icon.ShowBalloonTip(4000, "Stratagem", text, ToolTipIcon.Info);

    private static Icon Draw(bool paused)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var yellow = paused ? Color.FromArgb(130, 130, 130) : Color.FromArgb(255, 231, 16);
            using var bg = new SolidBrush(Color.FromArgb(16, 18, 23));
            using var pen = new Pen(yellow, 2.5f);
            using var fill = new SolidBrush(yellow);
            g.FillEllipse(bg, 1, 1, 30, 30);
            g.DrawEllipse(pen, 2, 2, 28, 28);
            g.FillPolygon(fill, [new PointF(16, 5), new PointF(20.5f, 11), new PointF(11.5f, 11)]);
            g.FillPolygon(fill, [new PointF(27, 16), new PointF(21, 20.5f), new PointF(21, 11.5f)]);
            g.FillPolygon(fill, [new PointF(16, 27), new PointF(11.5f, 21), new PointF(20.5f, 21)]);
            g.FillPolygon(fill, [new PointF(5, 16), new PointF(11, 11.5f), new PointF(11, 20.5f)]);
            g.FillEllipse(fill, 13, 13, 6, 6);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
