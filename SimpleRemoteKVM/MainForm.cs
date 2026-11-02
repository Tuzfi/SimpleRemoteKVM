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
    /// - Ambos escutam conexões automaticamente.
    /// - Qualquer um pode conectar ao outro.
    /// - O controle de mouse e teclado é mútuo e bidirecional em tempo real.
    /// </summary>
    public class MainForm : Form
    {
        // Core KVM Components
        private InputHookManager _hookManager;
        private InputInjector _injector;
        private NetworkPeer _peer;

        // Statistics
        private long _packetsSent = 0;
        private long _packetsReceived = 0;
        private System.Windows.Forms.Timer _statsTimer;

        // UI Theme Colors
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

        // Header controls
        private Label _lblTitle;
        private Label _lblLocalIpBadge;

        // Peer Connection controls
        private Label _lblRemoteIp;
        private TextBox _txtRemoteIp;
        private Label _lblSubnetHint;
        private Label _lblPort;
        private TextBox _txtPort;
        private Button _btnConnect;
        private Button _btnDisconnect;

        // Status & Control Banner controls
        private Panel _pnlControlBanner;
        private Label _lblStatusTitle;
        private Label _lblStatusDetail;
        private Label _lblControlMode;
        private Label _lblControlHint;
        private Button _btnToggleControl;
        private ComboBox _cmbHotkey;

        // Stats controls
        private Label _lblStatsSent;
        private Label _lblStatsReceived;
        private Label _lblStatsState;

        // Log controls
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

            // Wire peer events
            _peer.OnLog += msg => AppendLog(msg);

            _peer.OnConnected += endpoint =>
            {
                SafeInvoke(() =>
                {
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

            // Incoming packets
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
                    // Eventos normais de mouse/teclado para injeção
                    _injector.Inject(packet);
                }
            };

            // Outgoing packets from local hooks
            _hookManager.OnDataToSend += packet =>
            {
                System.Threading.Interlocked.Increment(ref _packetsSent);
                _peer.SendPacket(packet);
            };

            // Local user pressed Toggle Key (ScrollLock)
            _hookManager.OnControlModeChanged += isRemote =>
            {
                SafeInvoke(() =>
                {
                    UpdateControlBanner(isRemote, false);
                });
            };

            // Remote control status changed
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
            this.Size = new Size(740, 700);
            this.MinimumSize = new Size(700, 640);
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
                Text = "Arquitetura P2P Simétrica — Ambos os PCs podem controlar e ser controlados",
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

            // MAIN CONTAINER WITH PADDING
            Panel pnlBody = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(20, 15, 20, 15),
                AutoScroll = true
            };

            // 2. MUTUAL CONNECTION CARD
            Panel pnlConnectionCard = CreateCardPanel(130);
            pnlConnectionCard.Dock = DockStyle.Top;

            Label lblCard1Title = new Label
            {
                Text = "1. Conexão Mútua P2P com o Outro Computador",
                Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
                ForeColor = ClrTextPrimary,
                Location = new Point(15, 12),
                AutoSize = true
            };

            Label lblCard1Desc = new Label
            {
                Text = "Ambos os PCs são iguais: basta que QUALQUER UM dos dois digite o IP do outro e clique em Conectar.",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
                ForeColor = ClrTextSecondary,
                Location = new Point(17, 34),
                AutoSize = true
            };

            _lblRemoteIp = new Label
            {
                Text = "IP do Outro PC:",
                Location = new Point(18, 68),
                AutoSize = true,
                ForeColor = ClrTextSecondary
            };

            _txtRemoteIp = new TextBox
            {
                Text = _detectedSubnetBase,
                Location = new Point(125, 65),
                Width = 145,
                BackColor = Color.FromArgb(38, 46, 62),
                ForeColor = ClrTextPrimary,
                BorderStyle = BorderStyle.FixedSingle
            };

            _lblSubnetHint = new Label
            {
                Text = "(Na mesma rede: " + _detectedSubnetBase + "*)",
                Font = new Font("Segoe UI", 7.5f, FontStyle.Italic),
                ForeColor = ClrCyan,
                Location = new Point(125, 90),
                AutoSize = true
            };

            _lblPort = new Label
            {
                Text = "Porta:",
                Location = new Point(285, 68),
                AutoSize = true,
                ForeColor = ClrTextSecondary
            };

            _txtPort = new TextBox
            {
                Text = NetworkPeer.DEFAULT_PORT.ToString(),
                Location = new Point(330, 65),
                Width = 65,
                BackColor = Color.FromArgb(38, 46, 62),
                ForeColor = ClrTextPrimary,
                BorderStyle = BorderStyle.FixedSingle
            };

            _btnConnect = CreateStyledButton("Conectar ao PC", ClrAccentBlue, 140, 32);
            _btnConnect.Location = new Point(415, 61);
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

            _btnDisconnect = CreateStyledButton("Desconectar", ClrAccentRed, 110, 32);
            _btnDisconnect.Location = new Point(565, 61);
            _btnDisconnect.Enabled = false;
            _btnDisconnect.Click += (s, e) =>
            {
                _peer.Stop();
                _btnConnect.Enabled = true;
                _btnDisconnect.Enabled = false;
                _txtRemoteIp.Enabled = true;
                _txtPort.Enabled = true;
                UpdateConnectionState(false, "Desconectado pelo usuário");
                AppendLog("[P2P] Conexão finalizada pelo usuário.");
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

            // SPACER 1
            Panel pnlSpacer1 = new Panel { Dock = DockStyle.Top, Height = 12 };

            // 3. MUTUAL CONTROL BANNER (Hero Card)
            _pnlControlBanner = CreateCardPanel(160);
            _pnlControlBanner.Dock = DockStyle.Top;
            _pnlControlBanner.BackColor = Color.FromArgb(24, 30, 42);

            _lblStatusTitle = new Label
            {
                Text = "ESTADO DO VÍNCULO P2P",
                Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                ForeColor = ClrTextSecondary,
                Location = new Point(18, 14),
                AutoSize = true
            };

            _lblStatusDetail = new Label
            {
                Text = "Aguardando conexão mútua (pronto para conectar ou receber)...",
                Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
                ForeColor = ClrAccentAmber,
                Location = new Point(18, 33),
                AutoSize = true
            };

            _lblControlMode = new Label
            {
                Text = "MODO LOCAL ATIVO — Você está no controle deste computador",
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = ClrAccentGreen,
                Location = new Point(18, 66),
                AutoSize = true
            };

            _lblControlHint = new Label
            {
                Text = "Pressione [ScrollLock] a qualquer momento para alternar o controle entre os computadores.",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                ForeColor = ClrTextSecondary,
                Location = new Point(20, 92),
                AutoSize = true
            };

            _btnToggleControl = CreateStyledButton("Alternar Controle Mútuo [ScrollLock]", ClrCyan, 290, 36);
            _btnToggleControl.Location = new Point(20, 114);
            _btnToggleControl.ForeColor = Color.Black;
            _btnToggleControl.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            _btnToggleControl.Click += (s, e) =>
            {
                _hookManager.ToggleRemoteMode();
            };

            Label lblHotkeyHint = new Label
            {
                Text = "Tecla de Atalho:",
                Location = new Point(330, 122),
                AutoSize = true,
                ForeColor = ClrTextSecondary
            };

            _cmbHotkey = new ComboBox
            {
                Location = new Point(430, 119),
                Width = 140,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(38, 46, 62),
                ForeColor = ClrTextPrimary,
                FlatStyle = FlatStyle.Flat
            };
            _cmbHotkey.Items.Add("ScrollLock (Padrão)");
            _cmbHotkey.Items.Add("F12");
            _cmbHotkey.Items.Add("Pause / Break");
            _cmbHotkey.Items.Add("F10");
            _cmbHotkey.SelectedIndex = 0;
            _cmbHotkey.SelectedIndexChanged += (s, e) =>
            {
                switch (_cmbHotkey.SelectedIndex)
                {
                    case 0: _hookManager.ToggleVirtualKey = 0x91; break; // VK_SCROLL
                    case 1: _hookManager.ToggleVirtualKey = 0x7B; break; // VK_F12
                    case 2: _hookManager.ToggleVirtualKey = 0x13; break; // VK_PAUSE
                    case 3: _hookManager.ToggleVirtualKey = 0x79; break; // VK_F10
                }
                AppendLog("[HOTKEY] Tecla de alternância configurada para: " + _cmbHotkey.SelectedItem);
            };

            _pnlControlBanner.Controls.Add(_lblStatusTitle);
            _pnlControlBanner.Controls.Add(_lblStatusDetail);
            _pnlControlBanner.Controls.Add(_lblControlMode);
            _pnlControlBanner.Controls.Add(_lblControlHint);
            _pnlControlBanner.Controls.Add(_btnToggleControl);
            _pnlControlBanner.Controls.Add(lblHotkeyHint);
            _pnlControlBanner.Controls.Add(_cmbHotkey);

            // SPACER 2
            Panel pnlSpacer2 = new Panel { Dock = DockStyle.Top, Height = 12 };

            // 4. TELEMETRY STATUS BAR
            Panel pnlTelemetry = new Panel
            {
                Dock = DockStyle.Top,
                Height = 28,
                BackColor = Color.Transparent
            };

            _lblStatsSent = new Label
            {
                Text = "Enviados: 0 pkts",
                ForeColor = ClrTextSecondary,
                Font = new Font("Segoe UI", 8.5f),
                Location = new Point(5, 5),
                AutoSize = true
            };

            _lblStatsReceived = new Label
            {
                Text = "Recebidos: 0 pkts",
                ForeColor = ClrTextSecondary,
                Font = new Font("Segoe UI", 8.5f),
                Location = new Point(170, 5),
                AutoSize = true
            };

            _lblStatsState = new Label
            {
                Text = "Socket P2P Bidirecional  |  NoDelay=True  |  Buffer 1 MB",
                ForeColor = ClrTextSecondary,
                Font = new Font("Segoe UI", 8.5f),
                Location = new Point(340, 5),
                AutoSize = true
            };

            pnlTelemetry.Controls.Add(_lblStatsSent);
            pnlTelemetry.Controls.Add(_lblStatsReceived);
            pnlTelemetry.Controls.Add(_lblStatsState);

            // 5. LIVE LOG CARD
            Panel pnlLogCard = CreateCardPanel(200);
            pnlLogCard.Dock = DockStyle.Fill;

            Panel pnlLogHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 35
            };

            Label lblLogTitle = new Label
            {
                Text = "Registro de Eventos e Protocolo em Tempo Real",
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                ForeColor = ClrTextPrimary,
                Location = new Point(12, 8),
                AutoSize = true
            };

            _chkAutoScroll = new CheckBox
            {
                Text = "Rolar auto",
                Checked = true,
                ForeColor = ClrTextSecondary,
                Location = new Point(pnlLogCard.Width - 190, 6),
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            _btnClearLog = new Button
            {
                Text = "Limpar",
                Width = 70,
                Height = 25,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 48, 66),
                ForeColor = ClrTextSecondary,
                Location = new Point(pnlLogCard.Width - 90, 4),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Cursor = Cursors.Hand
            };
            _btnClearLog.FlatAppearance.BorderSize = 0;
            _btnClearLog.Click += (s, e) => _txtLog.Clear();

            pnlLogHeader.Controls.Add(lblLogTitle);
            pnlLogHeader.Controls.Add(_chkAutoScroll);
            pnlLogHeader.Controls.Add(_btnClearLog);

            _txtLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(18, 22, 30),
                ForeColor = Color.FromArgb(203, 213, 225),
                Font = new Font("Consolas", 8.5f),
                BorderStyle = BorderStyle.None
            };

            Panel pnlLogTextWrapper = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10, 0, 10, 10)
            };
            pnlLogTextWrapper.Controls.Add(_txtLog);

            pnlLogCard.Controls.Add(pnlLogTextWrapper);
            pnlLogCard.Controls.Add(pnlLogHeader);

            // Assemble Hierarchy
            pnlBody.Controls.Add(pnlLogCard);
            pnlBody.Controls.Add(pnlTelemetry);
            pnlBody.Controls.Add(pnlSpacer2);
            pnlBody.Controls.Add(_pnlControlBanner);
            pnlBody.Controls.Add(pnlSpacer1);
            pnlBody.Controls.Add(pnlConnectionCard);

            this.Controls.Add(pnlBody);
            this.Controls.Add(pnlHeader);

            this.FormClosing += (s, e) =>
            {
                _statsTimer.Stop();
                if (_hookManager != null) _hookManager.Dispose();
                if (_peer != null) _peer.Dispose();
            };
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
                using (Pen pen = new Pen(ClrCardBorder, 1))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, pnl.Width - 1, pnl.Height - 1);
                }
            };
            pnl.Resize += (s, e) => pnl.Invalidate();
            return pnl;
        }

        private Button CreateStyledButton(string text, Color bg, int width, int height)
        {
            Button btn = new Button
            {
                Text = text,
                Width = width,
                Height = height,
                BackColor = bg,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        private void ConnectToPeer(string ip, int port)
        {
            if (string.IsNullOrEmpty(ip))
            {
                MessageBox.Show("Informe o IP do outro computador.", "IP Ausente", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (ip == _detectedLocalIp)
            {
                DialogResult dr = MessageBox.Show(
                    "Você inseriu o IP deste próprio computador (" + _detectedLocalIp + ")!\r\n\r\nPara vincular dois computadores, você deve digitar o IP do OUTRO PC (mostrado no topo da tela do outro PC, ex: 192.168.100.X).\r\n\r\nDeseja tentar conectar mesmo assim?",
                    "Atenção: IP Próprio",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (dr == DialogResult.No) return;
            }

            if (ip.EndsWith("."))
            {
                MessageBox.Show("Por favor, complete o IP com o número final do outro PC (exemplo: " + ip + "50).\r\n\r\nO IP exato pode ser visto no topo da janela no outro PC.", "IP Incompleto", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                _peer.Connect(ip, port);
                _btnConnect.Enabled = false;
                _btnDisconnect.Enabled = true;
                _txtRemoteIp.Enabled = false;
                _txtPort.Enabled = false;

                _lblStatusDetail.Text = "Conectando ao peer em " + ip + ":" + port + "...";
                _lblStatusDetail.ForeColor = ClrAccentAmber;
                AppendLog("[P2P] Tentando estabelecer vínculo bidirecional com " + ip + ":" + port + "...");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro ao conectar: " + ex.Message, "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void UpdateConnectionState(bool isConnected, string detail)
        {
            _lblStatusDetail.Text = detail;
            _lblStatusDetail.ForeColor = isConnected ? ClrAccentGreen : ClrAccentRed;
            _btnDisconnect.Enabled = isConnected || !_btnConnect.Enabled;
        }

        private void UpdateControlBanner(bool isLocalSendingToRemote, bool isBeingControlledByRemote)
        {
            if (isLocalSendingToRemote)
            {
                // VOCÊ está controlando o outro PC
                _pnlControlBanner.BackColor = Color.FromArgb(16, 44, 64);
                _lblControlMode.Text = ">>> VOCÊ ESTÁ CONTROLANDO O OUTRO PC! <<<";
                _lblControlMode.ForeColor = ClrCyan;
                _lblControlHint.Text = "Seu mouse e teclado estão atuando no computador remoto. Pressione [ScrollLock] para retornar o controle a este PC.";
                _btnToggleControl.Text = "Retornar ao PC Local [ScrollLock]";
                _btnToggleControl.BackColor = ClrAccentAmber;
                _btnToggleControl.ForeColor = Color.Black;
                AppendLog(">>> [CONTROLE MÚTUO] Você assumiu o controle do PC remoto <<<");
            }
            else if (isBeingControlledByRemote)
            {
                // O OUTRO PC está controlando este PC
                _pnlControlBanner.BackColor = Color.FromArgb(50, 35, 20);
                _lblControlMode.Text = ">>> ESTE PC ESTÁ SENDO CONTROLADO REMOTAMENTE <<<";
                _lblControlMode.ForeColor = ClrAccentAmber;
                _lblControlHint.Text = "O outro PC está operando o mouse e teclado aqui. Você pode apertar [ScrollLock] a qualquer momento para retomar o controle.";
                _btnToggleControl.Text = "Tomar Controle deste PC [ScrollLock]";
                _btnToggleControl.BackColor = ClrAccentGreen;
                _btnToggleControl.ForeColor = Color.Black;
                AppendLog(">>> [CONTROLE MÚTUO] O outro PC assumiu o controle deste computador <<<");
            }
            else
            {
                // Modo Normal / Local
                _pnlControlBanner.BackColor = Color.FromArgb(24, 30, 42);
                _lblControlMode.Text = "MODO LOCAL ATIVO — Você está no controle deste computador";
                _lblControlMode.ForeColor = ClrAccentGreen;
                _lblControlHint.Text = "Seu mouse e teclado operam normalmente aqui. Pressione [ScrollLock] a qualquer momento para controlar o outro PC.";
                _btnToggleControl.Text = "Controlar o Outro PC [ScrollLock]";
                _btnToggleControl.BackColor = ClrCyan;
                _btnToggleControl.ForeColor = Color.Black;
                AppendLog("<<< [CONTROLE MÚTUO] Modo Local ativo <<<");
            }
        }

        public void AppendLog(string message)
        {
            SafeInvoke(() =>
            {
                if (_txtLog == null || _txtLog.IsDisposed) return;
                string line = string.Format("[{0:HH:mm:ss}] {1}\r\n", DateTime.Now, message);
                _txtLog.AppendText(line);

                if (_chkAutoScroll.Checked)
                {
                    _txtLog.SelectionStart = _txtLog.Text.Length;
                    _txtLog.ScrollToCaret();
                }
            });
        }

        private void SafeInvoke(Action act)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            if (this.InvokeRequired)
            {
                this.BeginInvoke(act);
            }
            else
            {
                act();
            }
        }

        private static string GetLocalIpAddress()
        {
            try
            {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530);
                    IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                    if (endPoint != null) return endPoint.Address.ToString();
                }
            }
            catch { }

            try
            {
                foreach (IPAddress ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    {
                        return ip.ToString();
                    }
                }
            }
            catch { }

            return "127.0.0.1";
        }
    }
}
