using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Licht.Fenster
{
    /// <summary>
    /// Sends changes of one lamp: always the newest value, never a queue of stale ones. While a command is on
    /// its way, further changes are merged; the merged change goes next. A short gap between commands gives
    /// the lamp room to breathe (like web/app.js).
    /// </summary>
    internal sealed class LampSender
    {
        public static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(60);

        private readonly string _id;
        private readonly IHub _hub;
        private readonly IClock _clock;
        private readonly Action<string, bool> _failed;
        private Dictionary<string, object?>? _pending;
        private bool _busy;

        /// <param name="failed">(message, showRealState): showRealState is false when the user already tried again.</param>
        public LampSender(string id, IHub hub, IClock clock, Action<string, bool> failed)
        {
            _id = id;
            _hub = hub;
            _clock = clock;
            _failed = failed;
        }

        public bool Idle => !_busy && _pending == null;

        public void Push(Dictionary<string, object?> change)
        {
            if (_pending == null) _pending = new Dictionary<string, object?>(change);
            else foreach (var pair in change) _pending[pair.Key] = pair.Value;
            _ = Run();
        }

        private async Task Run()
        {
            if (_busy || _pending == null) return;
            _busy = true;
            var change = _pending;
            _pending = null;
            string? failure = null;
            try
            {
                await _hub.Call("update", _id, change);
            }
            catch (HubException e)
            {
                failure = e.Message;
            }
            catch (Exception e)
            {
                failure = e.Message;
            }
            if (failure != null) _pending = null; // the lamp is shown as it is; a new tap is sent again
            await _clock.Delay(Gap);
            _busy = false;
            if (failure != null) _failed(failure, _pending == null);
            if (_pending != null) _ = Run();
        }
    }
}
