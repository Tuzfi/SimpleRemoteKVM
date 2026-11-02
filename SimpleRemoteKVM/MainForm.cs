using System;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;

namespace SimpleRemoteKVM
{
    /// <summary>
    /// Interface Gráfica P2P Mútua e Simétrica do SimpleRemoteKVM.
    /// Ambos os PCs iniciam como nós P2P equivalentes:
    /// - Escutam conexões automaticamente na porta 15200.
    /// - Qualquer um pode conectar ao outro.
    /// - O controle de mouse e teclado é mútuo e bidirecional em tempo real.
    /// - Transição automática de monitor pela borda da tela estilo Mouse without Borders.
    /// </summary>
    public class MainForm : Form
    {
        // Componentes Centrais KVM
        private InputHookManager _hookManager;
        private InputInjector _injector;
        private NetworkPeer _peer;

        // Telemetria
        private long _packetsSent = 0;
        private long _packetsReceived = 0;
        private System.Windows.Forms.Timer _statsTimer;

        // Cores do Tema Moderno Dark
        private readonly Color ClrBackground = Color.FromArgb(18, 22, 31);
        private readonly Color ClrCard = Color.FromArgb(28, 34, 48);
        private readonly Color ClrCardBorder = Color.FromArgb(45, 55, 75);
        private readonly Color ClrTextPrimary = Color.FromArgb(240, 245, 255);
        private readonly Color ClrTextSecondary = Color.FromArgb(148, 163, 184);
        private readonly Color ClrAccentBlue = Color.FromArgb(37, 99, 235);
        private readonly Color ClrAccentGreen = Color.FromArgb(16, 185, 129);
        private readonly Color ClrAccentAmber = Color.FromArgb(245, 158, 11);
        private readonly Color ClrAccentRed = Color.FromArgb(239, 68, 68);
        private readonly Color ClrCyan = Color.FromArgb(6, 182, 212);

        // Header
        private Label _lblTitle;
        private Label _lblLocalIpBadge;

        // Conexão P2P
        private Label _lblRemoteIp;
        private TextBox _txtRemoteIp;
        private Label _lblSubnetHint;
        private Label _lblPort;
        private TextBox _txtPort;
        private Button _btnConnect;
        private Button _btnDisconnect;

        // Configuração de Monitores (Estilo Mouse without Borders)
        private ComboBox _cmbMonitorPosition;
        private CheckBox _chkEdgeSwitch;
        private Label _lblDiagram;
        private Label _lblResolutionInfo;

        // Status & Controle
        private Panel _pnlControlBanner;
        private Label _lblStatusTitle;
        private Label _lblControlMode;
        private Label _lblControlHint;
        private Button _btnToggleControl;
        private ComboBox _cmbHotkey;

        // Telemetria
        private Label _lblStatsSent;
        private Label _lblStatsReceived;
        private Label _lblStatsState;

        // Log
        private TextBox _txtLog;
        private Button _btnClearLog;
        private CheckBox _chkAutoScroll;

        private string _detectedLocalIp;
        private string _detectedSubnetBase;

        public MainForm(string autoMode = null, string autoIp = null, int autoPort = NetworkPeer.DEFAULT_PORT)
        {
            InitializeComponent();
            InitializeKvmEngine(autoPort);

            if (autoMode == "client" && !string.IsNullOrEmpty(autoIp))
            {
                _txtRemoteIp.Text = autoIp;
                _txtPort.Text = autoPort.ToString();
                ConnectToPeer(autoIp, autoPort);
            }
        }

