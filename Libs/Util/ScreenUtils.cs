using System;
using System.Runtime.InteropServices;

namespace Util
{
    public class ScreenUtils
    {
        public static bool IsForegroundWindowFullScreen()
        {
            const int MONITOR_DEFAULTTOPRIMARY = 1;

            var hWnd = GetForegroundWindow();
            if (hWnd == GetDesktopWindow() || hWnd == GetShellWindow())
            {
                return false;
            }

            GetWindowRect(hWnd, out Rect rect);

            var mi = new MonitorInfoEx();
            mi.cbSize = Marshal.SizeOf(mi);
            GetMonitorInfoEx(MonitorFromWindow(hWnd, MONITOR_DEFAULTTOPRIMARY), ref mi);

            int windowHeight = rect.Right - rect.Left;
            int windowWidth = rect.Bottom - rect.Top;

            int monitorHeight = mi.rcMonitor.Right - mi.rcMonitor.Left;
            int monitorWidth = mi.rcMonitor.Bottom - mi.rcMonitor.Top;

            bool fullScreen = (windowHeight == monitorHeight) && (windowWidth == monitorWidth);

            return fullScreen;
        }

        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("User32")]
        internal static extern IntPtr MonitorFromWindow(IntPtr hWnd, int dwFlags);

        [DllImport("user32", EntryPoint = "GetMonitorInfo", CharSet = CharSet.Auto, SetLastError = true)]
        internal static extern bool GetMonitorInfoEx(IntPtr hMonitor, ref MonitorInfoEx lpmi);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public UInt32 dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDeviceName;
    }
}
