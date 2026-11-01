using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Licht.Fenster;

namespace LichtFenster
{
    /// <summary>The dialogs and messages of the Licht window (IUi of the view models).</summary>
    internal sealed class WindowUi : IUi
    {
        private readonly MainView _view;
        private readonly Action _show;
        private readonly Action _bringToFront;
        private readonly Action _quit;

        /// <param name="show">Makes the window visible (for messages that must be seen).</param>
        public WindowUi(MainView view, Action show, Action bringToFront, Action quit)
        {
            _view = view;
            _show = show;
            _bringToFront = bringToFront;
            _quit = quit;
        }

        public Task<string?> AskText(string title, string label, string value, string okLabel)
        {
            var dialog = new MessageDialog(title, "", label, value, okLabel, "Abbrechen", danger: false);
            var result = new TaskCompletionSource<string?>();
            Action close = () => { };
            dialog.Done += ok =>
            {
                close();
                result.TrySetResult(ok ? dialog.Text : null);
            };
            close = _view.ShowDialog(dialog, () => { close(); result.TrySetResult(null); }, dialog.FirstFocus);
            return result.Task;
        }

        public Task<bool> Confirm(string title, string text, string okLabel, bool danger)
        {
            var dialog = new MessageDialog(title, text, null, "", okLabel, "Abbrechen", danger);
            var result = new TaskCompletionSource<bool>();
            Action close = () => { };
            dialog.Done += ok =>
            {
                close();
                result.TrySetResult(ok);
            };
            close = _view.ShowDialog(dialog, () => { close(); result.TrySetResult(false); }, dialog.FirstFocus);
            return result.Task;
        }

        public Task Notice(string title, string text)
        {
            _show();
            var dialog = new MessageDialog(title, text, null, "", "OK", null, danger: false);
            var result = new TaskCompletionSource<bool>();
            Action close = () => { };
            Action done = () => { close(); result.TrySetResult(true); };
            dialog.Done += _ => done();
            close = _view.ShowDialog(dialog, done, dialog.FirstFocus);
            return result.Task;
        }

        public void Toast(string text, bool ok = false) => _view.ShowToast(text, ok);

        public Task ShowAdd(AddViewModel add)
        {
            var dialog = new AddDialog(add);
            var result = new TaskCompletionSource<bool>();
            Action close = () => { };
            Action done = () =>
            {
                close();
                add.Closed();
                result.TrySetResult(true);
            };
            dialog.CloseClicked += done;
            add.CloseRequested += done;
            close = _view.ShowDialog(dialog, done, dialog.FirstFocus);
            return result.Task;
        }

        public Task ShowShare(ShareViewModel share)
        {
            share.CopyToClipboard = CopyToClipboard;
            var dialog = new ShareDialog(share);
            var result = new TaskCompletionSource<bool>();
            Action close = () => { };
            Action done = () => { close(); result.TrySetResult(true); };
            dialog.CloseClicked += done;
            share.CloseRequested += done;
            close = _view.ShowDialog(dialog, done, dialog.FirstFocus);
            return result.Task;
        }

        public Task ShowConnections(ConnectionsViewModel connections)
        {
            var dialog = new ConnectionsDialog(connections);
            var result = new TaskCompletionSource<bool>();
            Action close = () => { };
            Action done = () => { close(); result.TrySetResult(true); };
            dialog.CloseClicked += done;
            close = _view.ShowDialog(dialog, done, dialog.FirstFocus);
            return result.Task;
        }

        public Task ShowFatal(string title, string text)
        {
            _show();
            _bringToFront();
            var dialog = new MessageDialog(title, text, null, "", "Licht schließen", null, danger: false);
            var result = new TaskCompletionSource<bool>();
            dialog.Done += _ => result.TrySetResult(true);
            _view.ShowDialog(dialog, null, dialog.FirstFocus);
            return result.Task;
        }

        public void CopyToClipboard(string text)
        {
            // Another program may hold the clipboard for a moment: try a few times.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(text, true);
                    Toast("Code kopiert.", ok: true);
                    return;
                }
                catch (ExternalException)
                {
                    Thread.Sleep(60);
                }
            }
            Toast("Kopieren ging gerade nicht. Bitte noch einmal versuchen.");
        }

        public void BringToFront() => _bringToFront();

        public void Quit() => _quit();
    }
}