        private void InitializeKvmEngine(int localPort)
        {
            _injector = new InputInjector();
            _hookManager = new InputHookManager(InputHookManager.DEFAULT_TOGGLE_VK);
            _peer = new NetworkPeer();

            // Wire eventos do peer de rede
            _peer.OnLog += msg => AppendLog(msg);

            _peer.OnConnected += endpoint =>
            {
                SafeInvoke(() =>
                {
                    _hookManager.IsConnected = true;
                    UpdateConnectionState(true, "Conectado em modo P2P com: " + endpoint);
                    _btnConnect.Enabled = false;
                    _btnDisconnect.Enabled = true;
                    _txtRemoteIp.Enabled = false;
                    _txtPort.Enabled = false;
                    AppendLog("[VÍNCULO P2P] Conexão mútua bidirecional ativa com " + endpoint);
                });
            };

            _peer.OnDisconnected += reason =>
            {
                SafeInvoke(() =>
                {
                    _hookManager.IsConnected = false;
                    UpdateConnectionState(false, "Desconectado (" + reason + ")");
                    _btnConnect.Enabled = true;
                    _btnDisconnect.Enabled = false;
                    _txtRemoteIp.Enabled = true;
                    _txtPort.Enabled = true;
                    _hookManager.SetBeingControlledByRemote(false);
                    UpdateControlBanner(false, false);
                    AppendLog("[VÍNCULO P2P] Conexão encerrada: " + reason);
                });
            };

            // Troca de resolução remota (handshake)
            _peer.OnRemoteResolutionReceived += (rw, rh) =>
            {
                SafeInvoke(() =>
                {
                    _hookManager.RemoteScreenWidth = rw;
                    _hookManager.RemoteScreenHeight = rh;
                    Rectangle localBounds = Screen.PrimaryScreen.Bounds;
                    _lblResolutionInfo.Text = string.Format("Resolução: Local ({0}x{1})  |  Remota ({2}x{3})  [Sincronizada]",
                        localBounds.Width, localBounds.Height, rw, rh);
                    AppendLog(string.Format("[MONITOR] Resolução do outro PC identificada: {0}x{1}", rw, rh));
                });
            };

            // Pacotes recebidos da rede
            _peer.OnDataReceived += packet =>
            {
                System.Threading.Interlocked.Increment(ref _packetsReceived);

                if (packet.Type == PackageType.machineswitched)
                {
                    // O outro PC alternou o controle (mútuo)!
                    bool remoteIsControlling = (packet.Id == 1);
                    SafeInvoke(() =>
                    {
                        _hookManager.SetBeingControlledByRemote(remoteIsControlling);
                        UpdateControlBanner(false, remoteIsControlling);
                    });
                }
                else
                {
                    // Eventos normais de mouse/teclado para injeção imediata
                    _injector.Inject(packet);
                }
            };

            // Pacotes locais originados dos ganchos de baixo nível
            _hookManager.OnDataToSend += packet =>
            {
                System.Threading.Interlocked.Increment(ref _packetsSent);
                _peer.SendPacket(packet);
            };

            // Mudança de status de controle (Local / Remoto)
            _hookManager.OnControlModeChanged += isRemote =>
            {
                SafeInvoke(() =>
                {
                    UpdateControlBanner(isRemote, false);
                });
            };

            // Notificação de quando está sendo controlado
            _hookManager.OnRemoteControlledChanged += isRemoteControlled =>
            {
                SafeInvoke(() =>
                {
                    UpdateControlBanner(false, isRemoteControlled);
                });
            };

            // 1. Inicia o servidor local automaticamente na porta 15200 (P2P Simétrico como no MWB)
            try
            {
                _peer.StartServer(localPort);
                AppendLog(string.Format("[P2P] Nó local pronto e aguardando conexões na porta {0}.", localPort));
            }
            catch (Exception ex)
            {
                AppendLog("[AVISO PORTA] " + ex.Message);
            }

            // 2. Instala ganchos globais de mouse e teclado
            try
            {
                _hookManager.Start();
                AppendLog("[HOOKS] Ganchos WH_MOUSE_LL e WH_KEYBOARD_LL instalados com sucesso.");
            }
            catch (Exception ex)
            {
                AppendLog("[ERRO HOOKS] Falha ao instalar ganchos: " + ex.Message);
            }

            // 3. Timer para telemetria de tráfego
            _statsTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _statsTimer.Tick += (s, e) =>
            {
                _lblStatsSent.Text = "Enviados: " + _packetsSent.ToString("N0") + " pkts";
                _lblStatsReceived.Text = "Recebidos: " + _packetsReceived.ToString("N0") + " pkts";
            };
            _statsTimer.Start();
        }

