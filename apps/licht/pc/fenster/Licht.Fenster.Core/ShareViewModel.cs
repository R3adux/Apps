using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace Licht.Fenster
{
    /// <summary>What Licht answers when lamps are shared (see core/hub.js share()).</summary>
    public sealed class ShareInfo
    {
        public string Code { get; set; } = "";
        public string? Manual { get; set; }
        public IReadOnlyList<string> Lamps { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> Skipped { get; set; } = Array.Empty<string>();
        /// <summary>Lamps left out because they take no further app (see ConnectionsViewModel).</summary>
        public IReadOnlyList<string> Full { get; set; } = Array.Empty<string>();
        public int ValidMinutes { get; set; } = 15;

        public static ShareInfo FromJson(IDictionary<string, object?> o) => new ShareInfo
        {
            Code = o.Str("code") ?? "",
            Manual = o.Str("manual"),
            Lamps = o.Arr("lamps")?.OfType<string>().ToList() ?? new List<string>(),
            Skipped = o.Arr("skipped")?.OfType<string>().ToList() ?? new List<string>(),
            Full = o.Arr("full")?.OfType<string>().ToList() ?? new List<string>(),
            ValidMinutes = o.Int("validMinutes", 15),
        };
    }

    /// <summary>
    /// The share dialog: a QR code another Licht scans to add the lamps – no setting up again.
    /// First "being prepared" (the lamps open for sharing), then the code, or what went wrong.
    /// </summary>
    public sealed class ShareViewModel : ObservableObject
    {
        private ShareInfo? _info;
        private string? _error;

        public ShareViewModel(string title)
        {
            Title = title;
            CopyCommand = new Command(Copy, () => _info != null);
            ConnectionsCommand = new Command(OpenConnections, () => _openConnections != null);
        }

        private Action? _openConnections;

        /// <summary>Sharing failed because the lamp is full: the button that leads to its connected apps.</summary>
        public bool CanOpenConnections => _openConnections != null;

        public ICommand ConnectionsCommand { get; }

        /// <summary>Raised when the dialog should close (it hands over to another dialog).</summary>
        public event Action? CloseRequested;

        private void OpenConnections()
        {
            var open = _openConnections;
            if (open == null) return;
            CloseRequested?.Invoke();
            open();
        }

        public string Title { get; }
        public bool Preparing => _info == null && _error == null;
        public bool HasCode => _info != null;
        public string? Error => _error;

        /// <summary>The QR code: true = dark module. Null until ready.</summary>
        public bool[,]? Matrix { get; private set; }

        public string Code => _info?.Code ?? "";

        /// <summary>The 11 digits for other Matter apps (only when sharing one lamp).</summary>
        public string? ManualCode => _info?.Manual is { Length: 11 } m ? $"{m.Substring(0, 4)}-{m.Substring(4, 3)}-{m.Substring(7)}" : _info?.Manual;

        public string LampsText => _info == null ? "" : _info.Lamps.Count == 1
            ? $"Enthält: {_info.Lamps[0]}"
            : $"Enthält {_info.Lamps.Count} Lampen: {string.Join(", ", _info.Lamps)}";

        public string? SkippedText => _info == null || _info.Skipped.Count == 0 ? null
            : $"Nicht dabei, weil gerade nicht erreichbar: {string.Join(", ", _info.Skipped)}. Später noch einmal teilen.";

        public string? FullText => _info == null || _info.Full.Count == 0 ? null
            : $"Nicht dabei, weil schon mit so vielen Apps verbunden, wie sie kann: {string.Join(", ", _info.Full)}. "
              + "Im Lampen-Menü ⋮ unter „Verbundene Apps“ eine nicht mehr benutzte App entfernen, dann neu teilen.";

        public string ValidText => _info == null ? "" : $"Gültig für {_info.ValidMinutes} Minuten. Jede Lampe lässt sich mit diesem Code einmal hinzufügen.";

        public ICommand CopyCommand { get; }

        /// <summary>Set by the window: puts the code on the clipboard.</summary>
        public Action<string>? CopyToClipboard { get; set; }

        internal void Ready(ShareInfo info)
        {
            _info = info;
            Matrix = Qr.Matrix(info.Code);
            OnPropertiesChanged(nameof(Preparing), nameof(HasCode), nameof(Matrix), nameof(Code), nameof(ManualCode),
                nameof(LampsText), nameof(SkippedText), nameof(FullText), nameof(ValidText));
            ((Command)CopyCommand).Refresh();
        }

        /// <param name="openConnections">Way out when the lamp is full (the "Verbundene Apps" button), or null.</param>
        internal void Failed(string message, Action? openConnections = null)
        {
            _error = message;
            _openConnections = openConnections;
            OnPropertiesChanged(nameof(Preparing), nameof(Error), nameof(CanOpenConnections));
            ((Command)ConnectionsCommand).Refresh();
        }

        private void Copy()
        {
            if (_info != null) CopyToClipboard?.Invoke(_info.Code);
        }
    }
}
