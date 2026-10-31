using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Licht.Fenster;

namespace LichtFenster
{
    /// <summary>
    /// LichtFenster.exe --render &lt;folder&gt;: draws the window in typical states (light and dark) into PNG files,
    /// with made-up lamps. The build (CI) runs it, so every change to the look can be checked by eye; it also
    /// catches errors in the XAML that only show when the window is really drawn.
    /// </summary>
    internal static class RenderMode
    {
        private const double Width = 460;

        public static bool Run(Application app, string folder)
        {
            Directory.CreateDirectory(folder);
            MainView.Animations = false;
            var failures = 0;
            void Shot(string name, Action<Scene> setup, double width = Width, double? height = null)
            {
                foreach (var dark in new[] { true, false })
                {
                    var file = Path.Combine(folder, $"{name}-{(dark ? "dunkel" : "hell")}.png");
                    try
                    {
                        Theme.Apply(app, dark);
                        var scene = new Scene();
                        setup(scene);
                        Save(scene.View, width, height, file);
                        Console.WriteLine($"Bild: {file}");
                    }
                    catch (Exception e)
                    {
                        failures++;
                        Console.WriteLine($"FEHLER bei {file}: {e}");
                    }
                }
            }

            Shot("lampen", s => s.Lamps(SampleLamps));
            Shot("lampen-breit", s => s.Lamps(SampleLamps), width: 1040);
            Shot("leer", s => s.Lamps(), height: 520);
            Shot("laden", s => { }, height: 360);
            Shot("hinzufuegen", s =>
            {
                s.Lamps(SampleLamps.Take(1).ToArray());
                s.Hub.Answer("discover", _ => Json.Parse("""[{"name":"SMART+ Classic A60","mode":2,"addresses":["192.168.178.42:5540"],"matchesCode":true}]"""));
                s.Hub.Answer("add", _ => throw new HubException("Der Code ist ungültig. Bitte die 11 Ziffern oder den ganzen geteilten Code prüfen.", 400));
                s.Main.AddCommand.Execute(null);
                var add = s.Ui.Add!;
                add.Code = "3497-011-2333";
                add.SearchCommand.Execute(null);
                add.SubmitCommand.Execute(null);
            }, height: 820);
            Shot("teilen-alle", s =>
            {
                s.Lamps(SampleLamps);
                s.Hub.Answer("share", _ => Json.Parse("""{"code":"MT:Y.K90AFN00KA0648G00*Y.K90AFN00KA0648G00*Y.K90AFN00KA0648G00","lamps":["Wohnzimmer","Leselampe","Flur"],"skipped":["Balkon"],"validMinutes":15}"""));
                s.Main.ShareAllCommand.Execute(null);
            }, height: 820);
            Shot("teilen-eine", s =>
            {
                s.Lamps(SampleLamps);
                s.Hub.Answer("share", _ => Json.Parse("""{"code":"MT:Y.K90AFN00KA0648G00","manual":"34970112332","lamps":["Wohnzimmer"],"skipped":[],"validMinutes":15}"""));
                s.Main.Lamps[0].ShareCommand.Execute(null);
            }, height: 860);
            Shot("entfernen", s =>
            {
                s.Lamps(SampleLamps);
                s.Main.Lamps[2].RemoveCommand.Execute(null);
            }, height: 600);
            Shot("umbenennen", s =>
            {
                s.Lamps(SampleLamps);
                s.Main.Lamps[0].RenameCommand.Execute(null);
            }, height: 600);
            Shot("meldung", s =>
            {
                s.Lamps(SampleLamps);
                s.View.ShowToast("Die Lampe antwortet gerade nicht. Ist sie eingeschaltet?", false);
            }, height: 600);
            Shot("menue", s =>
            {
                s.Lamps(SampleLamps);
                s.Hub.Raise("""{"t":"hello","autostart":true,"hint":false}""");
                var menu = (ContextMenu)((Button)FindByName(s.View, "Menü")!).ContextMenu;
                menu.DataContext = s.Main;
                s.Replace = menu;
            }, width: 260, height: 260);
            return failures == 0;
        }

        private static readonly string[] SampleLamps =
        {
            Lamp("1", "Wohnzimmer", on: true, mode: "white", brightness: 70, temperature: 30),
            Lamp("2", "Leselampe", on: true, mode: "colour", brightness: 55, hue: 210, sat: 80),
            Lamp("3", "Flur", on: false),
            Lamp("4", "Küche", on: true, status: "connecting", mode: "white", brightness: 100, temperature: 60),
            Lamp("5", "Balkon", status: "offline"),
        };

