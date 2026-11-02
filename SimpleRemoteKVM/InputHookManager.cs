using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SimpleRemoteKVM
{
    /// <summary>
    /// Manages low-level Windows hooks (WH_MOUSE_LL and WH_KEYBOARD_LL) to capture
    /// local input events and redirect them over the network.
    /// Replicates the core mechanism of MouseWithoutBorders.InputHook.
    /// </summary>
    public class InputHookManager : IDisposable
    {
        private IntPtr _mouseHookId = IntPtr.Zero;
        private IntPtr _keyboardHookId = IntPtr.Zero;

        // GC Prevention: Must hold strong references to delegates as long as hooks are installed!
        private readonly NativeMethods.HookProc _mouseProc;
        private readonly NativeMethods.HookProc _keyboardProc;

        private int _packageSequence = 0;
        private bool _isDisposed = false;

        // Default toggle key: ScrollLock (VK 0x91 / 145) or F12 (VK 0x7B / 123)
        public const int DEFAULT_TOGGLE_VK = 0x91; // VK_SCROLL (ScrollLock)

        public int ToggleVirtualKey { get; set; }
        public bool IsRemoteActive { get; private set; }
        public bool IsBeingControlledByRemote { get; set; }

        public event Action<DATA> OnDataToSend;
        public event Action<bool> OnControlModeChanged;
        public event Action<bool> OnRemoteControlledChanged;

        public InputHookManager(int toggleKey = DEFAULT_TOGGLE_VK)
        {
            ToggleVirtualKey = toggleKey;
            _mouseProc = MouseHookCallback;
            _keyboardProc = KeyboardHookCallback;
        }

        public void Start()
        {
            if (_mouseHookId != IntPtr.Zero || _keyboardHookId != IntPtr.Zero)
            {
                return;
            }

            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                IntPtr hMod = NativeMethods.GetModuleHandle(curModule.ModuleName);

                // 1. Install Low-Level Mouse Hook (WH_MOUSE_LL = 14)
                _mouseHookId = NativeMethods.SetWindowsHookEx(
                    NativeMethods.WH_MOUSE_LL,
                    _mouseProc,
                    hMod,
                    0);

                if (_mouseHookId == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException("Failed to install WH_MOUSE_LL hook. Win32 Error: " + err);
                }

                // 2. Install Low-Level Keyboard Hook (WH_KEYBOARD_LL = 13)
                _keyboardHookId = NativeMethods.SetWindowsHookEx(
                    NativeMethods.WH_KEYBOARD_LL,
                    _keyboardProc,
                    hMod,
                    0);

                if (_keyboardHookId == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    Stop();
                    throw new InvalidOperationException("Failed to install WH_KEYBOARD_LL hook. Win32 Error: " + err);
                }
            }
        }

        public void Stop()
        {
            if (_mouseHookId != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_mouseHookId);
                _mouseHookId = IntPtr.Zero;
            }

            if (_keyboardHookId != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_keyboardHookId);
                _keyboardHookId = IntPtr.Zero;
            }

            IsRemoteActive = false;
        }

        public void ToggleRemoteMode()
        {
            IsRemoteActive = !IsRemoteActive;
            if (IsRemoteActive)
            {
                IsBeingControlledByRemote = false;
            }

            // Envia pacote machineswitched (77) para sincronizar o status mútuo com o outro PC
            DATA switchPacket = new DATA
            {
                Type = PackageType.machineswitched,
                Id = IsRemoteActive ? 1 : 0,
                Timestamp = DateTime.UtcNow.Ticks
            };

            if (OnDataToSend != null)
            {
                OnDataToSend(switchPacket);
            }

            if (OnControlModeChanged != null)
            {
                OnControlModeChanged(IsRemoteActive);
            }
        }

        public void SetBeingControlledByRemote(bool isControlled)
        {
            IsBeingControlledByRemote = isControlled;
            if (isControlled)
            {
                IsRemoteActive = false;
            }

            if (OnRemoteControlledChanged != null)
            {
                OnRemoteControlledChanged(isControlled);
            }
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && IsRemoteActive)
            {
                int msg = wParam.ToInt32();
                NativeMethods.MouseLLHookStruct hookStruct = (NativeMethods.MouseLLHookStruct)
                    Marshal.PtrToStructure(lParam, typeof(NativeMethods.MouseLLHookStruct));

                uint dwFlags = 0;
                int delta = 0;

                switch (msg)
                {
                    case NativeMethods.WM_MOUSEMOVE:
                        dwFlags = (uint)NativeMethods.MOUSEEVENTF.MOVE;
                        break;
                    case NativeMethods.WM_LBUTTONDOWN:
                        dwFlags = (uint)NativeMethods.MOUSEEVENTF.LEFTDOWN;
                        break;
                    case NativeMethods.WM_LBUTTONUP:
                        dwFlags = (uint)NativeMethods.MOUSEEVENTF.LEFTUP;
                        break;
                    case NativeMethods.WM_RBUTTONDOWN:
                        dwFlags = (uint)NativeMethods.MOUSEEVENTF.RIGHTDOWN;
                        break;
                    case NativeMethods.WM_RBUTTONUP:
                        dwFlags = (uint)NativeMethods.MOUSEEVENTF.RIGHTUP;
                        break;
                    case NativeMethods.WM_MBUTTONDOWN:
                        dwFlags = (uint)NativeMethods.MOUSEEVENTF.MIDDLEDOWN;
                        break;
                    case NativeMethods.WM_MBUTTONUP:
                        dwFlags = (uint)NativeMethods.MOUSEEVENTF.MIDDLEUP;
                        break;
                    case NativeMethods.WM_MOUSEWHEEL:
                        dwFlags = (uint)NativeMethods.MOUSEEVENTF.WHEEL;
                        // High-order word of mouseData contains wheel delta
                        delta = (short)((hookStruct.mouseData >> 16) & 0xFFFF);
                        break;
                }

                if (dwFlags != 0)
                {
                    DATA data = new DATA
                    {
                        Type = PackageType.mouse,
                        Id = System.Threading.Interlocked.Increment(ref _packageSequence),
                        Timestamp = DateTime.UtcNow.Ticks,
                        Md = new MOUSEDATA
                        {
                            x = hookStruct.pt.x,
                            y = hookStruct.pt.y,
                            wDelta = delta,
                            dwFlags = (int)dwFlags
                        }
                    };

                    if (OnDataToSend != null)
                    {
                        OnDataToSend(data);
                    }

                    // Suppress mouse click events on the local desktop while controlling the remote machine
                    if (msg != NativeMethods.WM_MOUSEMOVE)
                    {
                        return (IntPtr)1;
                    }
                }
            }

            return NativeMethods.CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
        }

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                NativeMethods.KeyboardHookStruct hookStruct = (NativeMethods.KeyboardHookStruct)
                    Marshal.PtrToStructure(lParam, typeof(NativeMethods.KeyboardHookStruct));

                bool isKeyUp = (msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP);
                int vk = hookStruct.vkCode;

                // Handle Mode Toggle Key
                if (vk == ToggleVirtualKey)
                {
                    if (isKeyUp)
                    {
                        ToggleRemoteMode();
                    }
                    return (IntPtr)1; // Consume toggle key locally
                }

                // If in Remote Mode, transmit keystroke and suppress locally
                if (IsRemoteActive)
                {
                    uint dwFlags = isKeyUp ? (uint)NativeMethods.KEYEVENTF.KEYUP : (uint)NativeMethods.KEYEVENTF.KEYDOWN;
                    
                    // Check for extended keys (e.g. arrow keys, right Alt/Ctrl)
                    if ((hookStruct.flags & 1) != 0)
                    {
                        dwFlags |= (uint)NativeMethods.KEYEVENTF.EXTENDEDKEY;
                    }

                    DATA data = new DATA
                    {
                        Type = PackageType.keybd,
                        Id = System.Threading.Interlocked.Increment(ref _packageSequence),
                        Timestamp = DateTime.UtcNow.Ticks,
                        Kd = new KEYBDDATA
                        {
                            wVk = vk,
                            dwFlags = (int)dwFlags
                        }
                    };

                    if (OnDataToSend != null)
                    {
                        OnDataToSend(data);
                    }

                    return (IntPtr)1; // Suppress keystroke from executing on local window
                }
            }

            return NativeMethods.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                Stop();
                _isDisposed = true;
            }
        }
    }
}