        private void InitializeComponent()
        {
            _detectedLocalIp = GetLocalIpAddress();
            _detectedSubnetBase = "192.168.100.";
            if (!string.IsNullOrEmpty(_detectedLocalIp) && _detectedLocalIp.Contains("."))
            {
                int lastDot = _detectedLocalIp.LastIndexOf('.');
                _detectedSubnetBase = _detectedLocalIp.Substring(0, lastDot + 1);
            }

            this.Text = "SimpleRemoteKVM — Controle Mútuo de Mouse e Teclado LAN (P2P)";
            this.Size = new Size(760, 780);
            this.MinimumSize = new Size(720, 700);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = ClrBackground;
            this.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            this.ForeColor = ClrTextPrimary;

            // 1. HEADER PANEL
            Panel pnlHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 65,
                Padding = new Padding(20, 10, 20, 10),
                BackColor = Color.FromArgb(24, 29, 41)
            };

            _lblTitle = new Label
            {
                Text = "SimpleRemoteKVM (P2P Mútuo)",
                Font = new Font("Segoe UI", 13.5f, FontStyle.Bold),
                ForeColor = ClrTextPrimary,
                AutoSize = true,
                Location = new Point(20, 10)
            };

            Label lblSubtitle = new Label
            {
                Text = "Arquitetura P2P & Transição Automática de Telas (Mouse without Borders)",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                ForeColor = ClrTextSecondary,
                AutoSize = true,
                Location = new Point(22, 38)
            };

            _lblLocalIpBadge = new Label
            {
                Text = "Meu IP na LAN:  " + _detectedLocalIp,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                ForeColor = ClrCyan,
                BackColor = Color.FromArgb(35, 45, 65),
                Padding = new Padding(10, 6, 10, 6),
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _lblLocalIpBadge.Location = new Point(pnlHeader.Width - _lblLocalIpBadge.PreferredWidth - 25, 16);

            pnlHeader.Controls.Add(_lblTitle);
            pnlHeader.Controls.Add(lblSubtitle);
            pnlHeader.Controls.Add(_lblLocalIpBadge);

            // MAIN CONTAINER COM SCROLL
            Panel pnlBody = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(20, 12, 20, 12),
                AutoScroll = true
            };

            // -------------------------------------------------------------
            // CARD 1: CONEXÃO MÚTUA P2P
            // -------------------------------------------------------------
            Panel pnlConnectionCard = CreateCardPanel(115);
            pnlConnectionCard.Dock = DockStyle.Top;

            Label lblCard1Title = new Label
            {
                Text = "1. Conexão Mútua P2P com o Outro Computador",
                Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
                ForeColor = ClrTextPrimary,
                Location = new Point(15, 10),
                AutoSize = true
            };

            Label lblCard1Desc = new Label
            {
                Text = "Basta que QUALQUER UM dos dois computadores informe o IP do outro e clique em Conectar.",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
                ForeColor = ClrTextSecondary,
                Location = new Point(17, 32),
                AutoSize = true
            };

            _lblRemoteIp = new Label
            {
                Text = "IP do Outro PC:",
                Location = new Point(18, 65),
                AutoSize = true,
                ForeColor = ClrTextSecondary
            };

            _txtRemoteIp = new TextBox
            {
                Text = _detectedSubnetBase,
                Location = new Point(125, 62),
                Width = 145,
                BackColor = Color.FromArgb(38, 46, 62),
                ForeColor = ClrTextPrimary,
                BorderStyle = BorderStyle.FixedSingle
            };

