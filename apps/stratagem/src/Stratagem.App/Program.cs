// Entry point of Stratagem.exe.
//
// Runs in the background with a tray icon; the settings are a web page (http://localhost:8133), shown in the
// Stratagem window (WebView2) or, as a fallback, in the browser. The phone opens http://<name>.local (mDNS).
// Started by Windows at login (--autostart) it opens no window. A second start only shows the window.
// Data: %USERPROFILE%\.stratagem (settings.json, stratagem.log, window.json, WebView2\).
// Environment (mainly for tests): STRATAGEM_DATA, PORT, STRATAGEM_NO_BROWSER=1 (no window at start), STRATAGEM_NO_HOOKS=1.

using System.Diagnostics;
using System.Windows.Forms;
using Stratagem.Core.Service;
using Stratagem.Core.Settings;
using Stratagem.Server;
using Stratagem.Windows;

namespace Stratagem.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var atLogin = args.Contains("--autostart");
        var dataDir = Environment.GetEnvironmentVariable("STRATAGEM_DATA")
                      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".stratagem");
        using var log = new FileLog(Path.Combine(dataDir, "stratagem.log"));

        var store = new SettingsStore(Path.Combine(dataDir, "settings.json"), log.Log);
        var settings = store.Load();
        var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) ? p : settings.Web.Port;
        var url = $"http://localhost:{port}";

        using var single = new Mutex(true, $@"Local\Stratagem-{Environment.UserName}", out var first);
        if (!first)
        {
            if (!atLogin && !AskToShowWindow(port)) OpenBrowser(url);
            return 0;
        }

        log.Log($"Stratagem {StratagemService.AppVersion} startet{(atLogin ? " (Autostart)" : "")}");
        // No long blocking garbage collections: the hook thread must never wait.
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => log.Log($"Unerwarteter Fehler: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Log($"Unerwarteter Fehler: {e.ExceptionObject}");

        try
        {
            return Run(settings, store, log, url, port, dataDir, atLogin);
        }
        catch (Exception e)
        {
            log.Log($"Start fehlgeschlagen: {e}");
            MessageBox.Show(e.Message, "Stratagem", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int Run(AppSettings settings, SettingsStore store, FileLog log, string url, int port, string dataDir, bool atLogin)
    {
        StratagemService? service = null;
        var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        using var game = new GameWatcher(() =>
        {
            var g = (service?.Current ?? settings).Game;
            return (g.WindowTitle, g.ProcessName);
        });
        using var overlay = new RadialOverlay(() => game.WindowBounds, () => service?.Radial.OnTimer());
        using var bindings = new GameBindingsSource(log.Log);

        service = new StratagemService(store, settings,
            new ServicePlatform(new WindowsInput(log.Log), overlay, game, new PrecisionTimer(), bindings.Read,
                new GdiScreenCapture(() => game.Window, log.Log), dataDir),
            log: log.Log);
        using var _service = service;
        bindings.Changed += () =>
        {
            log.Log("Tastenbelegung im Spiel geändert");
            service.ReloadBindings();
        };

        // Compile the input paths now, not on the first Ctrl press in the game.
        Warmup.Run();

        // Hooks exist only while needed: keyboard while active (game in front, not paused),
        // mouse only while the radial menu is open or a mouse button is bound.
        using var hooks = new InputHooks(service.Router, log.Log);
        var hooksEnabled = Environment.GetEnvironmentVariable("STRATAGEM_NO_HOOKS") != "1";
        void SyncHooks()
        {
            var active = hooksEnabled && service.IsActive;
            hooks.Update(active, active && service.Router.NeedsMouse);
        }
        if (hooksEnabled) hooks.Start();
        service.ActiveChanged += active =>
        {
            SyncHooks();
            log.Log(active ? "Aktiv – Eingaben werden abgefangen" : "Inaktiv – keine Eingaben werden abgefangen");
        };
        service.Router.MouseNeedChanged += SyncHooks; // also from inside a hook callback: SyncHooks only posts a message
        SyncHooks();
        game.Changed += () => log.Log(game.IsFocused ? "Spiel im Vordergrund" : game.IsRunning ? "Spiel im Hintergrund" : "Spiel nicht gestartet");

        AppWindow? window = null;
        WebServer server;
        try
        {
            server = WebServer.StartAsync(service, new WebServerOptions(port, settings.Web.AllowLan, settings.Web.Token,
                new DesktopActions(dataDir, () => ui.Post(_ => Application.Exit(), null), () => ui.Post(_ => window?.Show(), null)),
                Environment.GetEnvironmentVariable("STRATAGEM_WEB_DIR"), log.Log, settings.Web.LanPort)).GetAwaiter().GetResult();
        }
        catch (IOException e)
        {
            throw new InvalidOperationException($"Port {port} ist belegt ({e.Message}). Läuft Stratagem schon, oder nutzt ein anderes Programm den Port?", e);
        }
        log.Log($"Weboberfläche auf {url}");

        // Phone: http://<name>.local – answered here, so no router setting is needed.
        MdnsResponder? mdns = null;
        if (settings.Web.AllowLan)
        {
            try { mdns = new MdnsResponder(() => service.Current.Web.Name, log.Log); }
            catch (Exception e) { log.Log($"Name im Heimnetz nicht möglich ({e.Message}) – das Handy braucht die IP-Adresse"); }
            var lanPort = server.LanPort == 80 ? "" : $":{(server.LanPort != 0 ? server.LanPort : port)}";
            log.Log($"Im Heimnetz: http://{settings.Web.Name}.local{lanPort}");
        }
        using var _mdns = mdns;

        using var tray = new TrayIcon(() => window?.Show(), () => OpenBrowser(url), paused => service.SetPaused(paused), Application.Exit);
        window = new AppWindow(url, dataDir, tray.AppIcon, log.Log, () => OpenBrowser(url));
        using var _window = window;
        var toldBackground = false;
        window.Closed += byUser =>
        {
            if (!byUser || toldBackground) return;
            toldBackground = true;
            tray.Notify("Stratagem läuft im Hintergrund weiter. Öffnen: Symbol unten rechts in der Taskleiste.");
        };
        // Tray and overlay follow the service, but only touch Windows when something they show changed,
        // and many changes in a row (a macro, a burst of log lines) become one update on the UI thread.
        var uiPending = 0;
        UiState? shown = null;
        void UpdateUi()
        {
            Interlocked.Exchange(ref uiPending, 0);
            var current = service.Current;
            var now = new UiState(current.Paused, game.IsFocused, current.Radial.SizePx, current.Radial.Opacity, service.Items);
            if (now == shown) return; // the list compares by reference: a new list means new content
            tray.Update(now.Paused, now.Focused, now.Items.Count);
            overlay.Configure(now.Size, now.Opacity, now.Items); // also pre-renders, so the menu appears instantly
            shown = now;
        }
        service.Changed += () =>
        {
            if (Interlocked.Exchange(ref uiPending, 1) == 0) ui.Post(_ => UpdateUi(), null);
        };
        UpdateUi();
        log.Log("Tray-Symbol bereit");

        if (!atLogin && Environment.GetEnvironmentVariable("STRATAGEM_NO_BROWSER") != "1") window.Show();
        if (!settings.List.Manual.Any() && settings.List.Source == ListSource.Manual) tray.Notify("Liste ist leer – in der App Stratagems hinzufügen.");

        Application.Run();

        log.Log("Stratagem wird beendet");
        hooks.Update(false, false);
        service.Radial.Enabled = false;
        service.Runner.Cancel();
        server.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        return 0;
    }

    private sealed record UiState(bool Paused, bool Focused, int Size, double Opacity, IReadOnlyList<Stratagem.Core.Model.StratagemItem> Items);

    // A second start: the running Stratagem shows its window (false: it did not answer).
    private static bool AskToShowWindow(int port)
    {
        try
        {
            AppWindow.LetOtherProcessComeToFront();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/show-window");
            req.Headers.Add(WebServer.HeaderName, "1");
            return http.Send(req).IsSuccessStatusCode;
        }
        catch (Exception) { return false; }
    }

    internal static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { /* no browser: the tray still works */ }
    }

    private sealed class DesktopActions(string dataDir, Action quit, Action showWindow) : IDesktop
    {
        public bool Autostart
        {
            get => Windows.Autostart.Enabled;
            set => Windows.Autostart.Enabled = value;
        }

        public void Quit() => quit();

        public void ShowWindow() => showWindow();

        public void OpenDataFolder() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dataDir}\"") { UseShellExecute = true });
    }
}
