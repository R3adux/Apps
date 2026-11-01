using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Licht.Fenster
{
    /// <summary>
    /// The Licht window: all lamps, "Alle aus/an", and the menu (add, share, start with Windows, quit).
    /// All members on the UI thread.
    /// </summary>
    public sealed class MainViewModel : ObservableObject
    {
        private readonly IHub _hub;
        private readonly IUi _ui;
        private readonly IClock _clock;
        private readonly LightsCache? _cache;
        private bool _loaded;
        private bool _autostart;
        private AddViewModel? _adding;
        private bool _saveScheduled;

        internal static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

        public MainViewModel(IHub hub, IUi ui, IClock clock, LightsCache? cache = null)
        {
            _hub = hub;
            _ui = ui;
            _clock = clock;
            _cache = cache;
            AllToggleCommand = new Command(AllToggle, () => Lamps.Any(l => !l.IsOffline));
            AddCommand = Command.Async(Add);
            ShareAllCommand = Command.Async(() => ShareLamps(null, "Alle Lampen teilen"), () => Lamps.Count > 0);
            AutostartCommand = Command.Async(ToggleAutostart);
            QuitCommand = Command.Async(AskQuit);
            hub.Message += OnMessage;

            // The lamps as last seen, right away – Licht itself is still starting.
            foreach (var light in cache?.Load() ?? new List<Light>()) Lamps.Add(new LampViewModel(this, hub, clock, light));
            Refresh();
        }

        public ObservableCollection<LampViewModel> Lamps { get; } = new ObservableCollection<LampViewModel>();

        /// <summary>Licht has reported its lamps (until then: the cached ones).</summary>
        public bool Loaded
        {
            get => _loaded;
            private set => Set(ref _loaded, value);
        }

        public bool IsEmpty => _loaded && Lamps.Count == 0;
        public bool IsLoading => !_loaded && Lamps.Count == 0;
        public string AllToggleText => Lamps.Any(l => l.IsOn && !l.IsOffline) ? "Alle aus" : "Alle an";

        public bool Autostart
        {
            get => _autostart;
            private set => Set(ref _autostart, value);
        }

        /// <summary>First start of Licht: tell the user once that closing the window keeps Licht running.</summary>
        public bool ShowHint { get; private set; }

        public ICommand AllToggleCommand { get; }
        public ICommand AddCommand { get; }
        public ICommand ShareAllCommand { get; }
        public ICommand AutostartCommand { get; }
        public ICommand QuitCommand { get; }

        /// <summary>Tells Licht the window program is up (it answers with hello and the lamps).</summary>
        public void Start() => _hub.Send(new Dictionary<string, object?> { ["t"] = "ready" });

        private void OnMessage(IDictionary<string, object?> message)
        {
            switch (message.Str("t"))
            {
                case "hello":
                    Autostart = message.Bool("autostart");
                    ShowHint = message.Bool("hint");
                    break;
                case "lights":
                    Merge((message.Arr("lights") ?? new List<object?>())
                        .OfType<IDictionary<string, object?>>().Select(Light.FromJson).ToList());
                    break;
                case "light":
                    if (message.Obj("light") is { } one) UpdateOne(Light.FromJson(one));
                    break;
                case "adding":
                    _adding?.Progress(message.Bool("done"), message.Int("index"), message.Int("total"));
                    break;
                case "show":
                    _ui.BringToFront();
                    break;
                case "message":
                    _ = _ui.Notice(message.Str("title") ?? "Licht", message.Str("text") ?? "");
                    break;
                case "fatal":
                    _ = Fatal(message.Str("title") ?? "Licht", message.Str("text") ?? "");
                    break;
                case "quit":
                    _ui.Quit();
                    break;
            }
        }

        private async Task Fatal(string title, string text)
        {
            await _ui.ShowFatal(title, text);
            _hub.Send(new Dictionary<string, object?> { ["t"] = "quit" });
            _ui.Quit();
        }

        /// <summary>The lamps from Licht, in its order. A lamp the user is adjusting keeps its controls.</summary>
        internal void Merge(IList<Light> lights)
        {
            var ids = new HashSet<string>(lights.Select(l => l.Id));
            for (var i = Lamps.Count - 1; i >= 0; i--) if (!ids.Contains(Lamps[i].Id)) Lamps.RemoveAt(i);
            for (var i = 0; i < lights.Count; i++)
            {
                var existing = Lamps.FirstOrDefault(l => l.Id == lights[i].Id);
                if (existing == null) Lamps.Insert(i, new LampViewModel(this, _hub, _clock, lights[i]));
                else
                {
                    existing.Apply(lights[i]);
                    var at = Lamps.IndexOf(existing);
                    if (at != i) Lamps.Move(at, i);
                }
            }
            Loaded = true;
            Refresh();
            Save();
        }

        // While the user adjusts the lamp, Apply keeps the controls; reachability and name are shown anyway.
        private void UpdateOne(Light light)
        {
            var lamp = Lamps.FirstOrDefault(l => l.Id == light.Id);
            if (lamp == null) return;
            lamp.Apply(light);
            Refresh();
            Save();
        }

        /// <summary>The lamps from Licht again (after a failure: show the real state).</summary>
        internal async Task Reload()
        {
            try
            {
                var result = await _hub.Call("lights");
                if (result is IList<object?> list) Merge(list.OfType<IDictionary<string, object?>>().Select(Light.FromJson).ToList());
            }
            catch (HubException)
            {
                // Licht is not running any more: the window program ends anyway
            }
        }

        internal void LampChanged()
        {
            Refresh();
            Save();
        }

        internal void SendFailed(string message, bool showRealState)
        {
            _ui.Toast(message);
            if (showRealState) _ = Reload();
        }

        private void Refresh()
        {
            OnPropertiesChanged(nameof(IsEmpty), nameof(IsLoading), nameof(AllToggleText));
            ((Command)AllToggleCommand).Refresh();
            ((Command)ShareAllCommand).Refresh();
        }

        // Written a moment later, once per burst of changes (a slider drag is many changes).
        private void Save()
        {
            if (!_loaded || _cache == null || _saveScheduled) return;
            _saveScheduled = true;
            _ = SaveSoon();
        }

        private async Task SaveSoon()
        {
            await _clock.Delay(SaveDelay);
            _saveScheduled = false;
            _cache?.Save(Lamps.Select(l => l.Snapshot).ToList());
        }

        private void AllToggle()
        {
            var turnOn = !Lamps.Any(l => l.IsOn && !l.IsOffline);
            foreach (var lamp in Lamps.Where(l => !l.IsOffline && l.IsOn != turnOn).ToList())
                lamp.Change(new LampChange { On = turnOn });
        }

        private async Task Add()
        {
            var add = new AddViewModel(_hub, _ui, this);
            _adding = add;
            try
            {
                await _ui.ShowAdd(add);
            }
            finally
            {
                if (_adding == add && !add.Busy) _adding = null;
            }
        }

        internal void AddFinished(AddViewModel add)
        {
            if (_adding == add) _adding = null;
        }

        internal Task Share(LampViewModel lamp) => ShareLamps(new[] { lamp.Id }, $"„{lamp.Name}“ teilen");

        private async Task ShareLamps(string[]? ids, string title)
        {
            var share = new ShareViewModel(title);
            var shown = _ui.ShowShare(share);
            try
            {
                var result = ids == null ? await _hub.Call("share") : await _hub.Call("share", (object)ids);
                if (result is IDictionary<string, object?> info) share.Ready(ShareInfo.FromJson(info));
            }
            catch (HubException e)
            {
                // A lamp that takes no further app: offer the way out right there.
                var lamp = ids?.Length == 1 && e.Status == 409 ? Lamps.FirstOrDefault(l => l.Id == ids[0]) : null;
                share.Failed(e.Message, lamp == null ? null : new Action(() => _ = ShowConnections(lamp)));
            }
            await shown;
        }

        /// <summary>"Verbundene Apps" of a lamp.</summary>
        internal async Task ShowConnections(LampViewModel lamp)
        {
            var connections = new ConnectionsViewModel(_hub, _ui, lamp.Id, lamp.Name);
            var shown = _ui.ShowConnections(connections);
            await connections.Load();
            await shown;
        }

        internal async Task Rename(LampViewModel lamp)
        {
            var name = await _ui.AskText("Umbenennen", "Neuer Name (höchstens 32 Zeichen)", lamp.Name, "Speichern");
            name = name?.Trim();
            if (string.IsNullOrEmpty(name) || name == lamp.Name) return;
            if (name!.Length > 32) name = name.Substring(0, 32);
            try
            {
                await _hub.Call("rename", lamp.Id, name);
                lamp.Rename(name);
                Save();
            }
            catch (HubException e)
            {
                _ui.Toast(e.Message);
            }
        }

        internal async Task Remove(LampViewModel lamp)
        {
            var sure = await _ui.Confirm($"„{lamp.Name}“ entfernen?",
                "Licht vergisst die Lampe. Andere Apps, mit denen sie geteilt ist, funktionieren weiter.", "Entfernen", danger: true);
            if (!sure) return;
            _ui.Toast($"„{lamp.Name}“ wird entfernt …", ok: true);
            try
            {
                var result = await _hub.Call("remove", lamp.Id) as IDictionary<string, object?>;
                Lamps.Remove(lamp);
                Refresh();
                Save();
                _ui.Toast($"„{lamp.Name}“ wurde entfernt.", ok: true);
                if (result?.Bool("localOnly") == true)
                {
                    await _ui.Notice("Lampe war nicht erreichbar",
                        $"Licht hat „{lamp.Name}“ vergessen. Die Lampe selbst kennt Licht aber noch, weil sie gerade nicht antwortet. "
                        + "Wenn du sie nicht mehr mit Licht nutzen willst: in der anderen App entfernen oder die Lampe zurücksetzen "
                        + "(5× aus- und einschalten).");
                }
            }
            catch (HubException e)
            {
                _ui.Toast(e.Message);
            }
        }

        private async Task ToggleAutostart()
        {
            try
            {
                var result = await _hub.Call("autostart", !Autostart);
                Autostart = result is bool on && on;
                _ui.Toast(Autostart ? "Licht startet jetzt automatisch mit Windows." : "Licht startet nicht mehr mit Windows.", ok: true);
            }
            catch (HubException e)
            {
                _ui.Toast(e.Message);
            }
        }

        private async Task AskQuit()
        {
            var sure = await _ui.Confirm("Licht beenden?",
                "Die Lampen lassen sich dann am PC nicht mehr steuern, bis du Licht wieder startest.", "Beenden", danger: true);
            if (sure) Quit();
        }

        /// <summary>End Licht (also from the tray menu).</summary>
        public void Quit()
        {
            _hub.Send(new Dictionary<string, object?> { ["t"] = "quit" });
        }

        /// <summary>Set autostart from the tray menu.</summary>
        public Task SetAutostart(bool on) => on == Autostart ? Task.CompletedTask : ToggleAutostart();
    }
}
