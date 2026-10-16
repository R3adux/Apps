using System.Runtime.InteropServices;
using Stratagem.Core.Input;
using static Stratagem.Windows.Native;

namespace Stratagem.Windows;

/// <summary>
/// Sends keys as scancodes via SendInput (games read positions, not letters), marked with <see cref="OurMarker"/>.
/// No driver, no injection into the game: the same mechanism as Stream Deck, AutoHotkey or Logitech G Hub.
/// </summary>
public sealed unsafe class WindowsInput(Action<string> log) : IInputOutput
{
    private long _lastWarning;

    public void Send(Key key, bool down)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.ki.wScan = key.ScanCode;
        input.ki.dwFlags = KEYEVENTF_SCANCODE | (key.Extended ? KEYEVENTF_EXTENDEDKEY : 0) | (down ? 0 : KEYEVENTF_KEYUP);
        input.ki.dwExtraInfo = OurMarker;
        Dispatch(ref input, key.Name);
    }

    public void Send(MouseButton button, bool down)
    {
        var input = new INPUT { type = INPUT_MOUSE };
        input.mi.dwExtraInfo = OurMarker;
        (input.mi.dwFlags, input.mi.mouseData) = button switch
        {
            MouseButton.Left => (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP, 0u),
            MouseButton.Right => (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP, 0u),
            MouseButton.Middle => (down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP, 0u),
            MouseButton.X1 => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, 1u),
            _ => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, 2u),
        };
        Dispatch(ref input, $"Mouse{button}");
    }

    private void Dispatch(ref INPUT input, string what)
    {
        fixed (INPUT* p = &input)
        {
            if (SendInput(1, p, sizeof(INPUT)) == 1) return;
        }
        // Usually UIPI: the game runs as administrator, we do not. Then Windows drops our input.
        var error = Marshal.GetLastPInvokeError();
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastWarning) < 10_000) return;
        Interlocked.Exchange(ref _lastWarning, now);
        log($"Windows hat die Eingabe '{what}' blockiert (Fehler {error}). Läuft das Spiel als Administrator? Dann Stratagem ebenfalls als Administrator starten.");
    }
}

/// <summary>Raises the Windows timer resolution to 1 ms while a macro runs (default: 15.6 ms steps).</summary>
public sealed class PrecisionTimer : IPrecisionTimer
{
    public IDisposable HighResolution()
    {
        timeBeginPeriod(1);
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) timeEndPeriod(1);
        }
    }
}