            _lblSubnetHint = new Label
            {
                Text = "(Sub-rede: " + _detectedSubnetBase + "*)",
                Font = new Font("Segoe UI", 7.5f, FontStyle.Italic),
                ForeColor = ClrCyan,
                Location = new Point(125, 87),
                AutoSize = true
            };

            _lblPort = new Label
            {
                Text = "Porta:",
                Location = new Point(285, 65),
                AutoSize = true,
                ForeColor = ClrTextSecondary
            };

            _txtPort = new TextBox
            {
                Text = NetworkPeer.DEFAULT_PORT.ToString(),
                Location = new Point(330, 62),
                Width = 65,
                BackColor = Color.FromArgb(38, 46, 62),
                ForeColor = ClrTextPrimary,
                BorderStyle = BorderStyle.FixedSingle
            };

            _btnConnect = CreateStyledButton("Conectar ao PC", ClrAccentBlue, 135, 30);
            _btnConnect.Location = new Point(410, 58);
            _btnConnect.Click += (s, e) =>
            {
                int port;
                if (!int.TryParse(_txtPort.Text, out port) || port <= 0 || port > 65535)
                {
                    MessageBox.Show("Informe uma porta válida.", "Porta Inválida", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                ConnectToPeer(_txtRemoteIp.Text.Trim(), port);
            };

            _btnDisconnect = CreateStyledButton("Desconectar", ClrAccentRed, 110, 30);
            _btnDisconnect.Location = new Point(555, 58);
            _btnDisconnect.Enabled = false;
            _btnDisconnect.Click += (s, e) =>
            {
                _peer.Stop();
                _hookManager.IsConnected = false;
                UpdateConnectionState(false, "Desconectado pelo usuário");
                _btnConnect.Enabled = true;
                _btnDisconnect.Enabled = false;
                _txtRemoteIp.Enabled = true;
                _txtPort.Enabled = true;
                _hookManager.SetBeingControlledByRemote(false);
                UpdateControlBanner(false, false);
                AppendLog("[VÍNCULO P2P] Desconectado manualmente.");
                // Reinicia o servidor para continuar ouvindo
                try { _peer.StartServer(int.Parse(_txtPort.Text)); } catch { }
            };

            pnlConnectionCard.Controls.Add(lblCard1Title);
            pnlConnectionCard.Controls.Add(lblCard1Desc);
            pnlConnectionCard.Controls.Add(_lblRemoteIp);
            pnlConnectionCard.Controls.Add(_txtRemoteIp);
            pnlConnectionCard.Controls.Add(_lblSubnetHint);
            pnlConnectionCard.Controls.Add(_lblPort);
            pnlConnectionCard.Controls.Add(_txtPort);
            pnlConnectionCard.Controls.Add(_btnConnect);
            pnlConnectionCard.Controls.Add(_btnDisconnect);

            // -------------------------------------------------------------
            // CARD 2: POSIÇÃO DOS MONITORES & TRANSIÇÃO AUTOMÁTICA
            // -------------------------------------------------------------
            Panel pnlMonitorCard = CreateCardPanel(145);
            pnlMonitorCard.Dock = DockStyle.Top;

            Label lblCard2Title = new Label
            {
                Text = "2. Posição do Segundo Computador / Monitor (Estilo Mouse without Borders)",
                Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
                ForeColor = ClrTextPrimary,
                Location = new Point(15, 10),
                AutoSize = true
            };

            Label lblPos = new Label
            {
                Text = "Posição do PC Remoto:",
                Location = new Point(18, 40),
                AutoSize = true,
                ForeColor = ClrTextSecondary
            };

            _cmbMonitorPosition = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(175, 37),
                Width = 285,
                BackColor = Color.FromArgb(38, 46, 62),
                ForeColor = ClrTextPrimary,
                FlatStyle = FlatStyle.Flat
            };
            _cmbMonitorPosition.Items.Add("À Direita  (Este PC à Esquerda ──▶ PC Remoto à Direita)");
            _cmbMonitorPosition.Items.Add("À Esquerda (PC Remoto à Esquerda ◀── Este PC à Direita)");
            _cmbMonitorPosition.Items.Add("Acima      (PC Remoto Acima ▲ Este PC Abaixo)");
            _cmbMonitorPosition.Items.Add("Abaixo     (Este PC Acima ▼ PC Remoto Abaixo)");
            _cmbMonitorPosition.SelectedIndex = 0; // Padrão: À Direita

            _cmbMonitorPosition.SelectedIndexChanged += (s, e) =>
            {
                _hookManager.MonitorPosition = (RemoteMonitorPosition)_cmbMonitorPosition.SelectedIndex;
                UpdateDiagram();
            };

            _chkEdgeSwitch = new CheckBox
            {
                Text = "Transição Automática de Tela pela Borda (Mudar de PC ao empurrar o mouse contra o canto)",
                Checked = true,
                Location = new Point(20, 70),
                AutoSize = true,
                ForeColor = ClrAccentGreen,
                Font = new Font("Segoe UI Semibold", 9.0f, FontStyle.Bold)
            };
            _chkEdgeSwitch.CheckedChanged += (s, e) =>
            {
                _hookManager.EnableEdgeSwitching = _chkEdgeSwitch.Checked;
            };

            _lblDiagram = new Label
            {
                Text = "[ 💻 Este PC ] ──────▶ [ 🖥️ PC Remoto ]",
                Font = new Font("Segoe UI Semibold", 10.0f, FontStyle.Bold),
                ForeColor = ClrCyan,
                BackColor = Color.FromArgb(20, 25, 36),
                Padding = new Padding(12, 6, 12, 6),
                AutoSize = true,
                Location = new Point(20, 98)
            };

            Rectangle curScreen = Screen.PrimaryScreen.Bounds;
            _lblResolutionInfo = new Label
            {
                Text = string.Format("Resolução: Local ({0}x{1})  |  Remota (Aguardando sincronização)", curScreen.Width, curScreen.Height),
                Font = new Font("Segoe UI", 8.0f, FontStyle.Italic),
                ForeColor = ClrTextSecondary,
                Location = new Point(340, 104),
                AutoSize = true
            };

            pnlMonitorCard.Controls.Add(lblCard2Title);
            pnlMonitorCard.Controls.Add(lblPos);
            pnlMonitorCard.Controls.Add(_cmbMonitorPosition);
            pnlMonitorCard.Controls.Add(_chkEdgeSwitch);
            pnlMonitorCard.Controls.Add(_lblDiagram);
            pnlMonitorCard.Controls.Add(_lblResolutionInfo);

            // -------------------------------------------------------------
            // CARD 3: STATUS DA CONEXÃO & BANNER DE CONTROLE
            // -------------------------------------------------------------
            Panel pnlControlCard = CreateCardPanel(135);
            pnlControlCard.Dock = DockStyle.Top;

            Label lblCard3Title = new Label
            {
                Text = "3. Status da Conexão e Alternância de Controle",
                Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
                ForeColor = ClrTextPrimary,
                Location = new Point(15, 10),
                AutoSize = true
            };

            _lblStatusTitle = new Label
            {
                Text = "Aguardando conexão...",
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                ForeColor = ClrAccentAmber,
                Location = new Point(18, 36),
                AutoSize = true
            };

            // Banner dinâmico
            _pnlControlBanner = new Panel
            {
                Location = new Point(18, 62),
                Width = 430,
                Height = 60,
                BackColor = Color.FromArgb(35, 45, 65),
                BorderStyle = BorderStyle.FixedSingle
            };

            _lblControlMode = new Label
            {
                Text = "💻 CONTROLANDO PC LOCAL",
                Font = new Font("Segoe UI", 11.5f, FontStyle.Bold),
                ForeColor = ClrAccentGreen,
                Location = new Point(12, 8),
                AutoSize = true
            };

            _lblControlHint = new Label
            {
                Text = "Mova o mouse até a borda da tela ou pressione ScrollLock para alternar.",
                Font = new Font("Segoe UI", 8.0f, FontStyle.Regular),
                ForeColor = ClrTextSecondary,
                Location = new Point(14, 34),
                AutoSize = true
            };

            _pnlControlBanner.Controls.Add(_lblControlMode);
            _pnlControlBanner.Controls.Add(_lblControlHint);

            _btnToggleControl = CreateStyledButton("Alternar Controle", ClrAccentBlue, 140, 38);
            _btnToggleControl.Location = new Point(465, 62);
            _btnToggleControl.Click += (s, e) =>
            {
                _hookManager.ToggleRemoteMode();
            };

            Label lblHotkey = new Label
            {
                Text = "Tecla de Atalho:",
                Location = new Point(465, 104),
                AutoSize = true,
                ForeColor = ClrTextSecondary,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular)
            };

