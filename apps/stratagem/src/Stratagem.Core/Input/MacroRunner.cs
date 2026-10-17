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
/// Guarantees: no key stays pressed (also on cancel/error) unless a job explicitly keeps it held for the next
/// one (the open stratagem list while the radial menu is open), keys the player still holds are pressed
/// again afterwards (e.g. W for walking), and while a macro runs the player's presses of the keys it
/// uses are held back from the game, so they cannot mix into the code.
/// </summary>
public sealed class MacroRunner : IDisposable
{
    private readonly IInputOutput _output;
    private readonly PhysicalKeyState _physical;
    private readonly IPrecisionTimer? _timer;
    private readonly Action<string> _log;
    private readonly BlockingCollection<Job> _queue = new();
    private readonly Thread _thread;
    private readonly HashSet<Key> _held = []; // kept pressed between jobs; macro thread only
    private volatile HashSet<Key> _busyKeys = [];
    private volatile bool _anyHeld;
    private CancellationTokenSource? _current;
    private int _pending;
    private int _generation;
    private readonly object _gate = new();

    private sealed record Job(IReadOnlyList<MacroStep> Steps, string Label, IReadOnlySet<Key> KeepHeld, IReadOnlySet<Key> Guard, int Generation, TaskCompletionSource<bool> Done);

    public MacroRunner(IInputOutput output, PhysicalKeyState physical, IPrecisionTimer? timer = null, Action<string>? log = null)
    {
        _output = output;
        _physical = physical;
        _timer = timer;
        _log = log ?? (_ => { });
        _thread = new Thread(Loop) { IsBackground = true, Name = "Stratagem macros", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>A job is running or waiting.</summary>
    public bool IsRunning => Volatile.Read(ref _pending) > 0;

    /// <summary>
    /// True if a player event for this key must not reach the game right now (the macro uses it).
    /// Called from the keyboard hook: must be fast.
    /// </summary>
    public bool ShouldHoldBack(Key key) => _busyKeys.Contains(key);

    /// <summary>
    /// Runs a macro only if nothing runs or waits: in the game a second code would only replace the first,
    /// so dropping is the expected behaviour. The task completes with true when the macro ran to the end.
    /// </summary>
    public bool TryRun(IReadOnlyList<MacroStep> steps, string label, out Task<bool> done)
    {
        done = Task.FromResult(false);
        if (_queue.IsAddingCompleted || Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return false;
        done = Add(steps, label, null, null);
        return true;
    }

    /// <summary>
    /// Queues a job after the current ones (a sequence that belongs together: open the list, read it, enter the code).
    /// <paramref name="keepHeld"/>: keys this job leaves pressed for a later job, which releases them.
    /// <paramref name="guard"/>: further keys of the player held back from the game while the job runs (and pressed
    /// again afterwards if still held), e.g. walking keys while the list is briefly open for reading.
    /// </summary>
    public Task<bool> Enqueue(IReadOnlyList<MacroStep> steps, string label, IReadOnlySet<Key>? keepHeld = null, IReadOnlySet<Key>? guard = null)
    {
        if (_queue.IsAddingCompleted) return Task.FromResult(false);
        Interlocked.Increment(ref _pending);
        return Add(steps, label, keepHeld, guard);
    }

    private Task<bool> Add(IReadOnlyList<MacroStep> steps, string label, IReadOnlySet<Key>? keepHeld, IReadOnlySet<Key>? guard)
    {
        var job = new Job(steps, label, keepHeld ?? new HashSet<Key>(), guard ?? new HashSet<Key>(), Volatile.Read(ref _generation),
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        try { _queue.Add(job); }
        catch (InvalidOperationException)
        {
            Interlocked.Decrement(ref _pending);
            job.Done.TrySetResult(false);
        }
        return job.Done.Task;
    }

    /// <summary>
    /// Stops the running macro and drops waiting ones (e.g. game lost focus). Every key we pressed is released,
    /// also one kept held between jobs.
    /// </summary>
    public void Cancel()
    {
        Interlocked.Increment(ref _generation);
        lock (_gate) _current?.Cancel();
        if (_anyHeld && !_queue.IsAddingCompleted) Enqueue([new MacroStep.ReleaseHeld()], "Tasten freigeben");
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
                // Dropped by Cancel() while waiting (the release job itself always runs).
                var stale = job.Generation != Volatile.Read(ref _generation) && job.Steps is not [MacroStep.ReleaseHeld];
                if (!stale) completed = Execute(job, cts.Token);
            }
            catch (Exception e)
            {
                _log($"Makro '{job.Label}' fehlgeschlagen: {e.Message}");
            }
            finally
            {
                lock (_gate) _current = null;
                cts.Dispose();
                Interlocked.Decrement(ref _pending); // before completing: whoever awaits sees "not running"
                job.Done.TrySetResult(completed);
            }
        }
        foreach (var key in _held) _output.Send(key, false);
        _held.Clear();
    }

    private bool Execute(Job job, CancellationToken ct)
    {
        var used = job.Steps.OfType<MacroStep.Press>().Select(p => p.Key).ToHashSet();
        used.UnionWith(job.Guard);
        var pressed = new HashSet<Key>();
        var completed = false;
        _busyKeys = used;
        using var precision = _timer?.HighResolution();
        try
        {
            // A key the player holds (W for walking) is first released, so our press is a real new press
            // (and a guarded walking key does not reach the open list as an arrow).
            foreach (var key in used.Where(_physical.IsDown)) _output.Send(key, false);

            foreach (var step in job.Steps)
            {
                if (!Step(step)) return false;
            }
            completed = true;
            return true;

            // False: stop the job here (cancelled, or a Decide chose other steps).
            bool Step(MacroStep step)
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
                        _held.Remove(r.Key);
                        break;
                    case MacroStep.Wait w:
                        if (ct.WaitHandle.WaitOne(w.Ms)) return false;
                        break;
                    case MacroStep.Run run:
                        run.Action();
                        break;
                    case MacroStep.ReleaseHeld:
                        foreach (var key in _held) _output.Send(key, false);
                        _held.Clear();
                        break;
                    case MacroStep.Forget f:
                        _held.Remove(f.Key);
                        pressed.Remove(f.Key);
                        break;
                    case MacroStep.Decide d:
                        if (d.Instead() is not { } instead) break;
                        foreach (var other in instead)
                            if (other is not MacroStep.Decide && !Step(other)) return false;
                        return false;
                }
                return true;
            }
        }
        finally
        {
            foreach (var key in pressed)
            {
                if (completed && job.KeepHeld.Contains(key)) _held.Add(key);
                else _output.Send(key, false);
            }
            if (!completed)
            {
                foreach (var key in _held) _output.Send(key, false);
                _held.Clear();
            }
            // Hand the keys back: whatever the player still holds is pressed again.
            foreach (var key in used.Where(k => _physical.IsDown(k) && !_held.Contains(k))) _output.Send(key, true);
            _anyHeld = _held.Count > 0;
            _busyKeys = [];
        }
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _generation);
        lock (_gate) _current?.Cancel();
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }
}
