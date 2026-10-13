using System.Diagnostics;
using System.Runtime.InteropServices;
using Stratagem.Core.Service;
using static Stratagem.Windows.Native;

namespace Stratagem.Windows;

/// <summary>
/// Knows whether the game is running and in the foreground: the foreground window belongs to a process with
/// the configured name, or (fallback) has exactly the configured title. A browser tab "Helldivers 2 – Reddit"
/// must not count. Deliberately never opens the game process (no handle): anti-cheat protects it, and
/// touching it could look suspicious. Process names come from the system's process list.
/// Must be created on the UI thread (the foreground event arrives through its message loop).
/// </summary>
public sealed unsafe class GameWatcher : IGameWatcher, IDisposable
{
    private static GameWatcher? s_current;

    private readonly Func<(string Title, string Process)> _target;
    private readonly nint _hook;
    private readonly System.Windows.Forms.Timer _timer;
    private int _tick;

    public GameWatcher(Func<(string Title, string Process)> target)
    {
        _target = target;
        s_current = this;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, &OnForeground, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        // Titles can change without a foreground switch, and the process list has no event: poll as well.
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => Update(checkProcess: _tick++ % 3 == 0);
        _timer.Start();
        Update(checkProcess: true);
    }

    public bool IsRunning { get; private set; }
    public bool IsFocused { get; private set; }
    public nint Window { get; private set; }
    public event Action? Changed;

    /// <summary>Screen rectangle of the game window (for placing the overlay), null if not in the foreground.</summary>
    public System.Drawing.Rectangle? WindowBounds =>
        IsFocused && Window != 0 && GetWindowRect(Window, out var r)
            ? System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)
            : null;

    [UnmanagedCallersOnly]
    private static void OnForeground(nint hook, uint ev, nint hwnd, int obj, int child, uint thread, uint time) =>
        s_current?.Update(checkProcess: false);

    private void Update(bool checkProcess)
    {
        var (title, process) = _target();
        var hwnd = GetForegroundWindow();
        var focused = hwnd != 0 && (OwnerIs(hwnd, process) || Matches(WindowTitle(hwnd), title));
        var running = focused || (checkProcess ? ProcessRunning(process) : IsRunning);
        if (focused == IsFocused && running == IsRunning && (!focused || hwnd == Window)) return;
        IsFocused = focused;
        IsRunning = running;
        Window = focused ? hwnd : 0;
        Changed?.Invoke();
    }

    private static string WindowTitle(nint hwnd)
    {
        var buffer = stackalloc char[256];
        var length = GetWindowText(hwnd, buffer, 256);
        return new string(buffer, 0, Math.Max(0, length));
    }

    private static bool OwnerIs(nint hwnd, string process)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || string.IsNullOrWhiteSpace(process)) return false;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return string.Equals(p.ProcessName, process, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    /// <summary>Same letters and digits ("HELLDIVERS™ 2" = "helldivers 2"), nothing more.</summary>
    internal static bool Matches(string actual, string expected)
    {
        var a = Normalize(actual);
        var e = Normalize(expected);
        return e.Length > 0 && a == e;

        static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static bool ProcessRunning(string name)
    {
        // Enumerating the process list does not open a handle to the game.
        var processes = Process.GetProcessesByName(name);
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    public void Dispose()
    {
        _timer.Dispose();
        if (_hook != 0) UnhookWinEvent(_hook);
        s_current = null;
    }
}
