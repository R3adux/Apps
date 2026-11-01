using System;
using System.Windows;
using System.Windows.Controls;
using Licht.Fenster;

namespace LichtFenster
{
    public partial class ConnectionsDialog : UserControl
    {
        public ConnectionsDialog(ConnectionsViewModel connections)
        {
            InitializeComponent();
            DataContext = connections;
        }

        public event Action? CloseClicked;

        public FrameworkElement FirstFocus => DoneButton;

        private void DoneClick(object sender, RoutedEventArgs e) => CloseClicked?.Invoke();
    }
}
