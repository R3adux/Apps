using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LichtFenster
{
    /// <summary>
    /// The content of the Licht window. Dialogs are layers over the page (like in the phone app), not extra
    /// windows: they look the same, follow the window and can never get lost behind it.
    /// </summary>
    public partial class MainView : UserControl
    {
        private readonly List<Layer> _layers = new List<Layer>();
        private readonly DispatcherTimer _toastTimer = new DispatcherTimer();

        public MainView()
        {
            InitializeComponent();
            _toastTimer.Tick += (_, _) => HideToast();
            PreviewKeyDown += OnPreviewKeyDown;
        }

        /// <summary>Off when drawing pictures of the window (--render): everything at its final state at once.</summary>
        internal static bool Animations { get; set; } = true;

        // ⋯ and ⋮ open their menu with a normal click.
        private void OpenMenu(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || button.ContextMenu == null) return;
            var menu = button.ContextMenu;
            menu.DataContext = button.DataContext;
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.HorizontalOffset = 0;
            menu.IsOpen = true;
        }

        /// <summary>
        /// Shows a dialog over the page. <paramref name="cancel"/> runs for Escape (null: Escape does nothing).
        /// Returns the function that closes it again.
        /// </summary>
        public Action ShowDialog(FrameworkElement content, Action? cancel, FrameworkElement? focus = null)
        {
            var previousFocus = Keyboard.FocusedElement as IInputElement;
            var card = new Border { Style = (Style)FindResource("DialogCard"), Child = content };
            var layer = new Grid { Children = { card } };
            layer.SetResourceReference(Panel.BackgroundProperty, "Backdrop");
            KeyboardNavigation.SetTabNavigation(layer, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetControlTabNavigation(layer, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetDirectionalNavigation(layer, KeyboardNavigationMode.Contained);

            var entry = new Layer(layer, cancel, previousFocus);
            foreach (var other in _layers) other.Element.IsEnabled = false;
            _layers.Add(entry);
            Overlays.Children.Add(layer);
            Page.IsEnabled = false;

            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                var target = focus ?? FirstFocusable(card);
                if (target != null) Keyboard.Focus(target);
                else layer.Focus();
            }));

            var closed = false;
            return () =>
            {
                if (closed) return;
                closed = true;
                _layers.Remove(entry);
                Overlays.Children.Remove(layer);
                if (_layers.Count > 0)
                {
                    var top = _layers[_layers.Count - 1];
                    top.Element.IsEnabled = true;
                    var target = FirstFocusable(top.Element);
                    if (target != null) Keyboard.Focus(target);
                }
                else
                {
                    Page.IsEnabled = true;
                    if (entry.PreviousFocus is UIElement { IsVisible: true, IsEnabled: true } before) Keyboard.Focus(before);
                }
            };
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || _layers.Count == 0) return;
            _layers[_layers.Count - 1].Cancel?.Invoke();
            e.Handled = true;
        }

        private static IInputElement? FirstFocusable(DependencyObject root)
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is UIElement { Focusable: true, IsEnabled: true, IsVisible: true } element
                    && (element is TextBox || element is ButtonBase)) return element;
                var inner = FirstFocusable(child);
                if (inner != null) return inner;
            }
            return null;
        }

        /// <summary>A short message at the bottom (red frame: something went wrong; accent: done).</summary>
        public void ShowToast(string text, bool ok)
        {
            ToastText.Text = text;
            Toast.SetResourceReference(Border.BorderBrushProperty, ok ? "Accent" : "Danger");
            Toast.Visibility = Visibility.Visible;
            Toast.BeginAnimation(OpacityProperty, Animations ? new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) : null);
            Toast.Opacity = 1;
            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromMilliseconds(Math.Min(9000, 2500 + text.Length * 40));
            _toastTimer.Start();
        }

        private void HideToast()
        {
            _toastTimer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) =>
            {
                if (!_toastTimer.IsEnabled) Toast.Visibility = Visibility.Collapsed;
            };
            Toast.BeginAnimation(OpacityProperty, fade);
        }

        private sealed class Layer
        {
            public Layer(FrameworkElement element, Action? cancel, IInputElement? previousFocus)
            {
                Element = element;
                Cancel = cancel;
                PreviousFocus = previousFocus;
            }

            public FrameworkElement Element { get; }
            public Action? Cancel { get; }
            public IInputElement? PreviousFocus { get; }
        }
    }
}