            _cmbHotkey = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(565, 102),
                Width = 100,
                BackColor = Color.FromArgb(38, 46, 62),
                ForeColor = ClrTextPrimary,
                FlatStyle = FlatStyle.Flat
            };
            _cmbHotkey.Items.Add("ScrollLock");
            _cmbHotkey.Items.Add("F12");
            _cmbHotkey.Items.Add("CapsLock");
            _cmbHotkey.Items.Add("Pause");
            _cmbHotkey.SelectedIndex = 0;
            _cmbHotkey.SelectedIndexChanged += (s, e) =>
            {
                switch (_cmbHotkey.SelectedIndex)
                {
                    case 0: _hookManager.ToggleVirtualKey = 0x91; break; // ScrollLock
                    case 1: _hookManager.ToggleVirtualKey = 0x7B; break; // F12
                    case 2: _hookManager.ToggleVirtualKey = 0x14; break; // CapsLock
                    case 3: _hookManager.ToggleVirtualKey = 0x13; break; // Pause
                }
            };

            pnlControlCard.Controls.Add(lblCard3Title);
            pnlControlCard.Controls.Add(_lblStatusTitle);
            pnlControlCard.Controls.Add(_pnlControlBanner);
            pnlControlCard.Controls.Add(_btnToggleControl);
            pnlControlCard.Controls.Add(lblHotkey);
            pnlControlCard.Controls.Add(_cmbHotkey);