        private static string Lamp(string id, string name, bool on = false, string status = "online", string mode = "white",
            int brightness = 50, int temperature = 50, int hue = 0, int sat = 100) =>
            Json.Write(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["name"] = name,
                ["status"] = status,
                ["online"] = status != "offline",
                ["on"] = on,
                ["mode"] = mode,
                ["brightness"] = brightness,
                ["temperature"] = temperature,
                ["color"] = new Dictionary<string, object?> { ["h"] = hue, ["s"] = sat },
                ["supports"] = new Dictionary<string, object?> { ["brightness"] = true, ["temperature"] = true, ["color"] = true },
            });

        private static void Save(MainView view, double width, double? height, string file)
        {
            FrameworkElement root = view;
            if (view.Tag is FrameworkElement replacement) root = replacement;
            var size = new Size(width, height ?? double.PositiveInfinity);
            root.Measure(size);
            var final = new Size(width, height ?? root.DesiredSize.Height);
            root.Arrange(new Rect(final));
            root.UpdateLayout();
            DoEvents();
            root.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(final.Width), (int)Math.Ceiling(final.Height), 96, 96, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var dc = background.RenderOpen())
                dc.DrawRectangle((Brush)Application.Current.Resources["Bg"], null, new Rect(final));
            bitmap.Render(background);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(file);
            encoder.Save(stream);
        }

        private static void DoEvents()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        private static FrameworkElement? FindByName(DependencyObject root, string automationName)
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement element && System.Windows.Automation.AutomationProperties.GetName(element) == automationName)
                    return element;
                var inner = FindByName(child, automationName);
                if (inner != null) return inner;
            }
            return null;
        }

        /// <summary>A window with made-up lamps and a Licht that answers what the scene says.</summary>
        private sealed class Scene
        {
            public Scene()
            {
                View = new MainView();
                Ui = new RecordingUi(new WindowUi(View, () => { }, () => { }, () => { }));
                Main = new MainViewModel(Hub, Ui, SystemClock.Instance);
                View.DataContext = Main;
                Main.Start();
                View.Measure(new Size(Width, 800));
                View.Arrange(new Rect(0, 0, Width, 800));
            }

            public DemoHub Hub { get; } = new DemoHub();
            public MainView View { get; }
            public RecordingUi Ui { get; }
            public MainViewModel Main { get; }

            /// <summary>Something else to draw instead of the window (a menu).</summary>
            public FrameworkElement? Replace
            {
                set => View.Tag = value;
            }

            public void Lamps(params string[] lights) => Hub.Raise("{\"t\":\"lights\",\"lights\":[" + string.Join(",", lights) + "]}");
        }

        /// <summary>The window's real dialogs; remembers the add dialog so a scene can fill it in.</summary>
        private sealed class RecordingUi : IUi
        {
            private readonly WindowUi _ui;

            public RecordingUi(WindowUi ui) => _ui = ui;

            public AddViewModel? Add { get; private set; }

            public Task<string?> AskText(string title, string label, string value, string okLabel) => _ui.AskText(title, label, value, okLabel);
            public Task<bool> Confirm(string title, string text, string okLabel, bool danger) => _ui.Confirm(title, text, okLabel, danger);
            public Task Notice(string title, string text) => _ui.Notice(title, text);
            public void Toast(string text, bool ok = false) => _ui.Toast(text, ok);

            public Task ShowAdd(AddViewModel add)
            {
                Add = add;
                return _ui.ShowAdd(add);
            }

            public Task ShowShare(ShareViewModel share) => _ui.ShowShare(share);
            public Task ShowFatal(string title, string text) => _ui.ShowFatal(title, text);
            public void CopyToClipboard(string text) { }
            public void BringToFront() { }
            public void Quit() { }
        }

        /// <summary>Licht, made up: answers calls with what the scene set.</summary>
        private sealed class DemoHub : IHub
        {
            private readonly Dictionary<string, Func<object?[], object?>> _answers = new Dictionary<string, Func<object?[], object?>>();

            public event Action<IDictionary<string, object?>>? Message;

            public void Answer(string method, Func<object?[], object?> answer) => _answers[method] = answer;

            public Task<object?> Call(string method, params object?[] args)
            {
                if (!_answers.TryGetValue(method, out var answer)) return new TaskCompletionSource<object?>().Task; // never answers
                try
                {
                    return Task.FromResult(answer(args));
                }
                catch (Exception e)
                {
                    var failed = new TaskCompletionSource<object?>();
                    failed.SetException(e);
                    return failed.Task;
                }
            }

            public void Send(IDictionary<string, object?> message) { }

            public void Raise(string json) => Message?.Invoke((IDictionary<string, object?>)Json.Parse(json)!);
        }
    }
}
