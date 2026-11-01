using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace Licht.Fenster
{
    /// <summary>
    /// One lamp card. Changes are shown at once and sent in the background (see <see cref="LampSender"/>);
    /// while the user is adjusting a lamp, updates from Licht do not move the controls under their fingers.
    /// </summary>
    public sealed class LampViewModel : ObservableObject
    {
        /// <summary>After the last input, updates from Licht wait this long (the lamp reports interim values).</summary>
        public static readonly TimeSpan QuietAfterInput = TimeSpan.FromMilliseconds(1500);

        private readonly MainViewModel _main;
        private readonly IClock _clock;
        private readonly LampSender _sender;
        private Light _light;
        private DateTime _touched = DateTime.MinValue;

        internal LampViewModel(MainViewModel main, IHub hub, IClock clock, Light light)
        {
            _main = main;
            _clock = clock;
            _light = light.Clone();
            _sender = new LampSender(light.Id, hub, clock, OnSendFailed);
            ToggleCommand = new Command(() => Change(new LampChange { On = !_light.On }));
            WhiteCommand = new Command(() => Change(new LampChange { Mode = LampMode.White }));
            ColourCommand = new Command(() => Change(new LampChange { Mode = LampMode.Colour }));
            PresetCommand = new Command(p => { if (p is Preset preset) ApplyPreset(preset); });
            RenameCommand = Command.Async(() => _main.Rename(this));
            ShareCommand = Command.Async(() => _main.Share(this));
            RemoveCommand = Command.Async(() => _main.Remove(this));
            ConnectionsCommand = Command.Async(() => _main.ShowConnections(this));
            UpdatePresets();
        }

        public string Id => _light.Id;
        public string Name => _light.Name;
        public LampStatus Status => _light.Status;
        public bool IsOn => _light.On;
        public bool IsOffline => _light.Status == LampStatus.Offline;
        public bool IsConnecting => _light.Status == LampStatus.Connecting;
        /// <summary>Lit on screen: switched on and reachable.</summary>
        public bool IsLit => _light.On && !IsOffline;
        public bool ShowControls => IsLit;
        public LampMode Mode => LampColors.ShownMode(_light);
        public bool IsWhite => Mode == LampMode.White;
        public bool IsColour => Mode == LampMode.Colour;
        public bool ShowModeSwitch => _light.SupportsColor && _light.SupportsTemperature;
        public bool ShowBrightness => _light.SupportsBrightness;
        public bool ShowTemperature => IsWhite && _light.SupportsTemperature;
        public bool ShowColour => IsColour && _light.SupportsColor;

        public string StateText
        {
            get
            {
                var onText = _light.On ? $"An · {_light.Brightness} %" : "Aus";
                return _light.Status switch
                {
                    LampStatus.Offline => "Nicht erreichbar",
                    LampStatus.Connecting => $"Verbinde … · {onText}",
                    _ => onText,
                };
            }
        }

        public string ToggleLabel => $"{Name} {(_light.On ? "ausschalten" : "einschalten")}";

        /// <summary>The colour of the light, for the bulb and the glow.</summary>
        public Rgb LampColor => LampColors.Of(_light);

        /// <summary>How strongly the card glows: 0 (off) … about 1 (full brightness).</summary>
        public double Glow => IsLit ? 0.35 + _light.Brightness / 155.0 : 0;

        /// <summary>The fully saturated colour of the current hue (end of the saturation slider).</summary>
        public Rgb HueColor => LampColors.FromHsv(_light.Hue, 1);

        /// <summary>The preset colours this lamp can show (changes only with its abilities).</summary>
        public IReadOnlyList<Preset> Presets { get; private set; } = Array.Empty<Preset>();

        private void UpdatePresets()
        {
            var presets = Preset.All
                .Where(p => p.Mode == LampMode.Colour ? _light.SupportsColor : _light.SupportsTemperature)
                .ToList();
            if (presets.SequenceEqual(Presets)) return;
            Presets = presets;
            OnPropertyChanged(nameof(Presets));
        }

        public int Brightness
        {
            get => _light.Brightness;
            set { if (value != _light.Brightness) Change(new LampChange { Brightness = value }); }
        }

        public int Temperature
        {
            get => _light.Temperature;
            set { if (value != _light.Temperature) Change(new LampChange { Mode = LampMode.White, Temperature = value }); }
        }

        public int Hue
        {
            get => _light.Hue;
            set { if (value != _light.Hue) Change(new LampChange { Mode = LampMode.Colour, Hue = value }); }
        }

        public int Saturation
        {
            get => _light.Saturation;
            set { if (value != _light.Saturation) Change(new LampChange { Mode = LampMode.Colour, Saturation = value }); }
        }

        public ICommand ToggleCommand { get; }
        public ICommand WhiteCommand { get; }
        public ICommand ColourCommand { get; }
        public ICommand PresetCommand { get; }
        public ICommand RenameCommand { get; }
        public ICommand ShareCommand { get; }
        public ICommand RemoveCommand { get; }
        public ICommand ConnectionsCommand { get; }

        /// <summary>The user is adjusting this lamp right now (or a change is still on its way).</summary>
        public bool IsBusy => !_sender.Idle || _clock.UtcNow - _touched < QuietAfterInput;

        internal Light Snapshot => _light.Clone();

        /// <summary>
        /// The lamp as Licht reports it. While the user is busy with it, the controls keep the user's values
        /// (name, reachability and abilities are taken over anyway).
        /// </summary>
        internal void Apply(Light light)
        {
            if (IsBusy)
            {
                var mine = _light;
                light = light.Clone();
                light.On = mine.On;
                light.Mode = mine.Mode;
                light.Brightness = mine.Brightness;
                light.Temperature = mine.Temperature;
                light.Hue = mine.Hue;
                light.Saturation = mine.Saturation;
            }
            _light = light.Clone();
            UpdatePresets();
            NotifyAll();
        }

        internal void ApplyPreset(Preset preset) => Change(preset.Mode == LampMode.White
            ? new LampChange { Mode = LampMode.White, Temperature = preset.Temperature }
            : new LampChange { Mode = LampMode.Colour, Hue = preset.Hue, Saturation = preset.Saturation });

        internal void Rename(string name)
        {
            _light.Name = name;
            NotifyAll();
        }

        /// <summary>Shows a change at once and sends it. Changing the light of a lamp that is off switches it on.</summary>
        internal void Change(LampChange change)
        {
            if (change.On == null && !_light.On) change.On = true;
            if (change.On is bool on) _light.On = on;
            if (change.Mode is LampMode mode) _light.Mode = mode;
            if (change.Brightness is int brightness) _light.Brightness = Math.Min(100, Math.Max(1, brightness));
            if (change.Temperature is int temperature) _light.Temperature = Math.Min(100, Math.Max(0, temperature));
            if (change.Hue is int hue) _light.Hue = Math.Min(360, Math.Max(0, hue));
            if (change.Saturation is int saturation) _light.Saturation = Math.Min(100, Math.Max(0, saturation));
            _touched = _clock.UtcNow;
            NotifyAll();
            _main.LampChanged();

            // Only what changed: dimming is one command to the lamp, not dimming plus re-sending the colour.
            var payload = new Dictionary<string, object?>();
            if (change.On is bool switchOn) payload["on"] = switchOn;
            if (change.Brightness != null) payload["brightness"] = _light.Brightness;
            if (change.Mode != null || change.Temperature != null || change.Hue != null || change.Saturation != null)
            {
                payload["mode"] = _light.Mode == LampMode.Colour ? "colour" : "white";
                if (_light.Mode == LampMode.Colour)
                    payload["color"] = new Dictionary<string, object?> { ["h"] = _light.Hue, ["s"] = _light.Saturation };
                else if (_light.SupportsTemperature) payload["temperature"] = _light.Temperature;
            }
            _sender.Push(payload);
        }

        private void OnSendFailed(string message, bool showRealState)
        {
            // Show the lamp as it really is again – not the change that did not happen (unless the user
            // already tapped again: that change is on its way).
            if (showRealState) _touched = DateTime.MinValue;
            _main.SendFailed(message, showRealState);
        }

        private void NotifyAll() => OnPropertiesChanged(
            nameof(Name), nameof(Status), nameof(IsOn), nameof(IsOffline), nameof(IsConnecting), nameof(IsLit),
            nameof(ShowControls), nameof(Mode), nameof(IsWhite), nameof(IsColour), nameof(ShowModeSwitch),
            nameof(ShowBrightness), nameof(ShowTemperature), nameof(ShowColour), nameof(StateText), nameof(ToggleLabel),
            nameof(LampColor), nameof(Glow), nameof(HueColor), nameof(Brightness), nameof(Temperature),
            nameof(Hue), nameof(Saturation));
    }

    /// <summary>A change to a lamp; only the fields that are set change.</summary>
    public sealed class LampChange
    {
        public bool? On { get; set; }
        public LampMode? Mode { get; set; }
        public int? Brightness { get; set; }
        public int? Temperature { get; set; }
        public int? Hue { get; set; }
        public int? Saturation { get; set; }
    }
}
