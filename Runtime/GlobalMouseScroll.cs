using UnityEngine;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using UnityEngine.InputSystem;
#endif

namespace BIG.Unity.Overlay
{
    /// <summary>
    /// Global mouse wheel reader that works also when the game window has no focus.
    ///
    /// Windows delivers mouse events only to the focused window, so an overlay normally never sees
    /// the wheel without being clicked first. We register the mouse in Raw Input with RIDEV_INPUTSINK
    /// (WM_INPUT delivered to our window also in background) and read from a HYBRID of two sources:
    /// - window focused   → Input System's Mouse.current (100% of ticks, no queue contention),
    /// - window unfocused → our own WndProc subclass counting the delta straight from WM_INPUT
    ///   (Unity's input backend sleeps without focus, so the event-queue contention that normally
    ///   rules out a WndProc subclass does not occur).
    /// The WndProc counter is drained on every read and discarded while focused, so ticks are never counted twice.
    /// Deliberately NOT a WH_MOUSE_LL hook — that sits in the system-wide mouse delivery path
    /// and degrades mouse responsiveness across all of Windows.
    /// </summary>
    public static class GlobalMouseScroll
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
        private const ushort HID_USAGE_GENERIC_MOUSE = 0x02;
        private const uint RIDEV_INPUTSINK = 0x00000100;

        private const uint WM_INPUT = 0x00FF;
        private const uint RID_INPUT = 0x10000003;
        private const int RIM_TYPEMOUSE = 0;
        private const ushort RI_MOUSE_WHEEL = 0x0400;
        private const int GWLP_WNDPROC = -4;

        // RAWINPUT buffer offsets for x64: RAWINPUTHEADER is 24 bytes
        // (dwType 4 + dwSize 4 + hDevice 8 + wParam 8), then RAWMOUSE:
        // usFlags(2) + padding(2) + usButtonFlags(2) + usButtonData(2) + ...
        private const int OFFSET_DWTYPE = 0;
        private const int OFFSET_BUTTON_FLAGS = 24 + 4;
        private const int OFFSET_BUTTON_DATA = 24 + 6;
        private const uint RAWINPUT_HEADER_SIZE = 24;
        private const int RAWINPUT_BUFFER_SIZE = 128;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort UsagePage;
            public ushort Usage;
            public uint Flags;
            public IntPtr Target;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] devices, uint numDevices, uint size);

        [DllImport("user32.dll")]
        private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProcW(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private static IntPtr _window = IntPtr.Zero;
        private static IntPtr _originalWndProc = IntPtr.Zero;
        private static WndProcDelegate _wndProc; // Keep the reference — GC must not collect a delegate used natively.
        private static IntPtr _rawInputBuffer = IntPtr.Zero;
        private static int _accumulatedDelta;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            _window = GetActiveWindow();
            if (_window == IntPtr.Zero)
            {
                Debug.LogWarning("[BIG] GlobalMouseScroll | No window handle — scroll will work only while the window is focused.");
                return;
            }

            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;

            RegisterSink();

            // Backup source for the unfocused time: wheel delta straight from WM_INPUT.
            _rawInputBuffer = Marshal.AllocHGlobal(RAWINPUT_BUFFER_SIZE);
            _wndProc = WndProc;
            _originalWndProc = SetWindowLongPtrW(_window, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProc));

            Application.quitting += Uninstall;

            // Unity can re-register raw input on focus changes (without INPUTSINK),
            // overwriting our registration — refresh it.
            Application.focusChanged += _ => RegisterSink();
        }

        private static void Uninstall()
        {
            if (_originalWndProc != IntPtr.Zero)
            {
                SetWindowLongPtrW(_window, GWLP_WNDPROC, _originalWndProc);
                _originalWndProc = IntPtr.Zero;
            }

            if (_rawInputBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_rawInputBuffer);
                _rawInputBuffer = IntPtr.Zero;
            }
        }

        [AOT.MonoPInvokeCallback(typeof(WndProcDelegate))]
        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_INPUT && _rawInputBuffer != IntPtr.Zero)
            {
                uint size = RAWINPUT_BUFFER_SIZE;
                uint read = GetRawInputData(lParam, RID_INPUT, _rawInputBuffer, ref size, RAWINPUT_HEADER_SIZE);

                if (read != unchecked((uint)-1)
                    && Marshal.ReadInt32(_rawInputBuffer, OFFSET_DWTYPE) == RIM_TYPEMOUSE
                    && (Marshal.ReadInt16(_rawInputBuffer, OFFSET_BUTTON_FLAGS) & RI_MOUSE_WHEEL) != 0)
                {
                    short delta = Marshal.ReadInt16(_rawInputBuffer, OFFSET_BUTTON_DATA);
                    System.Threading.Interlocked.Add(ref _accumulatedDelta, delta);
                }
            }

            return CallWindowProcW(_originalWndProc, hWnd, msg, wParam, lParam);
        }

        private static void RegisterSink()
        {
            var devices = new[]
            {
                new RAWINPUTDEVICE
                {
                    UsagePage = HID_USAGE_PAGE_GENERIC,
                    Usage = HID_USAGE_GENERIC_MOUSE,
                    Flags = RIDEV_INPUTSINK,
                    Target = _window,
                },
            };

            if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
                Debug.LogWarning("[BIG] GlobalMouseScroll | RegisterRawInputDevices failed — scroll will work only while the window is focused.");
        }

        /// <summary>
        /// Wheel delta in "notches" for the current frame (positive = up).
        /// Focused → Input System; unfocused → our WM_INPUT counter.
        /// </summary>
        public static float ConsumeNotches()
        {
            // Drain the WndProc counter ALWAYS — while focused the same ticks are seen by the Input System,
            // so our backlog is discarded (otherwise ticks would be counted twice).
            int rawDelta = System.Threading.Interlocked.Exchange(ref _accumulatedDelta, 0);

            if (!Application.isFocused)
                return rawDelta / 120f;

            var mouse = Mouse.current;
            if (mouse == null)
                return 0f;

            float value = mouse.scroll.ReadValue().y;

            // Windows reports 120 per notch; the Input System does not always normalize it.
            if (Mathf.Abs(value) >= 60f)
                value /= 120f;

            return value;
        }
#else
        public static float ConsumeNotches()
        {
            return Input.mouseScrollDelta.y;
        }
#endif
    }
}
