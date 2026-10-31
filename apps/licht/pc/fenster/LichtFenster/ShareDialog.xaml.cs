using System;
using System.Windows;
using System.Windows.Controls;
using Licht.Fenster;

namespace LichtFenster
{
    public partial class ShareDialog : UserControl
    {
        public ShareDialog(ShareViewModel share)
        {
            InitializeComponent();
            DataContext = share;
        }

        public event Action? CloseClicked;

        public FrameworkElement FirstFocus => DoneButton;

        private void DoneClick(object sender, RoutedEventArgs e) => CloseClicked?.Invoke();
    }
}
