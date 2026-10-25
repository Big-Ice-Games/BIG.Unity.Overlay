using UnityEngine;

#if UNITY_STANDALONE_WIN
using System;
using System.Runtime.InteropServices;
#endif

namespace BIG.Unity.Overlay
{
    /// <summary>
    /// Consolidated Win32 window interop. Coordinate convention notes:
    /// Win32 origin is the TOP-left corner of the primary monitor with Y going DOWN;
    /// UniWinC origin is the BOTTOM-left corner of the primary monitor with Y going UP.
    /// Work area = monitor minus taskbar. Monitors are resolved with MonitorFromPoint
    /// instead of enumeration callbacks — IL2CPP cannot marshal closure delegates to native code.
    /// </summary>
    internal static class NativeWindow
    {
#if UNITY_STANDALONE_WIN
        [StructLayout(LayoutKind.Sequential)]
        private struct WinRect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public WinRect Monitor;
            public WinRect Work;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinPoint
        {
            public int X, Y;
        }

        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int SM_CYSCREEN = 1;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(WinPoint pt, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        private const int GWL_STYLE = -16;
        private const long WS_POPUP = 0x80000000L;
        private const long WS_FRAME_BITS = 0x00C00000L /* CAPTION (= BORDER | DLGFRAME) */ | 0x00040000L /* THICKFRAME */
                                           | 0x00080000L /* SYSMENU */ | 0x00020000L /* MINIMIZEBOX */ | 0x00010000L /* MAXIMIZEBOX */;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_FRAMECHANGED = 0x0020;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int value);

        /// <summary>
        /// The player window. GetActiveWindow only answers while ours is the active window, so the
        /// handle is remembered from the first call that saw it (boot: ParkOffScreen).
        /// </summary>
        private static IntPtr s_window;

        private static IntPtr PlayerWindow
        {
            get
            {
                IntPtr active = GetActiveWindow();
                if (active != IntPtr.Zero) s_window = active;
                return s_window;
            }
        }

        /// <summary>
        /// Park the window far off-screen so the user never sees the flash of a not-yet-transparent window.
        /// </summary>
        public static void ParkOffScreen()
        {
            SetWindowPos(PlayerWindow, IntPtr.Zero, -32000, -32000, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        /// <summary>
        /// Emergency pull of a parked window back on screen with raw Win32,
        /// when the normal UniWinC path is unavailable.
        /// </summary>
        public static void EmergencyShow()
        {
            SetWindowPos(PlayerWindow, IntPtr.Zero, 100, 100, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        /// <summary>
        /// Strips the caption and the frame (WS_POPUP) — fullscreen mode: UniWinC restores the original
        /// decorated style when transparency goes OFF, which showed a Windows title bar over the game.
        /// Turning transparency back on re-applies UniWinC's own borderless style, so nothing undoes this.
        /// </summary>
        public static void MakeBorderless()
        {
            IntPtr window = PlayerWindow;
            if (window == IntPtr.Zero)
                return;

            if (IntPtr.Size == 8)
            {
                long style = GetWindowLongPtr64(window, GWL_STYLE).ToInt64();
                style = (style & ~WS_FRAME_BITS) | WS_POPUP;
                SetWindowLongPtr64(window, GWL_STYLE, new IntPtr(style));
            }
            else
            {
                long style = (uint)GetWindowLong32(window, GWL_STYLE);
                style = (style & ~WS_FRAME_BITS) | WS_POPUP;
                SetWindowLong32(window, GWL_STYLE, unchecked((int)style));
            }

            // Re-layout the non-client area so the client fills the whole window rect.
            SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        /// <summary>
        /// Work area (monitor minus taskbar) of the monitor whose rect (UniWinC coordinates) is given.
        /// </summary>
        public static bool TryGetWorkAreaForMonitor(Rect uniwinMonitorRect, out Rect workArea)
        {
            workArea = default;
            if (uniwinMonitorRect.width <= 0f || uniwinMonitorRect.height <= 0f)
                return false;

            int primaryHeight = GetSystemMetrics(SM_CYSCREEN);

            // Monitor center: UniWinC (Y up) -> Win32 (Y down).
            var center = new WinPoint
            {
                X = Mathf.RoundToInt(uniwinMonitorRect.center.x),
                Y = primaryHeight - Mathf.RoundToInt(uniwinMonitorRect.center.y),
            };

            IntPtr monitor = MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
                return false;

            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfoW(monitor, ref info))
                return false;

            workArea = Win32RectToUniWin(info.Work, primaryHeight);
            return true;
        }

        /// <summary> Work area of the primary monitor — Win32 point (0,0) always lies on it. </summary>
        public static bool TryGetPrimaryWorkArea(out Rect workArea)
        {
            int primaryHeight = GetSystemMetrics(SM_CYSCREEN);
            var origin = new WinPoint { X = 0, Y = 0 };

            IntPtr monitor = MonitorFromPoint(origin, MONITOR_DEFAULTTONEAREST);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };

            if (monitor != IntPtr.Zero && GetMonitorInfoW(monitor, ref info))
            {
                workArea = Win32RectToUniWin(info.Work, primaryHeight);
                return true;
            }

            workArea = default;
            return false;
        }

        private static Rect Win32RectToUniWin(WinRect rect, int primaryHeight)
        {
            return Rect.MinMaxRect(rect.Left, primaryHeight - rect.Bottom, rect.Right, primaryHeight - rect.Top);
        }
#else
        public static void ParkOffScreen() { }
        public static void MakeBorderless() { }
        public static void EmergencyShow() { }

        public static bool TryGetWorkAreaForMonitor(Rect uniwinMonitorRect, out Rect workArea)
        {
            workArea = default;
            return false;
        }

        public static bool TryGetPrimaryWorkArea(out Rect workArea)
        {
            workArea = default;
            return false;
        }
#endif
    }
}
