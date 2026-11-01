using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Licht.Fenster
{
    /// <summary>
    /// "Verbundene Apps" of one lamp: the apps that may control it (Matter "fabrics"). A lamp takes only a few
    /// (the SMART+ A60: 5); a full lamp cannot be added anywhere else. Old entries – e.g. of an earlier
    /// installation of the phone app – can be removed here. Licht itself is shown, but not removable here.
    /// </summary>
    public sealed class ConnectionsViewModel : ObservableObject
    {
        private readonly IHub _hub;
        private readonly IUi _ui;
        private readonly string _lampId;
        private bool _loading = true;
        private string? _error;
        private string? _busyText;
        private int _supported;

        internal ConnectionsViewModel(IHub hub, IUi ui, string lampId, string lampName)
        {
            _hub = hub;
            _ui = ui;
            _lampId = lampId;
            LampName = lampName;
            RemoveCommand = Command.Async(p => p is ConnectionItem item ? Remove(item) : Task.CompletedTask);
        }

        public string LampName { get; }
        public string Title => $"Verbundene Apps – „{LampName}“";

        public ObservableCollection<ConnectionItem> Items { get; } = new ObservableCollection<ConnectionItem>();

        public bool Loading
        {
            get => _loading;
            private set => Set(ref _loading, value);
        }

        public string? Error
        {
            get => _error;
            private set => Set(ref _error, value);
        }

        /// <summary>Shown while an app is being removed (that can take a while).</summary>
        public string? BusyText
        {
            get => _busyText;
            private set
            {
                if (Set(ref _busyText, value)) OnPropertyChanged(nameof(Busy));
            }
        }

        public bool Busy => _busyText != null;

        /// <summary>"3 von 5 Plätzen belegt" – and what that means.</summary>
        public string SummaryText => Items.Count == 0 ? ""
            : $"{Items.Count} von {_supported} Plätzen belegt."
              + (IsFull ? " Die Lampe ist voll: Ein weiteres Gerät kann sie erst aufnehmen, wenn hier eine nicht mehr benutzte App entfernt wird." : "");

        public bool IsFull => _supported > 0 && Items.Count >= _supported;

        public ICommand RemoveCommand { get; }

        /// <summary>Reads the list from the lamp.</summary>
        public async Task Load()
        {
            Loading = true;
            Error = null;
            try
            {
                Show(await _hub.Call("connections", _lampId) as IDictionary<string, object?>);
            }
            catch (HubException e)
            {
                Error = e.Message;
            }
            finally
            {
                Loading = false;
            }
        }

        private async Task Remove(ConnectionItem item)
        {
            if (item.Own || Busy) return;
            var sure = await _ui.Confirm($"„{item.Name}“ entfernen?",
                $"Diese App kann „{LampName}“ danach nicht mehr steuern. Benutzt du sie noch, musst du die Lampe dort neu hinzufügen.",
                "Entfernen", danger: true);
            if (!sure) return;
            Error = null;
            BusyText = $"„{item.Name}“ wird entfernt … das kann bis zu einer halben Minute dauern.";
            try
            {
                Show(await _hub.Call("removeConnection", _lampId, item.Index) as IDictionary<string, object?>);
                _ui.Toast($"„{item.Name}“ wurde von „{LampName}“ entfernt.", ok: true);
            }
            catch (HubException e)
            {
                Error = e.Message;
            }
            finally
            {
                BusyText = null;
            }
        }

        private void Show(IDictionary<string, object?>? result)
        {
            if (result == null) return;
            _supported = result.Int("supported");
            Items.Clear();
            foreach (var entry in (result.Arr("entries") ?? new List<object?>()).OfType<IDictionary<string, object?>>())
                Items.Add(ConnectionItem.FromJson(entry));
            OnPropertiesChanged(nameof(SummaryText), nameof(IsFull));
        }
    }

    /// <summary>One app connected to a lamp.</summary>
    public sealed class ConnectionItem
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public bool Own { get; set; }

        /// <summary>Second line: what this entry is.</summary>
        public string Detail => Own ? "Dieses Licht – hier wird nichts entfernt" : "kann die Lampe steuern";

        internal static ConnectionItem FromJson(IDictionary<string, object?> o) => new ConnectionItem
        {
            Index = o.Int("index"),
            Name = o.Str("name") ?? "App",
            Own = o.Bool("own"),
        };
    }
}
