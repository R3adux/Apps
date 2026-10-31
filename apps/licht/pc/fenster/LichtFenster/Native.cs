using System;
using System.Runtime.InteropServices;

namespace LichtFenster
{
    /// <summary>The few Windows functions WPF does not offer.</summary>
    internal static class Native
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private const byte VK_MENU = 0x12;
        private const uint KEYEVENTF_KEYUP = 0x2;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // Windows 10 before 20H1
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        /// <summary>
        /// Brings a window to the front. Windows lets a background program do that only right after user input;
        /// when Licht is asked from elsewhere (second start of Licht.exe) a short Alt press counts as such input.
        /// </summary>
        public static void BringToFront(IntPtr hwnd)
        {
            if (GetForegroundWindow() == hwnd || SetForegroundWindow(hwnd)) return;
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            SetForegroundWindow(hwnd);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        /// <summary>Dark title bar when Windows is in dark mode, like other apps.</summary>
        public static void UseDarkTitleBar(IntPtr hwnd, bool dark)
        {
            if (hwnd == IntPtr.Zero) return;
            var on = dark ? 1 : 0;
            try
            {
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));
            }
            catch (Exception)
            {
                // only looks
            }
        }
    }
}
