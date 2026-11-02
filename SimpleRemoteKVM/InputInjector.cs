using System;

namespace SimpleRemoteKVM
{
    /// <summary>
    /// Injeta eventos de mouse e teclado no sistema operacional local
    /// usando chamadas diretas de Win32 (SetCursorPos, mouse_event, keybd_event).
    /// Idêntico ao mecanismo de execução do Mouse without Borders.
    /// </summary>
    public class InputInjector
    {
        public void Inject(DATA packet)
        {
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
            // 1. Move o cursor físico para a coordenada exata
            NativeMethods.SetCursorPos(md.x, md.y);

            // 2. Executa botões de mouse ou scroll da roda, se existirem
            uint flags = (uint)md.dwFlags & ~(uint)NativeMethods.MOUSEEVENTF.MOVE;
            if (flags != 0 || md.wDelta != 0)
            {
                NativeMethods.mouse_event(flags, md.x, md.y, md.wDelta, UIntPtr.Zero);
            }
        }

        private void InjectKeyboard(KEYBDDATA kd)
        {
            // Sintetiza tecla de forma direta e compatível com todas as arquiteturas x86/x64
            NativeMethods.keybd_event((byte)kd.wVk, 0, (uint)kd.dwFlags, UIntPtr.Zero);
        }
    }
}
