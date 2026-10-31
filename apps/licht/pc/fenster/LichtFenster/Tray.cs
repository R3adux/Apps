using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace LichtFenster
{
    /// <summary>
    /// The lamp symbol at the bottom right of the taskbar: click opens or hides the window, right click shows
    /// the menu (open, start with Windows, quit) – in the same look as the window's menus.
    /// </summary>
    internal sealed class Tray : IDisposable
    {
        private readonly Forms.NotifyIcon _icon;
        private readonly ContextMenu _menu;
        private readonly MenuItem _autostart;
        private readonly Window _window;

        public Tray(Window window, Action toggleWindow, Action openWindow, Func<bool> autostart, Action<bool> setAutostart, Action quit)
        {
            _window = window;
            var open = new MenuItem { Header = "Licht öffnen", FontWeight = FontWeights.SemiBold };
            open.Click += (_, _) => openWindow();
            _autostart = new MenuItem { Header = "Mit Windows starten" };
            _autostart.Click += (_, _) => setAutostart(!autostart());
            var end = new MenuItem { Header = "Licht beenden" };
            end.Click += (_, _) => quit();
            _menu = new ContextMenu
            {
                Items = { open, _autostart, new Separator(), end },
                Placement = PlacementMode.MousePoint,
            };
            _menu.Opened += (_, _) => _autostart.IsChecked = autostart();

            _icon = new Forms.NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "Licht",
                Visible = true,
            };
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left) toggleWindow();
            };
            _icon.MouseUp += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Right) ShowMenu();
            };
        }

        private void ShowMenu()
        {
            // Without this, the menu would not close when clicking elsewhere (Windows only closes menus of the
            // program in front).
            Native.SetForegroundWindow(new WindowInteropHelper(_window).EnsureHandle());
            _menu.IsOpen = true;
        }

        /// <summary>A Windows notification next to the tray symbol.</summary>
        public void Balloon(string title, string text) => _icon.ShowBalloonTip(6000, title, text, Forms.ToolTipIcon.None);

        private static System.Drawing.Icon LoadIcon()
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/licht.ico"));
            using var stream = resource!.Stream;
            return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        }

        public void Dispose()
        {
            _menu.IsOpen = false;
            _icon.Visible = false;
            _icon.Icon?.Dispose();
            _icon.Dispose();
        }
    }
}
