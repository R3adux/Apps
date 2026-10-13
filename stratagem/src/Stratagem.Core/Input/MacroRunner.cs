using System.Collections.Concurrent;

namespace Stratagem.Core.Input;

/// <summary>
/// Sends input events to the system (SendInput on Windows). Events must be marked as "ours", so the
/// hooks recognise and ignore them. Must not block (also called from the hook thread).
/// </summary>
public interface IInputOutput
{
    void Send(Key key, bool down);
    void Send(MouseButton button, bool down);
}

/// <summary>Waits precisely. Windows raises the timer resolution while a macro runs (default 15.6 ms is too coarse).</summary>
public interface IPrecisionTimer
{
    IDisposable HighResolution();
}

/// <summary>
/// Keys the player holds and the game knows about: fed by the keyboard hook with the player's events
/// that were passed on to the game (our own events and swallowed ones are not counted).
/// </summary>
public sealed class PhysicalKeyState
{
    private readonly ConcurrentDictionary<Key, bool> _down = new();

    public void Set(Key key, bool down)
    {
        if (down) _down[key] = true;
        else _down.TryRemove(key, out _);
    }

    public bool IsDown(Key key) => _down.ContainsKey(key);
    public void Clear() => _down.Clear();
}

/// <summary>
/// Plays macros one at a time on its own thread.
/// Guarantees: no key stays pressed (also on cancel/error), keys the player still holds are pressed
/// again afterwards (e.g. W for walking), and while a macro runs the player's presses of the keys it
/// uses are held back from the game, so they cannot mix into the code.
/// </summary>
public sealed class MacroRunner : IDisposable
{
    private readonly IInputOutput _output;
    private readonly PhysicalKeyState _physical;
    private readonly IPrecisionTimer? _timer;
    private readonly Action<string> _log;
    private readonly BlockingCollection<Job> _queue = new(boundedCapacity: 1);
    private readonly Thread _thread;
    private volatile HashSet<Key> _busyKeys = [];
    private CancellationTokenSource? _current;
    private int _pending;
    private readonly object _gate = new();

    private sealed record Job(IReadOnlyList<MacroStep> Steps, string Label, TaskCompletionSource<bool> Done);

    public MacroRunner(IInputOutput output, PhysicalKeyState physical, IPrecisionTimer? timer = null, Action<string>? log = null)
    {
        _output = output;
        _physical = physical;
        _timer = timer;
        _log = log ?? (_ => { });
        _thread = new Thread(Loop) { IsBackground = true, Name = "Stratagem macros", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public bool IsRunning => Volatile.Read(ref _pending) == 1;

    /// <summary>
    /// True if a player event for this key must not reach the game right now (the macro uses it).
    /// Called from the keyboard hook: must be fast.
    /// </summary>
    public bool ShouldHoldBack(Key key) => _busyKeys.Contains(key);

    /// <summary>
    /// Queues a macro. Returns false right away if one is already running or waiting:
    /// in the game a second code would only replace the first, so dropping is the expected behaviour.
    /// The task completes with true when the macro ran to the end.
    /// </summary>
    public bool TryRun(IReadOnlyList<MacroStep> steps, string label, out Task<bool> done)
    {
        var job = new Job(steps, label, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        done = job.Done.Task;
        if (_queue.IsAddingCompleted || Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return false;
        if (_queue.TryAdd(job)) return true;
        Volatile.Write(ref _pending, 0);
        return false;
    }

    /// <summary>Stops the running macro (e.g. game lost focus). Pressed keys are released.</summary>
    public void Cancel()
    {
        lock (_gate) _current?.Cancel();
    }

    private void Loop()
    {
        foreach (var job in _queue.GetConsumingEnumerable())
        {
            var cts = new CancellationTokenSource();
            lock (_gate) _current = cts;
            var completed = false;
            try
            {
                completed = Execute(job, cts.Token);
            }
            catch (Exception e)
            {
                _log($"Makro '{job.Label}' fehlgeschlagen: {e.Message}");
            }
            finally
            {
                lock (_gate) _current = null;
                cts.Dispose();
                Volatile.Write(ref _pending, 0); // before completing: whoever awaits sees "not running"
                job.Done.TrySetResult(completed);
            }
        }
    }

    private bool Execute(Job job, CancellationToken ct)
    {
        var used = job.Steps.OfType<MacroStep.Press>().Select(p => p.Key).ToHashSet();
        var pressed = new HashSet<Key>();
        _busyKeys = used;
        using var precision = _timer?.HighResolution();
        try
        {
            // A key the player holds (W for walking) is first released, so our press is a real new press.
            foreach (var key in used.Where(_physical.IsDown)) _output.Send(key, false);

            foreach (var step in job.Steps)
            {
                if (ct.IsCancellationRequested) return false;
                switch (step)
                {
                    case MacroStep.Press p:
                        _output.Send(p.Key, true);
                        pressed.Add(p.Key);
                        break;
                    case MacroStep.Release r:
                        _output.Send(r.Key, false);
                        pressed.Remove(r.Key);
                        break;
                    case MacroStep.Wait w:
                        if (ct.WaitHandle.WaitOne(w.Ms)) return false;
                        break;
                }
            }
            return true;
        }
        finally
        {
            foreach (var key in pressed) _output.Send(key, false);
            // Hand the keys back: whatever the player still holds is pressed again.
            foreach (var key in used.Where(_physical.IsDown)) _output.Send(key, true);
            _busyKeys = [];
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        Cancel();
        _thread.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }
}
