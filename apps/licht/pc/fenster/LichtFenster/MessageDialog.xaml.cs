using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LichtFenster
{
    public partial class MessageDialog : UserControl
    {
        /// <param name="text">Explanation (may be empty).</param>
        /// <param name="field">Label of a text field, or null for none.</param>
        /// <param name="cancelLabel">Null: only the OK button.</param>
        public MessageDialog(string title, string text, string? field, string value, string okLabel, string? cancelLabel, bool danger)
        {
            InitializeComponent();
            TitleText.Text = title;
            BodyText.Text = text;
            BodyText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            if (field != null)
            {
                Field.Visibility = Visibility.Visible;
                FieldLabel.Text = field;
                Input.Text = value;
                Input.SelectAll();
            }
            OkButton.Content = okLabel;
            if (danger) OkButton.Style = (Style)FindResource("Btn.Danger");
            if (cancelLabel == null) CancelButton.Visibility = Visibility.Collapsed;
            else CancelButton.Content = cancelLabel;
        }

        /// <summary>OK (true) or cancel (false).</summary>
        public event Action<bool>? Done;

        public string Text => Input.Text;

        /// <summary>Where the keyboard goes first: the text field, else OK.</summary>
        public FrameworkElement FirstFocus => Field.Visibility == Visibility.Visible ? Input : OkButton;

        private void OkClick(object sender, RoutedEventArgs e) => Done?.Invoke(true);

        private void CancelClick(object sender, RoutedEventArgs e) => Done?.Invoke(false);

        private void InputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Done?.Invoke(true);
        }
    }
}
