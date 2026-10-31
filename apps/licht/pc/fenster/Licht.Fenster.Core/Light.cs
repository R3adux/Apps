using System;
using System.Collections.Generic;

namespace Licht.Fenster
{
    public enum LampStatus { Online, Connecting, Offline }

    public enum LampMode { White, Colour }

    /// <summary>A lamp as Licht reports it (see core/model.js): the same fields as in the phone app.</summary>
    public sealed class Light
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public LampStatus Status { get; set; } = LampStatus.Online;
        public bool On { get; set; }
        public LampMode Mode { get; set; } = LampMode.White;
        /// <summary>1–100 %</summary>
        public int Brightness { get; set; } = 100;
        /// <summary>0 = warm white … 100 = cold white</summary>
        public int Temperature { get; set; } = 50;
        /// <summary>0–360°</summary>
        public int Hue { get; set; }
        /// <summary>0–100 %</summary>
        public int Saturation { get; set; } = 100;
        public bool SupportsBrightness { get; set; }
        public bool SupportsTemperature { get; set; }
        public bool SupportsColor { get; set; }

        public static Light FromJson(IDictionary<string, object?> o)
        {
            var color = o.Obj("color");
            var supports = o.Obj("supports");
            var online = o.Bool("online", true);
            return new Light
            {
                Id = o.Str("id") ?? "",
                Name = o.Str("name") ?? "Lampe",
                Status = (o.Str("status") ?? (online ? "online" : "offline")) switch
                {
                    "connecting" => LampStatus.Connecting,
                    "offline" => LampStatus.Offline,
                    _ => LampStatus.Online,
                },
                On = o.Bool("on"),
                Mode = o.Str("mode") == "colour" ? LampMode.Colour : LampMode.White,
                Brightness = Clamp(o.Int("brightness", 100), 1, 100),
                Temperature = Clamp(o.Int("temperature", 50), 0, 100),
                Hue = color == null ? 0 : Clamp(color.Int("h"), 0, 360),
                Saturation = color == null ? 100 : Clamp(color.Int("s", 100), 0, 100),
                SupportsBrightness = supports?.Bool("brightness") ?? false,
                SupportsTemperature = supports?.Bool("temperature") ?? false,
                SupportsColor = supports?.Bool("color") ?? false,
            };
        }

        public Dictionary<string, object?> ToJson() => new Dictionary<string, object?>
        {
            ["id"] = Id,
            ["name"] = Name,
            ["status"] = Status switch { LampStatus.Connecting => "connecting", LampStatus.Offline => "offline", _ => "online" },
            ["online"] = Status == LampStatus.Online,
            ["on"] = On,
            ["mode"] = Mode == LampMode.Colour ? "colour" : "white",
            ["brightness"] = Brightness,
            ["temperature"] = Temperature,
            ["color"] = new Dictionary<string, object?> { ["h"] = Hue, ["s"] = Saturation },
            ["supports"] = new Dictionary<string, object?>
            {
                ["brightness"] = SupportsBrightness,
                ["temperature"] = SupportsTemperature,
                ["color"] = SupportsColor,
            },
        };

        public Light Clone() => (Light)MemberwiseClone();

        private static int Clamp(int value, int min, int max) => Math.Min(max, Math.Max(min, value));
    }

    /// <summary>An RGB colour (0–255 each) – without a dependency on WPF.</summary>
    public readonly struct Rgb : IEquatable<Rgb>
    {
        public Rgb(byte r, byte g, byte b)
        {
            R = r;
            G = g;
            B = b;
        }

        public byte R { get; }
        public byte G { get; }
        public byte B { get; }

        public bool Equals(Rgb other) => R == other.R && G == other.G && B == other.B;
        public override bool Equals(object? obj) => obj is Rgb other && Equals(other);
        public override int GetHashCode() => (R << 16) | (G << 8) | B;
        public override string ToString() => $"#{R:x2}{G:x2}{B:x2}";
    }

    /// <summary>The colour a lamp shines in, for showing it on screen (the same as web/color.js).</summary>
    public static class LampColors
    {
        private static readonly double[] Warm = { 255, 166, 77 };
        private static readonly double[] Neutral = { 255, 241, 220 };
        private static readonly double[] Cold = { 207, 227, 255 };

        /// <param name="hue">0–360</param>
        /// <param name="saturation">0–1</param>
        public static Rgb FromHsv(double hue, double saturation, double value = 1)
        {
            byte F(double n)
            {
                var k = (n + hue / 60) % 6;
                var c = value - value * saturation * Math.Max(0, Math.Min(k, Math.Min(4 - k, 1)));
                return (byte)Math.Floor(c * 255 + 0.5);
            }
            return new Rgb(F(5), F(3), F(1));
        }

        /// <param name="temperature">0 = warm white, 100 = cold white</param>
        public static Rgb FromTemperature(double temperature)
        {
            var t = Math.Min(100, Math.Max(0, temperature)) / 50;
            var (from, to, k) = t <= 1 ? (Warm, Neutral, t) : (Neutral, Cold, t - 1);
            byte Mix(int i) => (byte)Math.Floor(from[i] + (to[i] - from[i]) * k + 0.5);
            return new Rgb(Mix(0), Mix(1), Mix(2));
        }

        /// <summary>Colour lamps without white tones always show their colour.</summary>
        public static LampMode ShownMode(Light light) =>
            light.SupportsColor && !light.SupportsTemperature ? LampMode.Colour : light.Mode;

        public static Rgb Of(Light light) => ShownMode(light) == LampMode.Colour
            ? FromHsv(light.Hue, light.Saturation / 100.0)
            : FromTemperature(light.Temperature);
    }

    /// <summary>A preset colour (the round buttons on a lamp card).</summary>
    public sealed class Preset
    {
        private Preset(string label, LampMode mode, int temperature, int hue, int saturation)
        {
            Label = label;
            Mode = mode;
            Temperature = temperature;
            Hue = hue;
            Saturation = saturation;
            Color = mode == LampMode.Colour ? LampColors.FromHsv(hue, saturation / 100.0) : LampColors.FromTemperature(temperature);
        }

        public string Label { get; }
        public LampMode Mode { get; }
        public int Temperature { get; }
        public int Hue { get; }
        public int Saturation { get; }
        public Rgb Color { get; }

        public static IReadOnlyList<Preset> All { get; } = new[]
        {
            new Preset("Warmweiß", LampMode.White, 0, 0, 0),
            new Preset("Neutralweiß", LampMode.White, 50, 0, 0),
            new Preset("Kaltweiß", LampMode.White, 100, 0, 0),
            new Preset("Rot", LampMode.Colour, 0, 0, 100),
            new Preset("Orange", LampMode.Colour, 0, 28, 100),
            new Preset("Gelb", LampMode.Colour, 0, 52, 100),
            new Preset("Grün", LampMode.Colour, 0, 120, 100),
            new Preset("Türkis", LampMode.Colour, 0, 180, 100),
            new Preset("Blau", LampMode.Colour, 0, 230, 100),
            new Preset("Lila", LampMode.Colour, 0, 275, 100),
            new Preset("Pink", LampMode.Colour, 0, 320, 85),
        };
    }
}
