using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Licht.Fenster;

namespace LichtFenster
{
    /// <summary>
    /// LichtFenster.exe – started by Licht.exe with: --data &lt;data folder&gt; [--show].
    /// Talks to Licht over standard input/output (pc/bridge.js); ends when Licht ends.
    /// For the build checks: --render &lt;folder&gt; draws the window in typical states into PNG files.
    /// </summary>
    public partial class App : Application
    {
        private const int MaxUiErrors = 20;

        private HubConnection? _hub;
        private Tray? _tray;
        private MainWindow? _window;
        private bool _ending;
        private int _uiErrors;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var options = Options.Parse(e.Args);
            Theme.Start(this);

            if (options.RenderTo != null)
            {
                var ok = RenderMode.Run(this, options.RenderTo);
                Shutdown(ok ? 0 : 1);
                return;
            }
            if (options.DataDir == null)
            {
                MessageBox.Show("LichtFenster.exe ist ein Teil von Licht. Bitte Licht.exe starten.", "Licht",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(2);
                return;
            }

            var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            _hub = new HubConnection(input, output, new DispatcherSynchronizationContext(Dispatcher));
            Log.Hub = _hub;
            WatchForErrors();

            var window = _window = new MainWindow(new Placement(options.DataDir));
            var ui = new WindowUi(window.View, window.ShowAndActivate, window.ShowAndActivate, End);
            var main = new MainViewModel(_hub, ui, SystemClock.Instance, new LightsCache(options.DataDir));
            window.DataContext = main;
            var tray = _tray = new Tray(window, window.Toggle, window.ShowAndActivate,
                () => main.Autostart, on => _ = main.SetAutostart(on), () => QuitFromTray(main));
            var hintShown = false;
            window.HiddenByUser += () =>
            {
                if (!main.ShowHint || hintShown) return;
                hintShown = true;
                tray.Balloon("Licht läuft im Hintergrund weiter",
                    "Über das Lampen-Symbol unten rechts öffnest du Licht wieder. Beenden: Rechtsklick → „Licht beenden“.");
            };

            _hub.Closed += End;
            _hub.Start();
            main.Start();
            if (options.Show) window.ShowAndActivate();
            Log.Write($"Fenster bereit (Tray-Symbol da{(options.Show ? ", Fenster offen" : "")})");
        }

        // "Licht beenden" in the tray menu: Licht ends the window program; if it does not answer, end anyway.
        private void QuitFromTray(MainViewModel main)
        {
            main.Quit();
            var fallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            fallback.Tick += (_, _) =>
            {
                fallback.Stop();
                End();
            };
            fallback.Start();
        }

        /// <summary>Licht ended (or asked to end): tray symbol away, window closed, program ends.</summary>
        private void End()
        {
            if (_ending) return;
            _ending = true;
            _tray?.Dispose();
            _window?.CloseForGood();
            Shutdown(0);
        }

        private void WatchForErrors()
        {
            // An error in the window is written to licht.log; the window stays usable. Many errors in a row:
            // end – Licht starts the window program again.
            DispatcherUnhandledException += (_, e) =>
            {
                Log.Write($"Fehler im Fenster: {e.Exception}");
                e.Handled = ++_uiErrors < MaxUiErrors;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Log.Write($"Fenster-Programm abgestürzt: {e.ExceptionObject}");
                try
                {
                    _tray?.Dispose();
                }
                catch (Exception)
                {
                    // ending anyway
                }
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.Write($"Fehler im Fenster (Hintergrund): {e.Exception}");
                e.SetObserved();
            };
        }
    }

    /// <summary>The command line of LichtFenster.exe.</summary>
    internal sealed class Options
    {
        public string? DataDir { get; private set; }
        public bool Show { get; private set; }
        public string? RenderTo { get; private set; }

        public static Options Parse(IList<string> args)
        {
            var options = new Options();
            for (var i = 0; i < args.Count; i++)
            {
                switch (args[i])
                {
                    case "--data" when i + 1 < args.Count:
                        options.DataDir = args[++i];
                        break;
                    case "--render" when i + 1 < args.Count:
                        options.RenderTo = args[++i];
                        break;
                    case "--show":
                        options.Show = true;
                        break;
                }
            }
            return options;
        }
    }

    /// <summary>Lines for licht.log (Licht writes them, marked "Fenster:").</summary>
    internal static class Log
    {
        public static IHub? Hub { get; set; }

        public static void Write(string text)
        {
            try
            {
                Hub?.Send(new Dictionary<string, object?> { ["t"] = "log", ["text"] = text });
            }
            catch (Exception)
            {
                // the log must never break the window
            }
        }
    }
}
