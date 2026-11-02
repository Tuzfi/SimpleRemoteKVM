using System;
using System.Runtime.InteropServices;

namespace SimpleRemoteKVM
{
    /// <summary>
    /// Injeta eventos remotos de mouse e teclado no sistema operacional local
    /// usando APIs Win32 SendInput e SetCursorPos.
    /// Otimizado para latência ultrabaixa com caminho direto para movimentos de mouse.
    /// Replicado a partir de MouseWithoutBorders.InputSimu.
    /// </summary>
    public class InputInjector
    {
        // Detecção de duplicatas para cliques e teclas: réplica do recentProcessedPackageIDs do MWB
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
            switch (packet.Type)
            {
                case PackageType.mouse:
                    // Otimização crítica de latência: movimentos puros de mouse não exigem lock de duplicatas
                    // pois SetCursorPos é uma atribuição direta de coordenadas no driver do Windows.
                    if ((packet.Md.dwFlags & ~(int)NativeMethods.MOUSEEVENTF.MOVE) == 0 && packet.Md.wDelta == 0)
                    {
                        NativeMethods.SetCursorPos(packet.Md.x, packet.Md.y);
                        return;
                    }

                    if (IsDuplicate(packet.Id)) return;
                    InjectMouse(packet.Md);
                    break;

                case PackageType.keybd:
                    if (IsDuplicate(packet.Id)) return;
                    InjectKeyboard(packet.Kd);
                    break;
            }
        }

        private void InjectMouse(MOUSEDATA md)
        {
            // 1. Move o cursor diretamente para a coordenada exata
            NativeMethods.SetCursorPos(md.x, md.y);

            // 2. Sintetiza cliques de botões ou movimento da roda do mouse
            uint flags = (uint)md.dwFlags;
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
