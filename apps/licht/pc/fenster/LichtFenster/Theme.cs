using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace LichtFenster
{
    /// <summary>
    /// Light or dark, like Windows ("App-Modus" in the colour settings) – also when it is changed while Licht runs.
    /// The colours are the same as in the phone app (web/style.css); XAML uses them as DynamicResource.
    /// </summary>
    internal static class Theme
    {
        public static bool IsDark { get; private set; } = true;

        /// <summary>Raised on the UI thread after the colours changed.</summary>
        public static event Action? Changed;

        private static readonly (string Key, string Dark, string Light)[] Colours =
        {
            ("Bg", "#101217", "#f3f4f7"),
            ("Surface", "#1a1d24", "#ffffff"),
            ("Surface2", "#242833", "#eceef3"),
            ("Border", "#2e3340", "#dde0e7"),
            ("Text", "#f1f2f5", "#15171c"),
            ("Muted", "#9aa1b0", "#5f6675"),
            ("Accent", "#ffb86b", "#c76a12"),
            ("Danger", "#ff6b6b", "#c62828"),
            ("OnAccent", "#1b1206", "#ffffff"),
            ("Backdrop", "#8c000000", "#66000000"),
            ("Hover", "#14ffffff", "#0f000000"),
            ("Pressed", "#24ffffff", "#1c000000"),
        };

        /// <summary>Applies the colours of Windows' current mode and follows later changes.</summary>
        public static void Start(Application app)
        {
            Apply(app, ReadWindowsIsDark());
            SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
            app.Exit += (_, _) => SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        }

        public static void Apply(Application app, bool dark)
        {
            IsDark = dark;
            foreach (var (key, darkValue, lightValue) in Colours)
            {
                var colour = (Color)ColorConverter.ConvertFromString(dark ? darkValue : lightValue);
                var brush = new SolidColorBrush(colour);
                brush.Freeze();
                app.Resources[key + "Color"] = colour;
                app.Resources[key] = brush;
            }
            Changed?.Invoke();
        }

        private static void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color) return;
            var app = Application.Current;
            app?.Dispatcher.BeginInvoke(new Action(() =>
            {
                var dark = ReadWindowsIsDark();
                if (dark != IsDark) Apply(app, dark);
            }));
        }

        /// <summary>Windows' setting for apps; without one (older Windows): light, like Windows itself.</summary>
        private static bool ReadWindowsIsDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
