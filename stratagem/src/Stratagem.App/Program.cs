// Entry point of Stratagem.exe.
//
// Runs in the background with a tray icon; the settings are a web page (http://localhost:8133).
// Started by Windows at login (--autostart) it does not open the browser. A second start only opens the browser.
// Data: %USERPROFILE%\.stratagem (settings.json, stratagem.log).
// Environment (mainly for tests): STRATAGEM_DATA, PORT, STRATAGEM_NO_BROWSER=1, STRATAGEM_NO_HOOKS=1.

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
            if (!atLogin) OpenBrowser(url);
            return 0;
        }

        log.Log($"Stratagem {StratagemService.AppVersion} startet{(atLogin ? " (Autostart)" : "")}");
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
            var g = service?.Settings.Game ?? settings.Game;
            return (g.WindowTitle, g.ProcessName);
        });
        using var overlay = new RadialOverlay(() => game.WindowBounds);
        using var bindings = new GameBindingsSource(log.Log);

        service = new StratagemService(store, settings,
            new ServicePlatform(new WindowsInput(log.Log), overlay, game, new PrecisionTimer(), bindings.Read),
            log: log.Log);
        using var _service = service;
        bindings.Changed += () =>
        {
            log.Log("Tastenbelegung im Spiel geändert");
            service.ReloadBindings();
        };

        // Hooks: everything the player presses goes through the router first.
        using var hooks = new InputHooks(service.Router, log.Log);
        if (Environment.GetEnvironmentVariable("STRATAGEM_NO_HOOKS") != "1")
        {
            hooks.Start();
            log.Log(hooks.Installed ? "Eingabe-Hooks aktiv" : "Eingabe-Hooks NICHT aktiv – Radialmenü und Tasten gehen nicht");
        }
        game.Changed += () =>
        {
            if (game.IsFocused) hooks.Reinstall(); // Windows may have dropped them (timeout)
            log.Log(game.IsFocused ? "Spiel im Vordergrund" : game.IsRunning ? "Spiel im Hintergrund" : "Spiel nicht gestartet");
        };

        WebServer server;
        try
        {
            server = WebServer.StartAsync(service, new WebServerOptions(port, settings.Web.AllowLan, settings.Web.Token,
                new DesktopActions(dataDir, () => ui.Post(_ => Application.Exit(), null)), Environment.GetEnvironmentVariable("STRATAGEM_WEB_DIR"), log.Log)).GetAwaiter().GetResult();
        }
        catch (IOException e)
        {
            throw new InvalidOperationException($"Port {port} ist belegt ({e.Message}). Läuft Stratagem schon, oder nutzt ein anderes Programm den Port?", e);
        }
        log.Log($"Weboberfläche auf {url}{(settings.Web.AllowLan ? " (auch im Heimnetz)" : "")}");

        using var tray = new TrayIcon(() => OpenBrowser(url), paused => service.SetPaused(paused), Application.Exit);
        void UpdateTray()
        {
            var s = service.GetState();
            tray.Update(s.Paused, s.Game.Focused, s.List.Items.Count);
            var r = service.Settings.Radial;
            overlay.Configure(r.SizePx, r.Opacity);
        }
        service.Changed += () => ui.Post(_ => UpdateTray(), null);
        UpdateTray();
        log.Log("Tray-Symbol bereit");

        // Closes a forgotten tap-menu.
        using var timer = new System.Windows.Forms.Timer { Interval = 250 };
        timer.Tick += (_, _) => service.Radial.OnTimer();
        timer.Start();

        if (!atLogin && Environment.GetEnvironmentVariable("STRATAGEM_NO_BROWSER") != "1") OpenBrowser(url);
        if (!settings.List.Manual.Any() && settings.List.Source == ListSource.Manual) tray.Notify("Liste ist leer – in der App Stratagems hinzufügen.");

        Application.Run();

        log.Log("Stratagem wird beendet");
        service.Radial.Enabled = false;
        service.Runner.Cancel();
        server.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        return 0;
    }

    internal static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { /* no browser: the tray still works */ }
    }

    private sealed class DesktopActions(string dataDir, Action quit) : IDesktop
    {
        public bool Autostart
        {
            get => Windows.Autostart.Enabled;
            set => Windows.Autostart.Enabled = value;
        }

        public void Quit() => quit();

        public void OpenDataFolder() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dataDir}\"") { UseShellExecute = true });
    }
}
