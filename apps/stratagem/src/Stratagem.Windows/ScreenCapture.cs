using System.Runtime.InteropServices;
using Stratagem.Core.Recognition;
using static Stratagem.Windows.Native;

namespace Stratagem.Windows;

/// <summary>
/// Takes a picture of the top-left area of the game window (where the stratagem list is) with GDI, the way
/// screenshot tools do: nothing touches the game. Works for borderless/windowed games; exclusive fullscreen
/// or HDR can give a black picture, which is reported. Only the needed area (~500×1000 px at 1080p) is copied,
/// which takes a few milliseconds.
/// </summary>
public sealed unsafe class GdiScreenCapture(Func<nint> gameWindow, Action<string> log) : IScreenCapture
{
    private long _lastWarning;

    public PixelImage? CaptureMenuArea(out double scale)
    {
        scale = 1;
        var hwnd = gameWindow();
        if (hwnd == 0 || !GetClientRect(hwnd, out var client)) return null;
        var origin = new POINT();
        if (!ClientToScreen(hwnd, ref origin)) return null;
        int clientW = client.Right - client.Left, clientH = client.Bottom - client.Top;
        if (clientW < 200 || clientH < 200) return null;
        scale = clientH / 1080.0;
        int w = Math.Min(clientW, (int)(520 * scale)), h = Math.Min(clientH, (int)(1000 * scale));

        var screen = GetDC(0);
        var mem = CreateCompatibleDC(screen);
        var info = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var bitmap = CreateDIBSection(mem, ref info, DIB_RGB_COLORS, out var bits, 0, 0);
        var old = SelectObject(mem, bitmap);
        try
        {
            if (bitmap == 0 || !BitBlt(mem, 0, 0, w, h, screen, origin.X, origin.Y, SRCCOPY)) return null;
            var data = new byte[w * h * 4];
            Marshal.Copy(bits, data, 0, data.Length);
            var image = new PixelImage(w, h, data);
            if (IsBlack(image)) Warn("Bildschirmaufnahme ist schwarz – läuft das Spiel im exklusiven Vollbild oder mit HDR? Randlos-Modus wählen.");
            return image;
        }
        finally
        {
            SelectObject(mem, old);
            if (bitmap != 0) DeleteObject(bitmap);
            DeleteDC(mem);
            ReleaseDC(0, screen);
        }
    }

    private static bool IsBlack(PixelImage image)
    {
        long sum = 0;
        var n = 0;
        for (var y = 0; y < image.Height; y += 7)
        for (var x = 0; x < image.Width; x += 7) { sum += image.Luma(x, y); n++; }
        return n > 0 && sum / n < 3;
    }

    private void Warn(string text)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastWarning) < 60_000) return;
        Interlocked.Exchange(ref _lastWarning, now);
        log(text);
    }
}
