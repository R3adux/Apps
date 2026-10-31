using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Licht.Fenster
{
    /// <summary>
    /// "Lampe hinzufügen": a code from a lamp, or the text of a share QR code from another Licht (several lamps
    /// at once). Adding goes on when the dialog is closed; the result then appears as a message.
    /// </summary>
    public sealed class AddViewModel : ObservableObject
    {
        private readonly IHub _hub;
        private readonly IUi _ui;
        private readonly MainViewModel _main;
        private string _code = "";
        private string _ip = "";
        private bool _busy;
        private bool _searching;
        private string? _progress;
        private string? _error;
        private string? _searchText;
        private bool _open = true;

        internal AddViewModel(IHub hub, IUi ui, MainViewModel main)
        {
            _hub = hub;
            _ui = ui;
            _main = main;
            SubmitCommand = Command.Async(Submit, () => !Busy && Code.Trim().Length > 0);
            SearchCommand = Command.Async(Search, () => !_searching);
            Found.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowSearchResult));
        }

        public string Code
        {
            get => _code;
            set
            {
                if (Set(ref _code, value ?? "")) ((Command)SubmitCommand).Refresh();
            }
        }

        /// <summary>Address of the lamp (optional): helps when the network search does not find it.</summary>
        public string Ip
        {
            get => _ip;
            set => Set(ref _ip, value ?? "");
        }

        public bool Busy
        {
            get => _busy;
            private set
            {
                if (Set(ref _busy, value)) ((Command)SubmitCommand).Refresh();
            }
        }

        public string? ProgressText
        {
            get => _progress;
            private set => Set(ref _progress, value);
        }

        public string? Error
        {
            get => _error;
            private set => Set(ref _error, value);
        }

        public bool Searching
        {
            get => _searching;
            private set
            {
                if (!Set(ref _searching, value)) return;
                ((Command)SearchCommand).Refresh();
                OnPropertyChanged(nameof(ShowSearchResult));
            }
        }

        public string? SearchText
        {
            get => _searchText;
            private set
            {
                if (Set(ref _searchText, value)) OnPropertyChanged(nameof(ShowSearchResult));
            }
        }

        /// <summary>The box with the result of the network search is shown.</summary>
        public bool ShowSearchResult => Searching || SearchText != null || Found.Count > 0;

        public ObservableCollection<FoundDevice> Found { get; } = new ObservableCollection<FoundDevice>();

        public ICommand SubmitCommand { get; }
        public ICommand SearchCommand { get; }

        /// <summary>Raised when the dialog should close (lamps added).</summary>
        public event Action? CloseRequested;

        /// <summary>The dialog was closed (adding may go on).</summary>
        public void Closed() => _open = false;

        internal void Progress(bool done, int index, int total)
        {
            if (done || total <= 1) return;
            ProgressText = $"Lampe {index + 1} von {total} wird hinzugefügt …";
        }

        private async Task Submit()
        {
            Error = null;
            Busy = true;
            ProgressText = LooksLikeShareCode(Code) ? "Lampen werden hinzugefügt …" : "Suche Lampe … das kann bis zu zwei Minuten dauern.";
            try
            {
                var args = new Dictionary<string, object?> { ["code"] = Code.Trim() };
                if (Ip.Trim().Length > 0) args["ip"] = Ip.Trim();
                var result = await _hub.Call("add", args) as IDictionary<string, object?>;
                var added = result?.Arr("added")?.Count ?? 0;
                var present = result?.Int("present") ?? 0;
                var failed = result?.Arr("failed")?.Count ?? 0;
                if (result?.Arr("lights") is { } lights)
                    _main.Merge(lights.OfType<IDictionary<string, object?>>().Select(Light.FromJson).ToList());
                var summary = Summary(added, present, failed);
                if (failed > 0) await _ui.Notice("Nicht alle Lampen hinzugefügt", summary
                    + " Die übrigen waren nicht erreichbar oder ihr Code ist abgelaufen (15 Minuten). Auf dem anderen Gerät neu teilen und noch einmal scannen.");
                else _ui.Toast(summary, ok: true);
                CloseRequested?.Invoke();
            }
            catch (HubException e)
            {
                if (_open) Error = e.Message;
                else _ui.Toast(e.Message);
            }
            finally
            {
                Busy = false;
                ProgressText = null;
                _main.AddFinished(this);
            }
        }

        internal static string Summary(int added, int present, int failed)
        {
            if (added == 0 && failed == 0 && present > 0)
                return present == 1 ? "Diese Lampe ist schon in Licht." : $"Alle {present} Lampen sind schon in Licht.";
            var parts = new List<string>();
            if (added > 0) parts.Add(added == 1 ? "1 Lampe hinzugefügt" : $"{added} Lampen hinzugefügt");
            if (present > 0) parts.Add(present == 1 ? "1 war schon da" : $"{present} waren schon da");
            if (failed > 0) parts.Add(failed == 1 ? "1 hat nicht geklappt" : $"{failed} haben nicht geklappt");
            return parts.Count == 0 ? "Keine Lampe hinzugefügt." : string.Join(", ", parts) + ".";
        }

        internal static bool LooksLikeShareCode(string code) => code.Trim().StartsWith("MT:", StringComparison.OrdinalIgnoreCase);

        private async Task Search()
        {
            Searching = true;
            SearchText = "Suche … (einige Sekunden)";
            Found.Clear();
            try
            {
                var result = await _hub.Call("discover", Code.Trim().Length > 0 ? Code.Trim() : null) as IList<object?>;
                foreach (var device in (result ?? new List<object?>()).OfType<IDictionary<string, object?>>())
                    Found.Add(FoundDevice.FromJson(device));
                SearchText = Found.Count == 0
                    ? "Keine Lampe bereit zum Hinzufügen gefunden. Ist sie im Kopplungsmodus? Hilft das nicht: unter „Erweitert“ die IP-Adresse angeben."
                    : null;
            }
            catch (HubException e)
            {
                SearchText = e.Message;
            }
            finally
            {
                Searching = false;
            }
        }
    }

    /// <summary>A lamp found by the network search.</summary>
    public sealed class FoundDevice
    {
        public string Name { get; set; } = "";
        public string Detail { get; set; } = "";
        public bool MatchesCode { get; set; }

        internal static FoundDevice FromJson(IDictionary<string, object?> o)
        {
            var mode = o.Int("mode");
            var addresses = o.Arr("addresses")?.OfType<string>().ToList() ?? new List<string>();
            var how = mode == 1 ? "neu – Code von der Lampe" : mode == 2 ? "geteilt – Code aus der anderen App" : "bereit";
            var matches = o.TryGetValue("matchesCode", out var m) && m is bool b && b;
            return new FoundDevice
            {
                Name = o.Str("name") ?? "Matter-Gerät",
                Detail = how + (addresses.Count > 0 ? $" · {addresses[0]}" : "") + (matches ? " · passt zum Code" : ""),
                MatchesCode = matches,
            };
        }
    }
}
