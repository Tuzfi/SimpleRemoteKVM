using System;
using System.Runtime.InteropServices;

namespace SimpleRemoteKVM
{
    /// <summary>
    /// Injects received remote mouse and keyboard events into the local operating system
    /// using Win32 SendInput and SetCursorPos.
    /// Replicates the injection routines from MouseWithoutBorders.InputSimu.
    /// </summary>
    public class InputInjector
    {
        // Duplicate detection: replicates MWB's recentProcessedPackageIDs circular buffer
        private const int QUEUE_SIZE = 50;
        private readonly int[] _recentProcessedPackageIDs = new int[QUEUE_SIZE];
        private int _recentPackageIndex = 0;
        private readonly object _duplicateLock = new object();

        private readonly int _inputStructSize;

        public InputInjector()
        {
            _inputStructSize = Marshal.SizeOf(typeof(NativeMethods.INPUT));
        }

        public bool IsDuplicate(int packageId)
        {
            if (packageId == 0) return false;

            lock (_duplicateLock)
            {
                for (int i = 0; i < QUEUE_SIZE; i++)
                {
                    if (_recentProcessedPackageIDs[i] == packageId)
                    {
                        return true;
                    }
                }

                _recentProcessedPackageIDs[_recentPackageIndex] = packageId;
                _recentPackageIndex = (_recentPackageIndex + 1) % QUEUE_SIZE;
                return false;
            }
        }

        public void Inject(DATA packet)
        {
            // Discard out-of-order duplicates
            if (IsDuplicate(packet.Id))
            {
                return;
            }

            switch (packet.Type)
            {
                case PackageType.mouse:
                    InjectMouse(packet.Md);
                    break;

                case PackageType.keybd:
                    InjectKeyboard(packet.Kd);
                    break;
            }
        }

        private void InjectMouse(MOUSEDATA md)
        {
            // 1. Move cursor directly to exact screen coordinates
            NativeMethods.SetCursorPos(md.x, md.y);

            // 2. Synthesize mouse button or wheel actions if flags contain clicks/scroll
            uint flags = (uint)md.dwFlags;
            
            // Remove pure MOVE flag since SetCursorPos already positioned the pointer accurately
            flags &= ~(uint)NativeMethods.MOUSEEVENTF.MOVE;

            if (flags != 0)
            {
                NativeMethods.INPUT input = new NativeMethods.INPUT
                {
                    type = NativeMethods.INPUT_MOUSE,
                    u = new NativeMethods.InputUnion
                    {
                        mi = new NativeMethods.MOUSEINPUT
                        {
                            dx = md.x,
                            dy = md.y,
                            mouseData = md.wDelta,
                            dwFlags = flags,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    }
                };

                NativeMethods.SendInput(1, new NativeMethods.INPUT[] { input }, _inputStructSize);
            }
        }

        private void InjectKeyboard(KEYBDDATA kd)
        {
            NativeMethods.INPUT input = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.InputUnion
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = (ushort)kd.wVk,
                        wScan = 0,
                        dwFlags = (uint)kd.dwFlags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            NativeMethods.SendInput(1, new NativeMethods.INPUT[] { input }, _inputStructSize);
        }
    }
}
