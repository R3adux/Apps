using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Licht.Desktop
{
    /// <summary>Tray icon, Licht window and the messages with Licht.exe. Everything runs on the UI thread.</summary>
    internal sealed class Host : ApplicationContext
    {
        private readonly Options _options;
        private readonly Control _ui = new Control();
        private readonly Channel _channel;
        private readonly Icon _smallIcon;
        private readonly Icon _bigIcon;
        private readonly NotifyIcon _tray;
        private readonly ToolStripMenuItem _autostart;
        private readonly AppWindow _window;
        private DateTime _lastClick = DateTime.MinValue;
        private bool _ending;

        public int ExitCode { get; private set; }

        public Host(Options options)
        {
            _options = options;
            _ui.CreateControl(); // a window handle, so other threads can hand work to the UI thread
            _channel = new Channel(line => Post(() => OnMessage(line)), () => Post(() => End(0)));

            _smallIcon = LoadIcon(SystemInformation.SmallIconSize);
            _bigIcon = LoadIcon(new Size(256, 256));
            _window = new AppWindow(options.Url, options.DataDir, _bigIcon, _channel);
            _window.Unavailable += () => _channel.Send("open-browser");
            // Windows shuts down or the user logs off: let Licht close its lamp data properly.
            SystemEvents.SessionEnding += OnSessionEnding;

            var menu = new ContextMenuStrip();
            var open = new ToolStripMenuItem("Licht öffnen", null, (s, e) => _window.Show());
            open.Font = new Font(open.Font, FontStyle.Bold);
            menu.Items.Add(open);
            menu.Items.Add(new ToolStripMenuItem("Im Browser öffnen", null, (s, e) => _channel.Send("open-browser")));
            menu.Items.Add(new ToolStripSeparator());
            _autostart = new ToolStripMenuItem("Mit Windows starten") { CheckOnClick = true, Checked = options.Autostart };
            _autostart.Click += (s, e) => _channel.Send(_autostart.Checked ? "autostart:on" : "autostart:off");
            menu.Items.Add(_autostart);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Licht beenden", null, (s, e) => Quit()));

            _tray = new NotifyIcon { Icon = _smallIcon, Text = "Licht", ContextMenuStrip = menu };
            _tray.MouseClick += OnTrayClick;

            if (options.SelfTest)
            {
                SelfTest();
                return;
            }
            _tray.Visible = true;
            _channel.Send("ready");
            if (options.Show) _window.Show();
            else if (options.Hello)
            {
                _tray.ShowBalloonTip(8000, "Licht läuft",
                    "Licht läuft jetzt im Hintergrund. Klick auf dieses Symbol öffnet Licht, Rechtsklick zeigt weitere Optionen.",
                    ToolTipIcon.None);
            }
        }

        private static Icon LoadIcon(Size size)
        {
            using (var stream = typeof(Host).Assembly.GetManifestResourceStream("licht.ico"))
                return stream != null ? new Icon(stream, size) : SystemIcons.Application;
        }

        private void Post(Action action)
        {
            try
            {
                if (!_ui.IsDisposed) _ui.BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // ending
            }
        }

        private void OnMessage(string line)
        {
            switch (line)
            {
                case "show": _window.Show(); break;
                case "autostart:on": _autostart.Checked = true; break;
                case "autostart:off": _autostart.Checked = false; break;
                case "quit": End(0); break;
            }
        }

        // Click: open Licht – or put it away if it was the active window right before the click.
        private void OnTrayClick(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            var now = DateTime.UtcNow;
            if ((now - _lastClick).TotalMilliseconds < SystemInformation.DoubleClickTime) return; // a double click is one click
            _lastClick = now;
            if (_window.WasActiveJustNow) _window.Hide();
            else _window.Show();
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e) => _channel.Send("quit");

        private void Quit()
        {
            _tray.Visible = false;
            _window.Close();
            _channel.Send("quit"); // Licht.exe ends; then our input closes and End() follows
        }

        private void End(int code)
        {
            if (_ending) return;
            _ending = true;
            ExitCode = code;
            _tray.Visible = false;
            _window.Close();
            ExitThread();
        }

        // Used by the build: opens the window with the real page and reports whether it loads.
        private void SelfTest()
        {
            var timeout = new Timer { Interval = 60000 };
            timeout.Tick += (s, e) =>
            {
                timeout.Stop();
                _channel.Send("selftest:fail timeout");
                End(1);
            };
            timeout.Start();
            _window.Loaded += async (ok, detail) =>
            {
                if (ok)
                {
                    // The real page, with its script running: title and menu button are there.
                    try
                    {
                        var page = await _window.Evaluate(
                            "document.title + '|' + !!document.getElementById('menu-btn') + '|' + typeof window.fetch");
                        ok = page == "\"Licht|true|function\"";
                        if (!ok) detail = "page " + page;
                    }
                    catch (Exception e)
                    {
                        ok = false;
                        detail = "script " + e.Message;
                    }
                }
                timeout.Stop();
                _channel.Send(ok ? "selftest:ok " + detail : "selftest:fail " + detail);
                End(ok ? 0 : 1);
            };
            _window.Unavailable += () =>
            {
                timeout.Stop();
                _channel.Send("selftest:fail webview2-missing");
                End(1);
            };
            _window.Show();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.SessionEnding -= OnSessionEnding;
                _tray.Visible = false;
                _tray.Dispose();
                _window.Dispose();
                _smallIcon.Dispose();
                _bigIcon.Dispose();
                _ui.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