            // -------------------------------------------------------------
            // CARD 4: TELEMETRIA & LOG DE EVENTOS EM TEMPO REAL
            // -------------------------------------------------------------
            Panel pnlLogCard = CreateCardPanel(230);
            pnlLogCard.Dock = DockStyle.Fill;

            Label lblLogTitle = new Label
            {
                Text = "4. Registro de Eventos e Telemetria em Tempo Real",
                Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
                ForeColor = ClrTextPrimary,
                Location = new Point(15, 10),
                AutoSize = true
            };

            _lblStatsSent = new Label
            {
                Text = "Enviados: 0 pkts",
                Location = new Point(18, 38),
                AutoSize = true,
                ForeColor = ClrTextSecondary,
                Font = new Font("Segoe UI Semibold", 9.0f, FontStyle.Bold)
            };

            _lblStatsReceived = new Label
            {
                Text = "Recebidos: 0 pkts",
                Location = new Point(170, 38),
                AutoSize = true,
                ForeColor = ClrTextSecondary,
                Font = new Font("Segoe UI Semibold", 9.0f, FontStyle.Bold)
            };

            _lblStatsState = new Label
            {
                Text = "Fila Assíncrona Ativa  |  Latência < 1ms  |  NoDelay = True  |  1 MB Buffer",
                Location = new Point(320, 38),
                AutoSize = true,
                ForeColor = ClrCyan,
                Font = new Font("Segoe UI", 8.0f, FontStyle.Italic)
            };

