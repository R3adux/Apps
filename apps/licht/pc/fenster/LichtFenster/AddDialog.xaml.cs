using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Licht.Fenster;

namespace LichtFenster
{
    public partial class AddDialog : UserControl
    {
        public AddDialog(AddViewModel add)
        {
            InitializeComponent();
            DataContext = add;
        }

        /// <summary>The user closed the dialog (adding goes on in the background if it has started).</summary>
        public event Action? CloseClicked;

        public FrameworkElement FirstFocus => CodeBox;

        private void CloseClick(object sender, RoutedEventArgs e) => CloseClicked?.Invoke();

        // Enter in a field: add.
        private void CodeKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || !(DataContext is AddViewModel add)) return;
            e.Handled = true;
            if (add.SubmitCommand.CanExecute(null)) add.SubmitCommand.Execute(null);
        }
    }
}
