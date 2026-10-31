using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using Licht.Fenster;

namespace LichtFenster
{
    /// <summary>
    /// Where the window is and how big: remembered in the data folder (fenster-platz.json). The first time,
    /// and when the remembered place is no longer on any screen: bottom right, above the tray icon.
    /// </summary>
    internal sealed class Placement
    {
        public const double DefaultWidth = 460;
        public const double DefaultHeight = 680;
        private const double Margin = 12;

        private readonly string? _file;

        public Placement(string? dataDir) => _file = dataDir == null ? null : Path.Combine(dataDir, "fenster-platz.json");

        public void Restore(Window window)
        {
            var saved = Load();
            if (saved is Rect rect && IsVisible(rect))
            {
                window.Left = rect.Left;
                window.Top = rect.Top;
                window.Width = Math.Max(window.MinWidth, rect.Width);
                window.Height = Math.Max(window.MinHeight, rect.Height);
                return;
            }
            var area = SystemParameters.WorkArea;
            window.Width = Math.Min(DefaultWidth, area.Width - 2 * Margin);
            window.Height = Math.Min(DefaultHeight, area.Height - 2 * Margin);
            window.Left = area.Right - window.Width - Margin;
            window.Top = area.Bottom - window.Height - Margin;
        }

        public void Save(Window window)
        {
            if (_file == null) return;
            var bounds = window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
                : window.RestoreBounds;
            if (bounds.IsEmpty || double.IsNaN(bounds.Left) || bounds.Width < 50 || bounds.Height < 50) return;
            try
            {
                File.WriteAllText(_file, Json.Write(new Dictionary<string, object?>
                {
                    ["left"] = Math.Round(bounds.Left),
                    ["top"] = Math.Round(bounds.Top),
                    ["width"] = Math.Round(bounds.Width),
                    ["height"] = Math.Round(bounds.Height),
                }));
            }
            catch (Exception)
            {
                // only a convenience
            }
        }

        private Rect? Load()
        {
            try
            {
                if (_file == null || !File.Exists(_file)) return null;
                if (!(Json.Parse(File.ReadAllText(_file)) is IDictionary<string, object?> o)) return null;
                if (o.Num("left") is double left && o.Num("top") is double top && o.Num("width") is double width && o.Num("height") is double height
                    && width > 0 && height > 0)
                    return new Rect(left, top, width, height);
            }
            catch (Exception)
            {
                // a broken file: like the first start
            }
            return null;
        }

        // The title bar must be reachable on the screens there are now (a second screen may be gone).
        private static bool IsVisible(Rect rect)
        {
            var screens = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var titleBar = new Rect(rect.Left + 40, rect.Top, Math.Max(1, rect.Width - 80), 30);
            titleBar.Intersect(screens);
            return !titleBar.IsEmpty && titleBar.Width >= 80 && titleBar.Height >= 20;
        }
    }
}
