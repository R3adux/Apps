using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Licht.Fenster
{
    /// <summary>
    /// Licht – the process with the lamps – as the window sees it. The messages are listed in pc/bridge.js.
    /// </summary>
    public interface IHub
    {
        /// <summary>Calls a function of Licht. Throws <see cref="HubException"/> with a German message on failure.</summary>
        Task<object?> Call(string method, params object?[] args);

        /// <summary>Messages from Licht other than replies: lights, light, adding, hello, show, message, fatal, quit.</summary>
        event Action<IDictionary<string, object?>>? Message;

        /// <summary>Tells Licht something without expecting an answer (ready, log, quit).</summary>
        void Send(IDictionary<string, object?> message);
    }

    public sealed class HubException : Exception
    {
        public HubException(string message, int status) : base(message) => Status = status;

        public int Status { get; }
    }

    /// <summary>
    /// The connection to Licht over standard input/output: JSON, one object per line.
    /// Events and replies are delivered on the <see cref="SynchronizationContext"/> given (the UI thread).
    /// </summary>
    public sealed class HubConnection : IHub
    {
        private readonly TextReader _input;
        private readonly TextWriter _output;
        private readonly SynchronizationContext? _context;
        private readonly object _lock = new object();
        private readonly Dictionary<int, TaskCompletionSource<object?>> _pending = new Dictionary<int, TaskCompletionSource<object?>>();
        private int _nextId;
        private bool _closed;

        public HubConnection(TextReader input, TextWriter output, SynchronizationContext? context)
        {
            _input = input;
            _output = output;
            _context = context;
        }

        public event Action<IDictionary<string, object?>>? Message;

        /// <summary>Licht has gone (its end of the connection closed).</summary>
        public event Action? Closed;

        /// <summary>Starts reading on a background thread.</summary>
        public void Start()
        {
            var thread = new Thread(ReadLoop) { IsBackground = true, Name = "Licht connection" };
            thread.Start();
        }

        public Task<object?> Call(string method, params object?[] args)
        {
            var source = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            int id;
            lock (_lock)
            {
                if (_closed)
                {
                    source.SetException(new HubException("Licht läuft nicht mehr.", 503));
                    return source.Task;
                }
                id = ++_nextId;
                _pending[id] = source;
            }
            Send(new Dictionary<string, object?> { ["t"] = "call", ["id"] = id, ["method"] = method, ["args"] = args });
            return source.Task;
        }

        public void Send(IDictionary<string, object?> message)
        {
            var line = Json.Write(message);
            lock (_lock)
            {
                if (_closed) return;
                try
                {
                    _output.Write(line);
                    _output.Write('\n');
                    _output.Flush();
                }
                catch (IOException)
                {
                    // Licht is gone; the reading side notices and reports it
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        private void ReadLoop()
        {
            try
            {
                string? line;
                while ((line = _input.ReadLine()) != null)
                {
                    if (line.Trim().Length == 0) continue;
                    IDictionary<string, object?>? message;
                    try
                    {
                        message = Json.Parse(line) as IDictionary<string, object?>;
                    }
                    catch (FormatException)
                    {
                        message = null;
                    }
                    if (message == null) continue;
                    if (message.Str("t") == "reply") Complete(message);
                    else Post(() => Message?.Invoke(message));
                }
            }
            catch (IOException)
            {
                // broken pipe: same as closed
            }
            catch (ObjectDisposedException)
            {
            }
            List<TaskCompletionSource<object?>> open;
            lock (_lock)
            {
                _closed = true;
                open = new List<TaskCompletionSource<object?>>(_pending.Values);
                _pending.Clear();
            }
            foreach (var source in open) source.TrySetException(new HubException("Licht läuft nicht mehr.", 503));
            Post(() => Closed?.Invoke());
        }

        private void Complete(IDictionary<string, object?> reply)
        {
            var id = reply.Int("id", -1);
            TaskCompletionSource<object?>? source;
            lock (_lock)
            {
                if (!_pending.TryGetValue(id, out source)) return;
                _pending.Remove(id);
            }
            if (reply.Bool("ok")) source.TrySetResult(reply.TryGetValue("result", out var result) ? result : null);
            else source.TrySetException(new HubException(reply.Str("error") ?? "Unbekannter Fehler", reply.Int("status", 500)));
        }

        private void Post(Action action)
        {
            if (_context == null) action();
            else _context.Post(_ => action(), null);
        }
    }
}
