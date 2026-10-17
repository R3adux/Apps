using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Licht.Desktop
{
    /// <summary>
    /// LichtFenster.exe – started by Licht.exe, never by the user. Shows the tray icon and the Licht window.
    ///
    /// Talks to Licht.exe through standard input/output, one line per message (UTF-8):
    ///   from Licht:  show · autostart:on · autostart:off · quit
    ///   to Licht:    ready · open-browser · quit · autostart:on · autostart:off · log:&lt;text&gt;
    ///                window:loaded &lt;ms&gt; · window:unavailable · selftest:ok &lt;ms&gt; · selftest:fail &lt;reason&gt;
    /// Ends when Licht.exe ends (its standard input closes).
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Options options;
            try
            {
                options = Options.Parse(args);
            }
            catch (ArgumentException e)
            {
                MessageBox.Show(e.Message + "\n\nBitte Licht.exe starten.", "Licht", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 2;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var host = new Host(options))
            {
                Application.Run(host);
                return host.ExitCode;
            }
        }
    }

    internal sealed class Options
    {
        public Uri Url = null!;
        public string DataDir = null!;
        public bool Autostart;
        public bool Show;
        public bool Hello;
        public bool SelfTest;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]}: Wert fehlt");
                switch (args[i])
                {
                    case "--url": o.Url = new Uri(Value()); break;
                    case "--data": o.DataDir = Value(); break;
                    case "--autostart": o.Autostart = Value() == "on"; break;
                    case "--show": o.Show = true; break;
                    case "--hello": o.Hello = true; break;
                    case "--selftest": o.SelfTest = true; break;
                    default: throw new ArgumentException($"Unbekannte Angabe: {args[i]}");
                }
            }
            if (o.Url == null || o.DataDir == null) throw new ArgumentException("Dieses Programm gehört zu Licht.");
            return o;
        }
    }

    /// <summary>Line-based messages with Licht.exe over standard input/output.</summary>
    internal sealed class Channel
    {
        private readonly StreamWriter _out;
        private readonly object _lock = new object();

        /// <param name="onLine">Called on a background thread for every line from Licht.exe.</param>
        /// <param name="onClosed">Called on a background thread when Licht.exe is gone.</param>
        public Channel(Action<string> onLine, Action onClosed)
        {
            _out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            var reader = new Thread(() =>
            {
                try
                {
                    using (var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
                    {
                        string? line;
                        while ((line = input.ReadLine()) != null)
                        {
                            line = line.Trim();
                            if (line.Length > 0) onLine(line);
                        }
                    }
                }
                catch (Exception)
                {
                    // pipe broken: same as closed
                }
                onClosed();
            })
            { IsBackground = true, Name = "Licht input" };
            reader.Start();
        }

        public void Send(string line)
        {
            lock (_lock)
            {
                try
                {
                    _out.WriteLine(line);
                }
                catch (Exception)
                {
                    // Licht.exe is gone; the input side notices and ends this program
                }
            }
        }

        public void Log(string text) => Send("log:" + text.Replace('\r', ' ').Replace('\n', ' '));

        public static string Ms(TimeSpan t) => ((int)t.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
    }
}
