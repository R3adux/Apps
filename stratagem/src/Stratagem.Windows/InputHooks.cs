using System.Runtime.InteropServices;
using Stratagem.Core.Input;
using static Stratagem.Windows.Native;

namespace Stratagem.Windows;

/// <summary>
/// Global low-level keyboard and mouse hooks on a dedicated thread with its own message loop.
/// Every player event goes to <see cref="InputRouter"/>, which decides whether it is swallowed.
///
/// Pitfalls handled here:
/// - Windows silently removes a low-level hook whose callback takes too long (LowLevelHooksTimeout, ~1 s).
///   The callback therefore only does in-memory work, and <see cref="Reinstall"/> re-creates the hooks
///   (called when the game gets the focus).
/// - Our own SendInput events carry <see cref="Native.OurMarker"/> and are never routed (no feedback loops).
/// - AltGr on German keyboards sends a fake left Ctrl with scancode 0x21D: it never matches the real Ctrl (0x1D).
/// - The mouse is "captured" by the game: deltas are computed against the current cursor position,
///   which does not move while we swallow movement.
/// </summary>
public sealed unsafe class InputHooks : IDisposable
{
    private static InputHooks? s_current;

    private readonly InputRouter _router;
    private readonly Action<string> _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private nint _keyboardHook, _mouseHook;
    private const uint WM_REINSTALL = WM_APP + 1;

    public InputHooks(InputRouter router, Action<string> log)
    {
        _router = router;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "Stratagem hooks", Priority = ThreadPriority.Highest };
    }

    public bool Installed => _keyboardHook != 0 && _mouseHook != 0;

    public void Start()
    {
        if (Interlocked.CompareExchange(ref s_current, this, null) is not null)
            throw new InvalidOperationException("Hooks laufen schon");
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Re-creates the hooks (Windows may have removed them after a timeout).</summary>
    public void Reinstall()
    {
        if (_threadId != 0) PostThreadMessageW(_threadId, WM_REINSTALL, 0, 0);
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        Install();
        _ready.Set();
        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == WM_REINSTALL)
            {
                Uninstall();
                Install();
            }
        }
        Uninstall();
    }

    private void Install()
    {
        var module = GetModuleHandleW(null);
        _keyboardHook = SetWindowsHookExW(WH_KEYBOARD_LL, &KeyboardProc, module, 0);
        if (_keyboardHook == 0) _log($"Tastatur-Hook fehlgeschlagen (Fehler {Marshal.GetLastPInvokeError()})");
        _mouseHook = SetWindowsHookExW(WH_MOUSE_LL, &MouseProc, module, 0);
        if (_mouseHook == 0) _log($"Maus-Hook fehlgeschlagen (Fehler {Marshal.GetLastPInvokeError()})");
    }

    private void Uninstall()
    {
        if (_keyboardHook != 0) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != 0) UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = 0;
    }

    [UnmanagedCallersOnly]
    private static nint KeyboardProc(int code, nint wParam, nint lParam)
    {
        var self = s_current;
        if (code < 0 || self is null) return CallNextHookEx(0, code, wParam, lParam);
        try
        {
            var data = (KBDLLHOOKSTRUCT*)lParam;
            var ours = (data->flags & LLKHF_INJECTED) != 0 && data->dwExtraInfo == OurMarker;
            if (!ours)
            {
                var msg = (int)wParam;
                var down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                var up = msg is WM_KEYUP or WM_SYSKEYUP;
                if ((down || up) && self._router.OnKey(ToKey(data), down)) return 1;
            }
        }
        catch (Exception e)
        {
            self._log($"Fehler im Tastatur-Hook: {e.Message}");
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    private static Key ToKey(KBDLLHOOKSTRUCT* data)
    {
        var scan = data->scanCode;
        var extended = (data->flags & LLKHF_EXTENDED) != 0;
        if (scan == 0)
        {
            // Some tools inject virtual keys without a scancode.
            var mapped = MapVirtualKeyW(data->vkCode, MAPVK_VK_TO_VSC_EX);
            scan = mapped & 0xFF;
            extended = (mapped & 0xFF00) is 0xE000 or 0xE100;
        }
        return new Key((ushort)scan, extended);
    }

    [UnmanagedCallersOnly]
    private static nint MouseProc(int code, nint wParam, nint lParam)
    {
        var self = s_current;
        if (code < 0 || self is null) return CallNextHookEx(0, code, wParam, lParam);
        try
        {
            var data = (MSLLHOOKSTRUCT*)lParam;
            var ours = (data->flags & LLMHF_INJECTED) != 0 && data->dwExtraInfo == OurMarker;
            if (!ours && self.HandleMouse((int)wParam, data)) return 1;
        }
        catch (Exception e)
        {
            self._log($"Fehler im Maus-Hook: {e.Message}");
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    private bool HandleMouse(int msg, MSLLHOOKSTRUCT* data)
    {
        switch (msg)
        {
            case WM_MOUSEMOVE:
                if (!GetCursorPos(out var cursor)) return false;
                return _router.OnMouseMove(data->pt.X - cursor.X, data->pt.Y - cursor.Y);
            case WM_LBUTTONDOWN: return _router.OnMouseButton(MouseButton.Left, true);
            case WM_LBUTTONUP: return _router.OnMouseButton(MouseButton.Left, false);
            case WM_RBUTTONDOWN: return _router.OnMouseButton(MouseButton.Right, true);
            case WM_RBUTTONUP: return _router.OnMouseButton(MouseButton.Right, false);
            case WM_MBUTTONDOWN: return _router.OnMouseButton(MouseButton.Middle, true);
            case WM_MBUTTONUP: return _router.OnMouseButton(MouseButton.Middle, false);
            case WM_XBUTTONDOWN or WM_XBUTTONUP:
                var button = (data->mouseData >> 16) == 1 ? MouseButton.X1 : MouseButton.X2;
                return _router.OnMouseButton(button, msg == WM_XBUTTONDOWN);
            default:
                return false; // wheel etc.
        }
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
        Interlocked.CompareExchange(ref s_current, null, this);
        _ready.Dispose();
    }
}