            _chkAutoScroll = new CheckBox
            {
                Text = "Rolar auto",
                Checked = true,
                Location = new Point(515, 10),
                AutoSize = true,
                ForeColor = ClrTextSecondary,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular)
            };

            _btnClearLog = CreateStyledButton("Limpar", Color.FromArgb(50, 60, 80), 70, 24);
            _btnClearLog.Location = new Point(610, 8);
            _btnClearLog.Click += (s, e) => _txtLog.Clear();

            _txtLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(12, 15, 22),
                ForeColor = Color.FromArgb(200, 215, 235),
                Font = new Font("Consolas", 8.5f, FontStyle.Regular),
                Location = new Point(18, 62),
                Width = 660,
                Height = 155,
                BorderStyle = BorderStyle.None,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            pnlLogCard.Controls.Add(lblLogTitle);
            pnlLogCard.Controls.Add(_lblStatsSent);
            pnlLogCard.Controls.Add(_lblStatsReceived);
            pnlLogCard.Controls.Add(_lblStatsState);
            pnlLogCard.Controls.Add(_chkAutoScroll);
            pnlLogCard.Controls.Add(_btnClearLog);
            pnlLogCard.Controls.Add(_txtLog);

            // Organização vertical com espaçamento
            Panel spacer1 = new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Color.Transparent };
            Panel spacer2 = new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Color.Transparent };
            Panel spacer3 = new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Color.Transparent };

            pnlBody.Controls.Add(pnlLogCard);
            pnlBody.Controls.Add(spacer3);
            pnlBody.Controls.Add(pnlControlCard);
            pnlBody.Controls.Add(spacer2);
            pnlBody.Controls.Add(pnlMonitorCard);
            pnlBody.Controls.Add(spacer1);
            pnlBody.Controls.Add(pnlConnectionCard);

            this.Controls.Add(pnlBody);
            this.Controls.Add(pnlHeader);

            this.FormClosing += (s, e) =>
            {
                if (_hookManager != null) _hookManager.Dispose();
                if (_peer != null) _peer.Dispose();
            };
        }

        private void UpdateDiagram()
        {
            switch (_hookManager.MonitorPosition)
            {
                case RemoteMonitorPosition.Right:
                    _lblDiagram.Text = "[ 💻 Este PC ] ──────▶ [ 🖥️ PC Remoto ]";
                    break;
                case RemoteMonitorPosition.Left:
                    _lblDiagram.Text = "[ 🖥️ PC Remoto ] ◀────── [ 💻 Este PC ]";
                    break;
                case RemoteMonitorPosition.Top:
                    _lblDiagram.Text = "[ 🖥️ PC Remoto ]   ▲ (Acima)   [ 💻 Este PC ]";
                    break;
                case RemoteMonitorPosition.Bottom:
                    _lblDiagram.Text = "[ 💻 Este PC ]   ▼ (Abaixo)   [ 🖥️ PC Remoto ]";
                    break;
            }
        }

        private void ConnectToPeer(string ip, int port)
        {
            if (string.IsNullOrEmpty(ip) || ip.Trim() == _detectedSubnetBase)
            {
                MessageBox.Show("Por favor, digite o endereço IP completo do outro computador na mesma rede Wi-Fi/LAN.", "IP Incompleto", MessageBoxButtons.OK, MessageBoxIcon.Information);
                _txtRemoteIp.Focus();
                return;
            }

            IPAddress parsed;
            if (!IPAddress.TryParse(ip, out parsed))
            {
                MessageBox.Show(string.Format("O IP '{0}' é inválido. Digite um IP como 192.168.100.X.", ip), "IP Inválido", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtRemoteIp.Focus();
                return;
            }

            _lblStatusTitle.Text = "Conectando ao PC " + ip + ":" + port + "...";
            _lblStatusTitle.ForeColor = ClrCyan;

            _peer.Connect(ip, port);
        }

        private void UpdateConnectionState(bool connected, string detail)
        {
            if (connected)
            {
                _lblStatusTitle.Text = "● CONECTADO EM MODO P2P MÚTUO";
                _lblStatusTitle.ForeColor = ClrAccentGreen;
            }
            else
            {
                _lblStatusTitle.Text = "○ DESCONECTADO (" + detail + ")";
                _lblStatusTitle.ForeColor = ClrAccentRed;
            }
        }

        private void UpdateControlBanner(bool isRemoteControlling, bool isBeingControlled)
        {
            if (isBeingControlled)
            {
                _pnlControlBanner.BackColor = Color.FromArgb(60, 45, 20);
                _lblControlMode.Text = "⚠️ SENDO CONTROLADO PELO OUTRO PC";
                _lblControlMode.ForeColor = ClrAccentAmber;
                _lblControlHint.Text = "O outro computador está utilizando seu mouse e teclado agora.";
                _btnToggleControl.Text = "Retomar Controle";
            }
            else if (isRemoteControlling)
            {
                _pnlControlBanner.BackColor = Color.FromArgb(20, 50, 75);
                _lblControlMode.Text = "🖥️ CONTROLANDO PC REMOTO";
                _lblControlMode.ForeColor = ClrCyan;
                _lblControlHint.Text = "Seu mouse e teclado estão operando o outro PC. Mova de volta pela borda ou aperte ScrollLock para sair.";
                _btnToggleControl.Text = "Voltar ao PC Local";
            }
            else
            {
                _pnlControlBanner.BackColor = Color.FromArgb(25, 45, 35);
                _lblControlMode.Text = "💻 CONTROLANDO PC LOCAL";
                _lblControlMode.ForeColor = ClrAccentGreen;
                _lblControlHint.Text = "Mova o mouse até a borda da tela ou pressione ScrollLock para alternar.";
                _btnToggleControl.Text = "Alternar Controle";
            }
        }

        private void AppendLog(string message)
        {
            SafeInvoke(() =>
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                string logLine = string.Format("[{0}] {1}\r\n", timestamp, message);
                _txtLog.AppendText(logLine);

                if (_chkAutoScroll.Checked)
                {
                    _txtLog.SelectionStart = _txtLog.TextLength;
                    _txtLog.ScrollToCaret();
                }
            });
        }

        private string GetLocalIpAddress()
        {
            try
            {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530);
                    IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                    if (endPoint != null)
                    {
                        return endPoint.Address.ToString();
                    }
                }
            }
            catch
            {
                try
                {
                    IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());
                    foreach (IPAddress ip in host.AddressList)
                    {
                        if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                        {
                            return ip.ToString();
                        }
                    }
                }
                catch { }
            }
            return "127.0.0.1";
        }

        private Panel CreateCardPanel(int height)
        {
            Panel pnl = new Panel
            {
                Height = height,
                BackColor = ClrCard,
                Padding = new Padding(15)
            };

            pnl.Paint += (s, e) =>
            {
                using (Pen borderPen = new Pen(ClrCardBorder, 1))
                {
                    e.Graphics.DrawRectangle(borderPen, 0, 0, pnl.Width - 1, pnl.Height - 1);
                }
            };

            return pnl;
        }

        private Button CreateStyledButton(string text, Color backColor, int width, int height)
        {
            Button btn = new Button
            {
                Text = text,
                Width = width,
                Height = height,
                BackColor = backColor,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 9.0f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        private void SafeInvoke(Action action)
        {
            if (this.InvokeRequired)
            {
                try
                {
                    this.BeginInvoke(action);
                }
                catch (ObjectDisposedException) { }
            }
            else
            {
                action();
            }
        }
    }
}
