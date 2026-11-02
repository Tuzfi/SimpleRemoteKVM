using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SimpleRemoteKVM
{
    /// <summary>
    /// Posição relativa do monitor do segundo computador na mesa de trabalho.
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
    /// Gerencia ganchos de baixo nível do Windows (WH_MOUSE_LL e WH_KEYBOARD_LL) para capturar
    /// eventos de entrada locais, detectar transições de borda de monitor e redirecioná-los
    /// em tempo real pela rede LAN.
    /// Réplica do mecanismo de controle e transição do MouseWithoutBorders.InputHook.
    /// </summary>
    public class InputHookManager : IDisposable
    {
        private IntPtr _mouseHookId = IntPtr.Zero;
        private IntPtr _keyboardHookId = IntPtr.Zero;

        // GC Prevention: Mantém referências fortes aos delegates durante todo o ciclo de vida dos ganchos
        private readonly NativeMethods.HookProc _mouseProc;
        private readonly NativeMethods.HookProc _keyboardProc;

        private int _packageSequence = 0;
        private bool _isDisposed = false;

        // Tecla de alternância padrão: ScrollLock (VK 0x91 / 145)
        public const int DEFAULT_TOGGLE_VK = 0x91;

        public int ToggleVirtualKey { get; set; }
        public bool IsRemoteActive { get; private set; }
        public bool IsBeingControlledByRemote { get; set; }
        public bool IsConnected { get; set; }

        // Configurações do Monitor Remoto
        public RemoteMonitorPosition MonitorPosition { get; set; }
        public bool EnableEdgeSwitching { get; set; }
        public int RemoteScreenWidth { get; set; }
        public int RemoteScreenHeight { get; set; }

        // Coordenadas virtuais e gerenciamento de transição contínua
        private int _virtualRemoteX = 0;
        private int _virtualRemoteY = 0;
        private Point _lastParkPoint = Point.Empty;
        private volatile bool _isParkMove = false;
        private long _lastSwitchTick = 0;
        private const long SWITCH_COOLDOWN_TICKS = 3000000; // 300 ms de debounce para evitar oscilação rápida na borda

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
                    throw new InvalidOperationException("Falha ao instalar gancho WH_MOUSE_LL. Erro Win32: " + err);
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
                    throw new InvalidOperationException("Falha ao instalar gancho WH_KEYBOARD_LL. Erro Win32: " + err);
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

        /// <summary>
        /// Alterna manualmente o modo de controle entre Local e Remoto (via botão ou tecla ScrollLock).
        /// </summary>
        public void ToggleRemoteMode()
        {
            SetControlMode(!IsRemoteActive, true);
        }

        public void SetControlMode(bool remoteActive, bool centerRemoteCursor = false)
        {
            IsRemoteActive = remoteActive;
            _lastSwitchTick = DateTime.UtcNow.Ticks;

            Rectangle localBounds = Screen.PrimaryScreen.Bounds;

            if (IsRemoteActive)
            {
                IsBeingControlledByRemote = false;

                if (centerRemoteCursor)
                {
                    _virtualRemoteX = RemoteScreenWidth / 2;
                    _virtualRemoteY = RemoteScreenHeight / 2;
                }

                // Centraliza e estaciona o cursor local para garantir movimento infinito sem trombar nas bordas físicas
                _isParkMove = true;
                _lastParkPoint = new Point(localBounds.Width / 2, localBounds.Height / 2);
                NativeMethods.SetCursorPos(_lastParkPoint.X, _lastParkPoint.Y);
                _isParkMove = false;

                // Envia pacote inicial de movimento para posicionar o ponteiro no PC remoto
                SendMouseData(_virtualRemoteX, _virtualRemoteY, 0, (int)NativeMethods.MOUSEEVENTF.MOVE);
            }

            // Envia pacote machineswitched (77) para sincronizar o status mútuo com o outro PC
            DATA switchPacket = new DATA
            {
                Type = PackageType.machineswitched,
                Id = IsRemoteActive ? 1 : 0,
                Timestamp = DateTime.UtcNow.Ticks,
                Md = new MOUSEDATA
                {
                    x = _virtualRemoteX,
                    y = _virtualRemoteY
                }
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

        /// <summary>
        /// Callback do Gancho de Mouse de Baixo Nível (WH_MOUSE_LL).
        /// </summary>
        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && !IsBeingControlledByRemote)
            {
                int msg = wParam.ToInt32();
                NativeMethods.MouseLLHookStruct hookStruct = (NativeMethods.MouseLLHookStruct)
                    Marshal.PtrToStructure(lParam, typeof(NativeMethods.MouseLLHookStruct));

                // Se o movimento foi gerado pelo nosso próprio auto-reestacionamento central, ignora
                if (_isParkMove)
                {
                    return (IntPtr)1;
                }

                Rectangle localBounds = Screen.PrimaryScreen.Bounds;
                int localW = localBounds.Width;
                int localH = localBounds.Height;

                // -------------------------------------------------------------
                // CASO 1: MODO LOCAL — Detecta se o mouse bateu na borda do monitor
                // -------------------------------------------------------------
                if (!IsRemoteActive)
                {
                    if (EnableEdgeSwitching && IsConnected)
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

                        // Se atingiu a borda e passou o intervalo mínimo de debounce (evita oscilação)
                        if (hitEdge && (DateTime.UtcNow.Ticks - _lastSwitchTick > SWITCH_COOLDOWN_TICKS))
                        {
                            // Calcula onde o cursor deve entrar na tela remota com mapeamento proporcional
                            switch (MonitorPosition)
                            {
                                case RemoteMonitorPosition.Right:
                                    _virtualRemoteX = 2;
                                    _virtualRemoteY = (int)((long)hookStruct.pt.y * RemoteScreenHeight / localH);
                                    break;
                                case RemoteMonitorPosition.Left:
                                    _virtualRemoteX = RemoteScreenWidth - 3;
                                    _virtualRemoteY = (int)((long)hookStruct.pt.y * RemoteScreenHeight / localH);
                                    break;
                                case RemoteMonitorPosition.Top:
                                    _virtualRemoteX = (int)((long)hookStruct.pt.x * RemoteScreenWidth / localW);
                                    _virtualRemoteY = RemoteScreenHeight - 3;
                                    break;
                                case RemoteMonitorPosition.Bottom:
                                    _virtualRemoteX = (int)((long)hookStruct.pt.x * RemoteScreenWidth / localW);
                                    _virtualRemoteY = 2;
                                    break;
                            }

                            _virtualRemoteX = Math.Max(0, Math.Min(RemoteScreenWidth - 1, _virtualRemoteX));
                            _virtualRemoteY = Math.Max(0, Math.Min(RemoteScreenHeight - 1, _virtualRemoteY));

                            // Transiciona para o modo remoto
                            SetControlMode(true, false);

                            return (IntPtr)1; // Consome o evento para não clicar na borda da tela local
                        }
                    }
                }
                // -------------------------------------------------------------
                // CASO 2: MODO REMOTO — Calcula deltas relativos e gerencia retorno
                // -------------------------------------------------------------
                else
                {
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
                        int dx = hookStruct.pt.x - _lastParkPoint.X;
                        int dy = hookStruct.pt.y - _lastParkPoint.Y;

                        if (dx == 0 && dy == 0)
                        {
                            return (IntPtr)1;
                        }

                        _virtualRemoteX += dx;
                        _virtualRemoteY += dy;

                        // Verifica se o usuário arrastou o mouse de volta para a tela local
                        bool returnToLocal = false;
                        if (EnableEdgeSwitching && (DateTime.UtcNow.Ticks - _lastSwitchTick > SWITCH_COOLDOWN_TICKS))
                        {
                            switch (MonitorPosition)
                            {
                                case RemoteMonitorPosition.Right:
                                    if (_virtualRemoteX < 0) returnToLocal = true;
                                    break;
                                case RemoteMonitorPosition.Left:
                                    if (_virtualRemoteX >= RemoteScreenWidth) returnToLocal = true;
                                    break;
                                case RemoteMonitorPosition.Top:
                                    if (_virtualRemoteY >= RemoteScreenHeight) returnToLocal = true;
                                    break;
                                case RemoteMonitorPosition.Bottom:
                                    if (_virtualRemoteY < 0) returnToLocal = true;
                                    break;
                            }
                        }

                        if (returnToLocal)
                        {
                            // Retorna o controle para o PC Local
                            SetControlMode(false, false);

                            // Posiciona o cursor físico na borda exata de retorno da tela local
                            int returnX = localW / 2;
                            int returnY = localH / 2;

                            switch (MonitorPosition)
                            {
                                case RemoteMonitorPosition.Right:
                                    returnX = localW - 3;
                                    returnY = (int)((long)_virtualRemoteY * localH / RemoteScreenHeight);
                                    break;
                                case RemoteMonitorPosition.Left:
                                    returnX = 3;
                                    returnY = (int)((long)_virtualRemoteY * localH / RemoteScreenHeight);
                                    break;
                                case RemoteMonitorPosition.Top:
                                    returnX = (int)((long)_virtualRemoteX * localW / RemoteScreenWidth);
                                    returnY = 3;
                                    break;
                                case RemoteMonitorPosition.Bottom:
                                    returnX = (int)((long)_virtualRemoteX * localW / RemoteScreenWidth);
                                    returnY = localH - 3;
                                    break;
                            }

                            returnX = Math.Max(1, Math.Min(localW - 2, returnX));
                            returnY = Math.Max(1, Math.Min(localH - 2, returnY));

                            NativeMethods.SetCursorPos(returnX, returnY);
                            return (IntPtr)1;
                        }

                        // Garante que a coordenada virtual permaneça nos limites da tela remota
                        _virtualRemoteX = Math.Max(0, Math.Min(RemoteScreenWidth - 1, _virtualRemoteX));
                        _virtualRemoteY = Math.Max(0, Math.Min(RemoteScreenHeight - 1, _virtualRemoteY));

                        // Envia pacote de movimento para a fila assíncrona
                        SendMouseData(_virtualRemoteX, _virtualRemoteY, 0, (int)NativeMethods.MOUSEEVENTF.MOVE);

                        // Se o ponteiro físico local se afastou mais de 80px do centro, estaciona de volta no centro
                        int distX = hookStruct.pt.x - (localW / 2);
                        int distY = hookStruct.pt.y - (localH / 2);
                        if ((distX * distX + distY * distY) > 80 * 80)
                        {
                            _isParkMove = true;
                            _lastParkPoint = new Point(localW / 2, localH / 2);
                            NativeMethods.SetCursorPos(_lastParkPoint.X, _lastParkPoint.Y);
                            _isParkMove = false;
                        }
                        else
                        {
                            _lastParkPoint = new Point(hookStruct.pt.x, hookStruct.pt.y);
                        }

                        return (IntPtr)1; // Suprime movimento na tela local
                    }
                    else if (dwFlags != 0)
                    {
                        // Cliques de botão ou scroll da roda do mouse
                        SendMouseData(_virtualRemoteX, _virtualRemoteY, delta, (int)dwFlags);
                        return (IntPtr)1; // Suprime cliques na tela local
                    }
                }
            }

            return NativeMethods.CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
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

        /// <summary>
        /// Callback do Gancho de Teclado de Baixo Nível (WH_KEYBOARD_LL).
        /// </summary>
        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                NativeMethods.KeyboardHookStruct hookStruct = (NativeMethods.KeyboardHookStruct)
                    Marshal.PtrToStructure(lParam, typeof(NativeMethods.KeyboardHookStruct));

                bool isKeyUp = (msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP);
                int vk = hookStruct.vkCode;

                // Tecla de Alternância Manual (ScrollLock / F12 / etc.)
                if (vk == ToggleVirtualKey)
                {
                    if (isKeyUp)
                    {
                        ToggleRemoteMode();
                    }
                    return (IntPtr)1; // Consome a tecla de atalho localmente
                }

                // Se estiver no Modo Remoto, transmite a tecla e suprime da janela local
                if (IsRemoteActive)
                {
                    uint dwFlags = isKeyUp ? (uint)NativeMethods.KEYEVENTF.KEYUP : (uint)NativeMethods.KEYEVENTF.KEYDOWN;

                    // Teclas estendidas (setas, Alt/Ctrl direito, Ins, Del, etc.)
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

                    return (IntPtr)1; // Suprime execução da tecla na máquina local
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
