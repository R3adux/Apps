using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Licht.Fenster
{
    /// <summary>Base for view models: change notifications for the window's bindings.</summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected void OnPropertiesChanged(params string[] names)
        {
            foreach (var name in names) OnPropertyChanged(name);
        }
    }

    /// <summary>A command for buttons and menu entries.</summary>
    public sealed class Command : ICommand
    {
        private readonly Action<object?> _run;
        private readonly Func<object?, bool>? _can;

        public Command(Action run, Func<bool>? can = null)
            : this(_ => run(), can == null ? null : new Func<object?, bool>(_ => can())) { }

        public Command(Action<object?> run, Func<object?, bool>? can = null)
        {
            _run = run;
            _can = can;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _can?.Invoke(parameter) ?? true;

        public void Execute(object? parameter)
        {
            if (CanExecute(parameter)) _run(parameter);
        }

        public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

        /// <summary>Runs an async action; errors are handled by the action itself (fire and forget is intended).</summary>
        public static Command Async(Func<Task> run, Func<bool>? can = null) => new Command(() => _ = run(), can);

        public static Command Async(Func<object?, Task> run) => new Command(p => _ = run(p));
    }

    /// <summary>Time, replaceable in tests.</summary>
    public interface IClock
    {
        DateTime UtcNow { get; }
        Task Delay(TimeSpan delay);
    }

    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();
        public DateTime UtcNow => DateTime.UtcNow;
        public Task Delay(TimeSpan delay) => Task.Delay(delay);
    }

    /// <summary>What only the window itself can do: dialogs, messages, the clipboard.</summary>
    public interface IUi
    {
        /// <summary>A question with a text field; the entered text, or null when cancelled.</summary>
        Task<string?> AskText(string title, string label, string value, string okLabel);

        /// <summary>A yes/no question.</summary>
        Task<bool> Confirm(string title, string text, string okLabel, bool danger);

        /// <summary>A message with only an OK button.</summary>
        Task Notice(string title, string text);

        /// <summary>A short message at the bottom that disappears by itself.</summary>
        void Toast(string text, bool ok = false);

        /// <summary>Shows the add dialog; completes when it is closed.</summary>
        Task ShowAdd(AddViewModel add);

        /// <summary>Shows the share dialog; completes when it is closed.</summary>
        Task ShowShare(ShareViewModel share);

        /// <summary>Licht cannot run: show it; the window program ends afterwards.</summary>
        Task ShowFatal(string title, string text);

        void CopyToClipboard(string text);

        /// <summary>The window to the front (Licht.exe was started again).</summary>
        void BringToFront();

        /// <summary>End the window program.</summary>
        void Quit();
    }
}
