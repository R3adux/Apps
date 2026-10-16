using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace Stratagem.Windows;

/// <summary>
/// The Stratagem window: the settings page (the same as in the browser) in a small window of its own.
/// It exists only while open: closing it ends the WebView2 (Edge) processes too, so during play it costs
/// nothing. Without the WebView2 runtime (only very old Windows 10 setups) the browser opens instead.
/// All members on the UI thread.
/// </summary>
public sealed class AppWindow : IDisposable
{
    private static readonly Size DefaultSize = new(940, 780);
    private static readonly Size MinSize = new(440, 420);

    private readonly Uri _url;
    private readonly string _dataDir;
    private readonly Icon _icon;
    private readonly Action<string> _log;
    private readonly Action _openBrowser;
    private Form? _form;

    public AppWindow(string url, string dataDir, Icon icon, Action<string> log, Action openBrowser)
    {
        _url = new Uri(url);
        _dataDir = dataDir;
        _icon = icon;
        _log = log;
        _openBrowser = openBrowser;
    }

    /// <summary>
    /// Called by a second start of Stratagem.exe before it asks the running one to show its window:
    /// Windows lets a background process come to the front only with this permission.
    /// </summary>
    public static void LetOtherProcessComeToFront() => Native.AllowSetForegroundWindow(Native.ASFW_ANY);

    /// <summary>The window was closed; true if by the user (Stratagem keeps running in the tray), false when quitting.</summary>
    public event Action<bool>? Closed;

    private string BoundsFile => Path.Combine(_dataDir, "window.json");

    public void Show()
    {
        if (_form is { IsDisposed: false } open)
        {
            BringToFront(open);
            return;
        }
        try { _ = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (Exception e)
        {
            _log($"App-Fenster nicht möglich, WebView2 fehlt ({e.GetType().Name}) – öffne den Browser");
            _openBrowser();
            return;
        }
        _form = Create();
        _form.Show();
        BringToFront(_form);
    }

    private Form Create()
    {
        var dark = IsDarkMode();
        var back = dark ? Color.FromArgb(0x10, 0x12, 0x17) : Color.FromArgb(0xf3, 0xf4, 0xf7);
        var form = new Form
        {
            Text = "Stratagem",
            Icon = _icon,
            BackColor = back,
            StartPosition = FormStartPosition.Manual,
            AutoScaleMode = AutoScaleMode.Dpi,
        };
        form.MinimumSize = form.LogicalToDeviceUnits(MinSize);
        var size = form.LogicalToDeviceUnits(DefaultSize);
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        form.Bounds = new Rectangle(screen.X + (screen.Width - size.Width) / 2, screen.Y + (screen.Height - size.Height) / 2,
            Math.Min(size.Width, screen.Width), Math.Min(size.Height, screen.Height));
        var maximized = RestoreBounds(form);
        if (maximized) form.WindowState = FormWindowState.Maximized;

        var web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = back };
        form.Controls.Add(web);
        form.HandleCreated += (_, _) =>
        {
            var on = dark ? 1 : 0;
            _ = Native.DwmSetWindowAttribute(form.Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        };
        form.Shown += async (_, _) => await Load(form, web);
        form.FormClosing += (_, _) => SaveBounds(form);
        form.FormClosed += (_, e) =>
        {
            web.Dispose(); // ends the WebView2 processes; the form itself is disposed by WinForms after closing
            if (_form == form) _form = null;
            Closed?.Invoke(e.CloseReason == CloseReason.UserClosing);
        };
        return form;
    }

    private async Task Load(Form form, WebView2 web)
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_dataDir, "WebView2"));
            await web.EnsureCoreWebView2Async(env);
            var core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false; // no F5, Ctrl+P, … – it is an app, not a browser
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            // Links to anything else (phone links, help pages) open in the browser.
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenExternal(e.Uri);
            };
            core.NavigationStarting += (_, e) =>
            {
                if (IsOurs(e.Uri)) return;
                e.Cancel = true;
                OpenExternal(e.Uri);
            };
            var first = true;
            core.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) _log($"App-Fenster: Seite nicht geladen ({e.WebErrorStatus})");
                else if (first) _log($"App-Fenster geladen (WebView2 {core.Environment.BrowserVersionString})");
                first = false;
            };
            core.Navigate(_url.ToString());
        }
        catch (Exception e)
        {
            if (form.IsDisposed || web.IsDisposed) return; // closed while starting
            _log($"App-Fenster nicht möglich ({e.Message}) – öffne den Browser");
            form.Close();
            _openBrowser();
        }
    }

    private bool IsOurs(string uri) =>
        uri == "about:blank"
        || (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttp
            && u.Port == _url.Port && (u.IsLoopback || u.Host == _url.Host));

    private void OpenExternal(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) return;
        try { Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true }); }
        catch (Exception e) { _log($"Browser nicht startbar: {e.Message}"); }
    }

    private static void BringToFront(Form form)
    {
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        form.Activate();
        Native.SetForegroundWindow(form.Handle);
    }

    private static bool IsDarkMode()
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;
        }
        catch (Exception) { return true; }
    }

    private sealed record SavedBounds(int X, int Y, int Width, int Height, bool Maximized);

    // Same place and size as last time, if that is still on a screen.
    private bool RestoreBounds(Form form)
    {
        try
        {
            if (!File.Exists(BoundsFile)) return false;
            var b = JsonSerializer.Deserialize<SavedBounds>(File.ReadAllText(BoundsFile));
            if (b is null) return false;
            var rect = new Rectangle(b.X, b.Y, Math.Max(b.Width, form.MinimumSize.Width), Math.Max(b.Height, form.MinimumSize.Height));
            var visible = Screen.AllScreens.Any(s =>
            {
                var overlap = Rectangle.Intersect(s.WorkingArea, rect);
                return overlap.Width >= 120 && overlap.Height >= 80;
            });
            if (!visible) return false;
            form.Bounds = rect;
            return b.Maximized;
        }
        catch (Exception) { return false; }
    }

    private void SaveBounds(Form form)
    {
        try
        {
            var r = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
            Directory.CreateDirectory(_dataDir);
            File.WriteAllText(BoundsFile, JsonSerializer.Serialize(new SavedBounds(r.X, r.Y, r.Width, r.Height, form.WindowState == FormWindowState.Maximized)));
        }
        catch (Exception) { /* only a convenience */ }
    }

    public void Dispose()
    {
        if (_form is { IsDisposed: false } f) f.Close();
        _form = null;
    }
}
