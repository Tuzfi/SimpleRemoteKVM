using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SimpleRemoteKVM
{
    /// <summary>
    /// Posição do monitor do segundo computador na mesa de trabalho.
    /// Exatamente como na matriz de máquinas do Microsoft Garage Mouse without Borders.
    /// </summary>
    public enum RemoteMonitorPosition
    {
        Right = 0,  // O PC Remoto fica à Direita do PC Local
        Left = 1,   // O PC Remoto fica à Esquerda do PC Local
        Top = 2,    // O PC Remoto fica Acima do PC Local
        Bottom = 3  // O PC Remoto fica Abaixo do PC Local
    }

    /// <summary>
    /// Gerencia ganchos globais de baixo nível do Windows (WH_MOUSE_LL e WH_KEYBOARD_LL)
    /// com controle de estado mútuo simétrico idêntico ao Mouse without Borders.
    /// </summary>
    public class InputHookManager : IDisposable
    {
        private IntPtr _mouseHookId = IntPtr.Zero;
        private IntPtr _keyboardHookId = IntPtr.Zero;

        // GC Prevention: Mantém referências fortes aos delegates
        private readonly NativeMethods.HookProc _mouseProc;
        private readonly NativeMethods.HookProc _keyboardProc;

        private int _packageSequence = 0;
        private bool _isDisposed = false;

        // Tecla de atalho padrão: ScrollLock (VK 0x91 / 145)
        public const int DEFAULT_TOGGLE_VK = 0x91;

        public int ToggleVirtualKey { get; set; }
        public bool IsRemoteActive { get; private set; }
        public bool IsBeingControlledByRemote { get; private set; }
        public bool IsConnected { get; set; }

        // Configuração dos Monitores
        public RemoteMonitorPosition MonitorPosition { get; set; }
        public bool EnableEdgeSwitching { get; set; }
        public int RemoteScreenWidth { get; set; }
        public int RemoteScreenHeight { get; set; }

        // Coordenadas virtuais no monitor remoto
        private int _remoteCursorX = 0;
        private int _remoteCursorY = 0;
        private Point _lastMousePt = Point.Empty;
        private volatile bool _isRecentering = false;
        private long _lastSwitchTick = 0;
        private const long SWITCH_COOLDOWN_TICKS = 4000000; // 400 ms de debounce para evitar trepidação

        public event Action<DATA> OnDataToSend;
        public event Action<bool> OnControlModeChanged;
        public event Action<bool> OnRemoteControlledChanged;

        public InputHookManager(int toggleKey = DEFAULT_TOGGLE_VK)
        {
            ToggleVirtualKey = toggleKey;
            MonitorPosition = RemoteMonitorPosition.Right;
            EnableEdgeSwitching = true;
            RemoteScreenWidth = 1920;
            RemoteScreenHeight = 1080;

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

                // 1. Instala Gancho de Mouse de Baixo Nível (WH_MOUSE_LL = 14)
                _mouseHookId = NativeMethods.SetWindowsHookEx(
                    NativeMethods.WH_MOUSE_LL,
                    _mouseProc,
                    hMod,
                    0);

                if (_mouseHookId == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException("Falha ao instalar gancho WH_MOUSE_LL. Erro: " + err);
                }

                // 2. Instala Gancho de Teclado de Baixo Nível (WH_KEYBOARD_LL = 13)
                _keyboardHookId = NativeMethods.SetWindowsHookEx(
                    NativeMethods.WH_KEYBOARD_LL,
                    _keyboardProc,
                    hMod,
                    0);

                if (_keyboardHookId == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    Stop();
                    throw new InvalidOperationException("Falha ao instalar gancho WH_KEYBOARD_LL. Erro: " + err);
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
            if (IsBeingControlledByRemote)
            {
                SetBeingControlledByRemote(false);
                SendSwitchPacket(false);
                return;
            }
            SetControlMode(!IsRemoteActive, true);
        }

        public void SetControlMode(bool remoteActive, bool centerRemoteCursor = false)
        {
            IsRemoteActive = remoteActive;
            _lastSwitchTick = DateTime.UtcNow.Ticks;

            if (IsRemoteActive)
            {
                IsBeingControlledByRemote = false;

                if (centerRemoteCursor)
                {
                    _remoteCursorX = RemoteScreenWidth / 2;
                    _remoteCursorY = RemoteScreenHeight / 2;
                }

                // Centraliza o cursor local de forma controlada para dar 360 graus de movimento livre
                Rectangle localBounds = Screen.PrimaryScreen.Bounds;
                Point center = new Point(localBounds.Width / 2, localBounds.Height / 2);
                _isRecentering = true;
                _lastMousePt = center;
                NativeMethods.SetCursorPos(center.X, center.Y);

                // Envia pacote inicial de movimento para posicionar o cursor no outro PC
                SendMouseData(_remoteCursorX, _remoteCursorY, 0, (int)NativeMethods.MOUSEEVENTF.MOVE);
            }

            SendSwitchPacket(IsRemoteActive);

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

        public void SendSwitchPacket(bool isRemote)
        {
            DATA switchPacket = new DATA
            {
                Type = PackageType.machineswitched,
                Id = isRemote ? 1 : 0,
                Timestamp = DateTime.UtcNow.Ticks,
                Md = new MOUSEDATA
                {
                    x = _remoteCursorX,
                    y = _remoteCursorY
                }
            };

            if (OnDataToSend != null)
            {
                OnDataToSend(switchPacket);
            }
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
            {
                return NativeMethods.CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
            }

            // CRÍTICO: Se esta máquina está sendo controlada pelo outro computador (modo passivo),
            // NÃO intercepta, NÃO calcula deltas e NÃO cancela o controle! Deixa o Windows mover normalmente.
            if (IsBeingControlledByRemote)
            {
                return NativeMethods.CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
            }

            NativeMethods.MouseLLHookStruct hookStruct = (NativeMethods.MouseLLHookStruct)
                Marshal.PtrToStructure(lParam, typeof(NativeMethods.MouseLLHookStruct));

            // Se o evento foi injetado por software (LLMHF_INJECTED = 1), deixa passar
            if ((hookStruct.flags & 1) != 0)
            {
                return NativeMethods.CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
            }

            int msg = wParam.ToInt32();

            // Se for o reposicionamento suave que nós mesmos acionamos para o centro:
            if (_isRecentering)
            {
                _isRecentering = false;
                _lastMousePt = new Point(hookStruct.pt.x, hookStruct.pt.y);
                return (IntPtr)1; // Consome sem disparar deltas
            }

            Rectangle localBounds = Screen.PrimaryScreen.Bounds;
            int localW = localBounds.Width;
            int localH = localBounds.Height;

            // -------------------------------------------------------------
            // CASO 1: MODO LOCAL — Operando normalmente o PC Local
            // -------------------------------------------------------------
            if (!IsRemoteActive)
            {
                if (EnableEdgeSwitching && IsConnected && (DateTime.UtcNow.Ticks - _lastSwitchTick > SWITCH_COOLDOWN_TICKS))
                {
                    bool hitEdge = false;
                    switch (MonitorPosition)
                    {
                        case RemoteMonitorPosition.Right:
                            if (hookStruct.pt.x >= localW - 1) hitEdge = true;
                            break;
                        case RemoteMonitorPosition.Left:
                            if (hookStruct.pt.x <= 0) hitEdge = true;
                            break;
                        case RemoteMonitorPosition.Top:
                            if (hookStruct.pt.y <= 0) hitEdge = true;
                            break;
                        case RemoteMonitorPosition.Bottom:
                            if (hookStruct.pt.y >= localH - 1) hitEdge = true;
                            break;
                    }

                    if (hitEdge)
                    {
                        // Transiciona para o PC Remoto
                        int entryX = RemoteScreenWidth / 2;
                        int entryY = RemoteScreenHeight / 2;

                        switch (MonitorPosition)
                        {
                            case RemoteMonitorPosition.Right:
                                entryX = 2;
                                entryY = (int)((long)hookStruct.pt.y * RemoteScreenHeight / localH);
                                break;
                            case RemoteMonitorPosition.Left:
                                entryX = RemoteScreenWidth - 3;
                                entryY = (int)((long)hookStruct.pt.y * RemoteScreenHeight / localH);
                                break;
                            case RemoteMonitorPosition.Top:
                                entryX = (int)((long)hookStruct.pt.x * RemoteScreenWidth / localW);
                                entryY = RemoteScreenHeight - 3;
                                break;
                            case RemoteMonitorPosition.Bottom:
                                entryX = (int)((long)hookStruct.pt.x * RemoteScreenWidth / localW);
                                entryY = 2;
                                break;
                        }

                        entryX = Math.Max(0, Math.Min(RemoteScreenWidth - 1, entryX));
                        entryY = Math.Max(0, Math.Min(RemoteScreenHeight - 1, entryY));

                        _remoteCursorX = entryX;
                        _remoteCursorY = entryY;

                        SetControlMode(true, false);

                        return (IntPtr)1; // Consome para não clicar na borda da tela local
                    }
                }

                // Normal: deixa o Windows processar o mouse local normalmente
                return NativeMethods.CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
            }

            // -------------------------------------------------------------
            // CASO 2: MODO REMOTO — Operando a tela do outro PC
            // -------------------------------------------------------------
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
                    delta = (short)((hookStruct.mouseData >> 16) & 0xFFFF);
                    break;
            }

            if (msg == NativeMethods.WM_MOUSEMOVE)
            {
                int dx = hookStruct.pt.x - _lastMousePt.X;
                int dy = hookStruct.pt.y - _lastMousePt.Y;
                _lastMousePt = new Point(hookStruct.pt.x, hookStruct.pt.y);

                if (dx != 0 || dy != 0)
                {
                    _remoteCursorX += dx;
                    _remoteCursorY += dy;

                    // Verifica se o usuário puxou o cursor de volta para o PC Local
                    bool returnToLocal = false;
                    if (EnableEdgeSwitching && (DateTime.UtcNow.Ticks - _lastSwitchTick > SWITCH_COOLDOWN_TICKS))
                    {
                        switch (MonitorPosition)
                        {
                            case RemoteMonitorPosition.Right:
                                if (_remoteCursorX < 0) returnToLocal = true;
                                break;
                            case RemoteMonitorPosition.Left:
                                if (_remoteCursorX >= RemoteScreenWidth) returnToLocal = true;
                                break;
                            case RemoteMonitorPosition.Top:
                                if (_remoteCursorY >= RemoteScreenHeight) returnToLocal = true;
                                break;
                            case RemoteMonitorPosition.Bottom:
                                if (_remoteCursorY < 0) returnToLocal = true;
                                break;
                        }
                    }

                    if (returnToLocal)
                    {
                        // Retorna o controle para o PC Local
                        SetControlMode(false, false);

                        // Posiciona o cursor físico na borda de retorno da tela local
                        int returnX = localW / 2;
                        int returnY = localH / 2;

                        switch (MonitorPosition)
                        {
                            case RemoteMonitorPosition.Right:
                                returnX = localW - 4;
                                returnY = (int)((long)_remoteCursorY * localH / RemoteScreenHeight);
                                break;
                            case RemoteMonitorPosition.Left:
                                returnX = 4;
                                returnY = (int)((long)_remoteCursorY * localH / RemoteScreenHeight);
                                break;
                            case RemoteMonitorPosition.Top:
                                returnX = (int)((long)_remoteCursorX * localW / RemoteScreenWidth);
                                returnY = 4;
                                break;
                            case RemoteMonitorPosition.Bottom:
                                returnX = (int)((long)_remoteCursorX * localW / RemoteScreenWidth);
                                returnY = localH - 4;
                                break;
                        }

                        returnX = Math.Max(2, Math.Min(localW - 3, returnX));
                        returnY = Math.Max(2, Math.Min(localH - 3, returnY));

                        NativeMethods.SetCursorPos(returnX, returnY);
                        return (IntPtr)1;
                    }

                    // Mantém nos limites da tela remota
                    _remoteCursorX = Math.Max(0, Math.Min(RemoteScreenWidth - 1, _remoteCursorX));
                    _remoteCursorY = Math.Max(0, Math.Min(RemoteScreenHeight - 1, _remoteCursorY));

                    // Envia coordenada ao PC remoto
                    SendMouseData(_remoteCursorX, _remoteCursorY, 0, (int)NativeMethods.MOUSEEVENTF.MOVE);

                    // Se o mouse físico estiver muito próximo das bordas físicas locais,
                    // centraliza suavemente para que o usuário não fique sem espaço de movimento
                    if (hookStruct.pt.x <= 20 || hookStruct.pt.x >= localW - 20 ||
                        hookStruct.pt.y <= 20 || hookStruct.pt.y >= localH - 20)
                    {
                        Point center = new Point(localW / 2, localH / 2);
                        _isRecentering = true;
                        _lastMousePt = center;
                        NativeMethods.SetCursorPos(center.X, center.Y);
                    }
                }

                return (IntPtr)1; // Suprime o movimento na tela local
            }
            else if (dwFlags != 0)
            {
                // Cliques ou roda do mouse
                SendMouseData(_remoteCursorX, _remoteCursorY, delta, (int)dwFlags);
                return (IntPtr)1; // Suprime na tela local
            }

            return (IntPtr)1;
        }

        private void SendMouseData(int x, int y, int delta, int dwFlags)
        {
            DATA data = new DATA
            {
                Type = PackageType.mouse,
                Id = Interlocked.Increment(ref _packageSequence),
                Timestamp = DateTime.UtcNow.Ticks,
                Md = new MOUSEDATA
                {
                    x = x,
                    y = y,
                    wDelta = delta,
                    dwFlags = dwFlags
                }
            };

            if (OnDataToSend != null)
            {
                OnDataToSend(data);
            }
        }

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                NativeMethods.KeyboardHookStruct hookStruct = (NativeMethods.KeyboardHookStruct)
                    Marshal.PtrToStructure(lParam, typeof(NativeMethods.KeyboardHookStruct));

                // Se a tecla foi sintetizada por software (LLKHF_INJECTED = 0x10), deixa passar
                if ((hookStruct.flags & 0x10) != 0)
                {
                    return NativeMethods.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
                }

                bool isKeyUp = (msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP);
                int vk = hookStruct.vkCode;

                // Se o PC estava sendo controlado e o usuário apertou ScrollLock, retoma o controle intencionalmente
                if (IsBeingControlledByRemote)
                {
                    if (vk == ToggleVirtualKey && isKeyUp)
                    {
                        SetBeingControlledByRemote(false);
                        SendSwitchPacket(false);
                        return (IntPtr)1;
                    }
                    return NativeMethods.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
                }

                // Tecla de Alternância Manual (ScrollLock / F12 / etc.)
                if (vk == ToggleVirtualKey)
                {
                    if (isKeyUp)
                    {
                        ToggleRemoteMode();
                    }
                    return (IntPtr)1;
                }

                // Se estiver no Modo Remoto, envia a tecla ao outro PC e suprime localmente
                if (IsRemoteActive)
                {
                    uint dwFlags = isKeyUp ? (uint)NativeMethods.KEYEVENTF.KEYUP : (uint)NativeMethods.KEYEVENTF.KEYDOWN;
                    if ((hookStruct.flags & 1) != 0)
                    {
                        dwFlags |= (uint)NativeMethods.KEYEVENTF.EXTENDEDKEY;
                    }

                    DATA data = new DATA
                    {
                        Type = PackageType.keybd,
                        Id = Interlocked.Increment(ref _packageSequence),
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

                    return (IntPtr)1; // Suprime tecla na janela local
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
