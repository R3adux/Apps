using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;

namespace LichtFenster
{
    /// <summary>
    /// The Licht window. Closing it only hides it – Licht keeps running in the background (tray symbol);
    /// "Licht beenden" ends it for good.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly Placement _placement;
        private bool _closingForGood;
        private DateTime _deactivated = DateTime.MinValue;

        internal MainWindow(Placement placement)
        {
            InitializeComponent();
            _placement = placement;
            _placement.Restore(this);
            SourceInitialized += (_, _) => Native.UseDarkTitleBar(Handle, Theme.IsDark);
            Theme.Changed += () => Native.UseDarkTitleBar(Handle, Theme.IsDark);
            Deactivated += (_, _) => _deactivated = DateTime.UtcNow;
            Closing += OnClosing;
        }

        /// <summary>The user closed the window (it went to the tray).</summary>
        public event Action? HiddenByUser;

        private IntPtr Handle => new WindowInteropHelper(this).Handle;

        /// <summary>Shows the window in front of the others.</summary>
        public void ShowAndActivate()
        {
            if (_closingForGood) return;
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Native.BringToFront(Handle);
        }

        /// <summary>Click on the tray symbol: open the window, or hide it when it was in front.</summary>
        public void Toggle()
        {
            // The click on the tray symbol takes the focus from the window first: was it in front just before?
            var wasInFront = IsActive || (DateTime.UtcNow - _deactivated).TotalMilliseconds < 500;
            if (IsVisible && WindowState != WindowState.Minimized && wasInFront) HideToTray();
            else ShowAndActivate();
        }

        private void HideToTray()
        {
            _placement.Save(this);
            Hide();
            HiddenByUser?.Invoke();
        }

        private void OnClosing(object? sender, CancelEventArgs e)
        {
            if (_closingForGood) return;
            e.Cancel = true;
            HideToTray();
        }

        /// <summary>Licht ends: remember the place and close.</summary>
        public void CloseForGood()
        {
            if (_closingForGood) return;
            if (IsVisible) _placement.Save(this);
            _closingForGood = true;
            Close();
        }
    }
}
