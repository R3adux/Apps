using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace Licht.Desktop
{
    /// <summary>
    /// The Licht window: the same page as in the browser, in a small window of its own (Edge WebView2).
    /// First opens at the bottom right, next to the tray icon; afterwards where the user left it.
    /// Closing only hides it, so it opens again instantly; after 10 minutes hidden it is freed
    /// (WebView2 needs memory). All members on the UI thread.
    /// </summary>
    internal sealed class AppWindow : IDisposable
    {
        private static readonly Size DefaultSize = new Size(400, 700);
        private static readonly Size MinSize = new Size(320, 420);
        private const int Margin = 12;
        private static readonly TimeSpan KeepHidden = TimeSpan.FromMinutes(10);

        // Licht only talks to the lamps and to itself: nothing in the background (no updates, no
        // statistics, no "safe browsing" lookups) – the page comes from this computer.
        private const string BrowserArguments =
            "--disable-background-networking --disable-component-update --disable-domain-reliability " +
            "--disable-sync --no-pings --no-first-run";

        private readonly Uri _url;
        private readonly string _dataDir;
        private readonly Icon _icon;
        private readonly Channel _channel;
        private readonly Timer _freeTimer;
        private Form? _form;
        private WebView2? _web;
        private DateTime _deactivatedAt = DateTime.MinValue;
        private bool _closing;

        public AppWindow(Uri url, string dataDir, Icon icon, Channel channel)
        {
            _url = url;
            _dataDir = dataDir;
            _icon = icon;
            _channel = channel;
            _freeTimer = new Timer { Interval = (int)KeepHidden.TotalMilliseconds };
            _freeTimer.Tick += (s, e) =>
            {
                _freeTimer.Stop();
                if (_form != null && !_form.Visible) Free();
            };
        }

        /// <summary>The page finished loading the first time: (success, "&lt;ms&gt; &lt;details&gt;").</summary>
        public event Action<bool, string>? Loaded;

        /// <summary>WebView2 is missing or broken – Licht should open in the browser instead.</summary>
        public event Action? Unavailable;

        private string BoundsFile => Path.Combine(_dataDir, "fenster.txt");

        /// <summary>Runs a script in the page and returns its result as JSON (self-test).</summary>
        public async Task<string> Evaluate(string script)
        {
            var core = _web?.CoreWebView2 ?? throw new InvalidOperationException("Fenster nicht geladen");
            return await core.ExecuteScriptAsync(script);
        }

        /// <summary>True if the window was the active window up to a moment ago (a tray click deactivates it).</summary>
        public bool WasActiveJustNow =>
            _form != null && _form.Visible && _form.WindowState != FormWindowState.Minimized
            && (Form.ActiveForm == _form || (DateTime.UtcNow - _deactivatedAt).TotalMilliseconds < 600);

        public void Show()
        {
            _freeTimer.Stop();
            if (_form == null || _form.IsDisposed)
            {
                if (!WebView2Available()) return;
                _form = Create();
            }
            if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
            _form.Show();
            _form.Activate();
            Native.BringToFront(_form.Handle);
            _web?.Focus();
        }

        public void Hide()
        {
            if (_form == null || _form.IsDisposed || !_form.Visible) return;
            SaveBounds(_form);
            _form.Hide();
            _freeTimer.Start();
        }

        /// <summary>Licht ends: close for good.</summary>
        public void Close()
        {
            _closing = true;
            _freeTimer.Stop();
            if (_form != null && !_form.IsDisposed)
            {
                if (_form.Visible) SaveBounds(_form);
                Free();
            }
        }

        private bool WebView2Available()
        {
            try
            {
                if (!string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString())) return true;
            }
            catch (Exception e)
            {
                _channel.Log($"WebView2 fehlt ({e.GetType().Name})");
            }
            _channel.Send("window:unavailable");
            Unavailable?.Invoke();
            return false;
        }

        private Form Create()
        {
            var started = Stopwatch.StartNew();
            var dark = IsDarkMode();
            var back = dark ? Color.FromArgb(0x10, 0x12, 0x17) : Color.FromArgb(0xf3, 0xf4, 0xf7); // as the page
            var form = new Form
            {
                Text = "Licht",
                Icon = _icon,
                BackColor = back,
                StartPosition = FormStartPosition.Manual,
                AutoScaleMode = AutoScaleMode.Dpi,
                KeyPreview = false,
            };
            form.MinimumSize = form.LogicalToDeviceUnits(MinSize);
            PlaceNearTray(form);
            RestoreBounds(form);

            var web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = back };
            form.Controls.Add(web);
            _web = web;

            form.HandleCreated += (s, e) => Native.UseDarkTitleBar(form.Handle, dark);
            form.Deactivate += (s, e) => _deactivatedAt = DateTime.UtcNow;
            form.FormClosing += (s, e) =>
            {
                if (_closing || e.CloseReason != CloseReason.UserClosing) return;
                e.Cancel = true; // the X only hides: Licht keeps running in the tray
                Hide();
            };
            var shown = false;
            form.Shown += async (s, e) =>
            {
                if (shown) return;
                shown = true;
                await Load(form, web, started);
            };
            return form;
        }

        private async Task Load(Form form, WebView2 web, Stopwatch started)
        {
            try
            {
                var options = new CoreWebView2EnvironmentOptions(BrowserArguments, "de-DE");
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_dataDir, "WebView2"), options);
                await web.EnsureCoreWebView2Async(env);
                if (form.IsDisposed) return;
                var core = web.CoreWebView2;
                var settings = core.Settings;
                settings.AreDevToolsEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false; // no F5, Ctrl+P, … – an app, not a browser
                settings.IsZoomControlEnabled = false;
                settings.IsPinchZoomEnabled = false;
                settings.IsSwipeNavigationEnabled = false;
                settings.IsGeneralAutofillEnabled = false;
                settings.IsPasswordAutosaveEnabled = false;
                settings.IsReputationCheckingRequired = false; // no SmartScreen lookups for our own page

                core.NavigationStarting += (s, e) =>
                {
                    if (!IsOurs(e.Uri)) e.Cancel = true;
                };
                core.NewWindowRequested += (s, e) => e.Handled = true;
                core.DownloadStarting += (s, e) => e.Cancel = true;
                core.PermissionRequested += (s, e) => e.State = CoreWebView2PermissionState.Deny;
                // Right click: only the editing entries (copy, paste, …), nothing browser-like.
                core.ContextMenuRequested += (s, e) =>
                {
                    var keep = new[] { "cut", "copy", "paste", "selectAll", "undo", "redo" };
                    foreach (var item in e.MenuItems.ToList())
                        if (!keep.Contains(item.Name)) e.MenuItems.Remove(item);
                    if (e.MenuItems.Count == 0) e.Handled = true;
                };
                core.ProcessFailed += (s, e) =>
                {
                    _channel.Log($"Fenster: WebView2-Prozess beendet ({e.ProcessFailedKind})");
                    if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited) BeginFree();
                    else if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited
                             || e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive) core.Reload();
                };

                var first = true;
                var attempts = 0;
                core.NavigationCompleted += (s, e) =>
                {
                    if (!e.IsSuccess && attempts++ < 5)
                    {
                        // Licht.exe is still starting: try again shortly.
                        var retry = new Timer { Interval = 700 };
                        retry.Tick += (_, __) =>
                        {
                            retry.Dispose();
                            if (!form.IsDisposed) core.Navigate(_url.ToString());
                        };
                        retry.Start();
                        return;
                    }
                    if (!first) return;
                    first = false;
                    var detail = $"{Channel.Ms(started.Elapsed)} WebView2 {env.BrowserVersionString}";
                    if (e.IsSuccess) _channel.Send("window:loaded " + detail);
                    else _channel.Log($"Fenster: Seite nicht geladen ({e.WebErrorStatus})");
                    Loaded?.Invoke(e.IsSuccess, e.IsSuccess ? detail : e.WebErrorStatus.ToString());
                };
                core.Navigate(_url.ToString());
            }
            catch (Exception e)
            {
                if (form.IsDisposed || _closing) return;
                _channel.Log($"Fenster nicht möglich ({e.GetType().Name}: {e.Message})");
                Free();
                _channel.Send("window:unavailable");
                Unavailable?.Invoke();
                Loaded?.Invoke(false, e.GetType().Name);
            }
        }

        private bool IsOurs(string uri) =>
            uri == "about:blank"
            || (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == _url.Scheme && u.Port == _url.Port
                && (u.IsLoopback || string.Equals(u.Host, _url.Host, StringComparison.OrdinalIgnoreCase)));

        // Freeing from inside a WebView2 event is not allowed: do it right after.
        private void BeginFree()
        {
            var form = _form;
            if (form == null || form.IsDisposed) return;
            form.BeginInvoke(new Action(Free));
        }

        private void Free()
        {
            var form = _form;
            _form = null;
            _web = null;
            if (form == null || form.IsDisposed) return;
            _closing = true;
            try
            {
                form.Close();
                form.Dispose(); // also ends the WebView2 processes
            }
            finally
            {
                _closing = false;
            }
        }

        private static void PlaceNearTray(Form form)
        {
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            var size = form.LogicalToDeviceUnits(DefaultSize);
            var margin = form.LogicalToDeviceUnits(Margin);
            var width = Math.Min(size.Width, area.Width - 2 * margin);
            var height = Math.Min(size.Height, area.Height - 2 * margin);
            form.Bounds = new Rectangle(area.Right - width - margin, area.Bottom - height - margin, width, height);
        }

        private static bool IsDarkMode()
        {
            try
            {
                return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1) is int light && light == 0;
            }
            catch (Exception)
            {
                return true;
            }
        }

        // Same place and size as last time, if that is still on a screen.
        private void RestoreBounds(Form form)
        {
            try
            {
                if (!File.Exists(BoundsFile)) return;
                var parts = File.ReadAllText(BoundsFile).Trim().Split(',')
                    .Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
                if (parts.Length != 4) return;
                var rect = new Rectangle(parts[0], parts[1],
                    Math.Max(parts[2], form.MinimumSize.Width), Math.Max(parts[3], form.MinimumSize.Height));
                var visible = Screen.AllScreens.Any(screen =>
                {
                    var overlap = Rectangle.Intersect(screen.WorkingArea, rect);
                    return overlap.Width >= 120 && overlap.Height >= 80;
                });
                if (visible) form.Bounds = rect;
            }
            catch (Exception)
            {
                // only a convenience
            }
        }

        private void SaveBounds(Form form)
        {
            try
            {
                var r = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
                Directory.CreateDirectory(_dataDir);
                File.WriteAllText(BoundsFile, string.Join(",", new[] { r.X, r.Y, r.Width, r.Height }
                    .Select(v => v.ToString(CultureInfo.InvariantCulture))));
            }
            catch (Exception)
            {
                // only a convenience
            }
        }

        public void Dispose()
        {
            Close();
            _freeTimer.Dispose();
        }
    }
}
