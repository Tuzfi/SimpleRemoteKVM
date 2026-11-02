using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace SimpleRemoteKVM
{
    /// <summary>
    /// Handles TCP peer networking, socket configuration, frame serialization,
    /// and heartbeat exchange.
    /// Replicates the socket options and buffering patterns extracted from
    /// MouseWithoutBorders.SocketStuff and MouseWithoutBorders.TcpServer.
    /// </summary>
    public class NetworkPeer : IDisposable
    {
        public const int DEFAULT_PORT = 15200; // Alterado de 15100 para 15200 para evitar conflitos com o Mouse without Borders oficial
        public const int BUFFER_SIZE = 1024000; // 1 MB buffer extraído de Common.ShakeHand

        private TcpListener _listener;
        private TcpClient _client;
        private NetworkStream _stream;
        private Thread _listenThread;
        private Thread _readThread;
        private Thread _heartbeatThread;

        private volatile bool _isRunning = false;
        private bool _isDisposed = false;

        public bool IsConnected
        {
            get { return _client != null && _client.Connected; }
        }

        public string RemoteEndPointDescription { get; private set; }

        public event Action<DATA> OnDataReceived;
        public event Action<string> OnConnected;
        public event Action<string> OnDisconnected;
        public event Action<string> OnLog;

        private void Log(string message)
        {
            if (OnLog != null)
            {
                OnLog(message);
            }
        }

        /// <summary>
        /// Start as a listening server node on the specified port.
        /// </summary>
        public void StartServer(int port = DEFAULT_PORT)
        {
            _isRunning = true;
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
            }
            catch (SocketException ex)
            {
                _isRunning = false;
                if (ex.ErrorCode == 10013 || ex.ErrorCode == 10048)
                {
                    throw new InvalidOperationException(string.Format("A porta {0} já está ocupada por outro aplicativo (por exemplo, o Mouse without Borders oficial ou outro serviço). Mude para a porta {1} e tente novamente.", port, port == 15100 ? 15200 : port + 1), ex);
                }
                throw;
            }

            Log(string.Format("[REDE] Servidor aguardando conexão em 0.0.0.0:{0}...", port));

            _listenThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "KVM_ServerListener"
            };
            _listenThread.Start();
        }

        /// <summary>
        /// Connect as a client node to a remote peer IP with fast timeout.
        /// </summary>
        public void Connect(string remoteHost, int port = DEFAULT_PORT)
        {
            _isRunning = true;
            Log(string.Format("[REDE] Conectando ao PC remoto em {0}:{1}...", remoteHost, port));

            Thread connectThread = new Thread(() =>
            {
                TcpClient client = null;
                try
                {
                    client = new TcpClient();
                    ApplyMwbSocketOptions(client.Client);

                    // Timeout rápido de 4 segundos para evitar travamentos longos caso o IP esteja incorreto ou inacessível
                    IAsyncResult ar = client.BeginConnect(remoteHost, port, null, null);
                    bool success = ar.AsyncWaitHandle.WaitOne(4000, false);
                    if (!success)
                    {
                        try { client.Close(); } catch { }
                        throw new TimeoutException(string.Format("Tempo esgotado ao tentar conectar com {0}:{1}. O outro PC pode estar desligado, em outra rede Wi-Fi, com o Modo Servidor desativado ou bloqueado pelo Firewall.", remoteHost, port));
                    }
                    client.EndConnect(ar);

                    SetupClient(client);

                    // Send initial handshake
                    DATA handshake = new DATA
                    {
                        Type = PackageType.handshake,
                        Id = 1,
                        Timestamp = DateTime.UtcNow.Ticks
                    };
                    SendPacket(handshake);
                }
                catch (Exception ex)
                {
                    string msg = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                    Log("[ERRO DE CONEXÃO] " + msg);
                    if (OnDisconnected != null)
                    {
                        OnDisconnected(msg);
                    }
                }
            })
            {
                IsBackground = true,
                Name = "KVM_ClientConnector"
            };
            connectThread.Start();
        }

        private void ListenLoop()
        {
            try
            {
                while (_isRunning)
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    Log("[NETWORK] Inbound connection accepted from: " + client.Client.RemoteEndPoint);

                    ApplyMwbSocketOptions(client.Client);
                    SetupClient(client);
                }
            }
            catch (SocketException)
            {
                // Expected when listener stops
            }
            catch (Exception ex)
            {
                Log("[NETWORK ERROR] Listener loop exception: " + ex.Message);
            }
        }

        /// <summary>
        /// Configures the underlying socket with the exact parameters extracted from MWB:
        /// NoDelay = true (disable Nagle algorithm for real-time responsiveness)
        /// SendBufferSize / ReceiveBufferSize = 1 MB (1024000 bytes)
        /// </summary>
        private void ApplyMwbSocketOptions(Socket socket)
        {
            socket.NoDelay = true;
            socket.SendBufferSize = BUFFER_SIZE;
            socket.ReceiveBufferSize = BUFFER_SIZE;
        }

        private void SetupClient(TcpClient client)
        {
            lock (this)
            {
                // Se já houver uma conexão ativa e conectada, evita duplicidade (padrão UpdateTcpSockets do MWB)
                if (_client != null && _client.Connected)
                {
                    Log("[REDE] Conexão P2P mútua já ativa. Descartando soquete redundante.");
                    try { client.Close(); } catch { }
                    return;
                }

                _client = client;
                _stream = _client.GetStream();
                RemoteEndPointDescription = _client.Client.RemoteEndPoint.ToString();
            }

            if (OnConnected != null)
            {
                OnConnected(RemoteEndPointDescription);
            }

            // Start reading thread
            _readThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "KVM_SocketReader"
            };
            _readThread.Start();

            // Start heartbeat thread
            _heartbeatThread = new Thread(HeartbeatLoop)
            {
                IsBackground = true,
                Name = "KVM_Heartbeat"
            };
            _heartbeatThread.Start();
        }

        private void ReadLoop()
        {
            byte[] buffer = new byte[DATA.PACKET_SIZE];
            try
            {
                while (_isRunning && _client != null && _client.Connected)
                {
                    int totalRead = 0;
                    while (totalRead < DATA.PACKET_SIZE)
                    {
                        int read = _stream.Read(buffer, totalRead, DATA.PACKET_SIZE - totalRead);
                        if (read == 0)
                        {
                            throw new IOException("Remote peer closed the socket stream.");
                        }
                        totalRead += read;
                    }

                    /*
                     * =========================================================================
                     * TODO: [MWB Crypto Hook - Decryption]
                     * In Microsoft Mouse without Borders, incoming packets are decrypted here
                     * using AES-256 (Common.GetDecryptedStream) with CBC mode and PKCS7 padding.
                     * The 256-bit symmetric key is derived from the shared Security Key using
                     * PBKDF2 (Rfc2898DeriveBytes, 50,000 iterations) in Common.GenLegalKey.
                     * Omitted here for didactic simplicity over a trusted LAN.
                     * =========================================================================
                     */

                    DATA packet = DATA.FromBytes(buffer);

                    if (packet.Type == PackageType.handshake)
                    {
                        Log("[HANDSHAKE] Handshake received from peer. Sending ACK...");
                        DATA ack = new DATA
                        {
                            Type = PackageType.handshakeack,
                            Id = packet.Id + 1,
                            Timestamp = DateTime.UtcNow.Ticks
                        };
                        SendPacket(ack);
                    }
                    else if (packet.Type == PackageType.handshakeack)
                    {
                        Log("[HANDSHAKE] Handshake ACK confirmed. Peer ready!");
                    }
                    else if (packet.Type == PackageType.heartbeat)
                    {
                        // Heartbeat keep-alive; no action required
                    }
                    else
                    {
                        if (OnDataReceived != null)
                        {
                            OnDataReceived(packet);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (_isRunning)
                {
                    Log("[NETWORK] Peer disconnected: " + ex.Message);
                    if (OnDisconnected != null)
                    {
                        OnDisconnected(ex.Message);
                    }
                }
            }
            finally
            {
                CloseActiveConnection();
            }
        }

        private void HeartbeatLoop()
        {
            int hbId = 1000;
            try
            {
                while (_isRunning && IsConnected)
                {
                    Thread.Sleep(2000); // Send heartbeat every 2 seconds

                    DATA hb = new DATA
                    {
                        Type = PackageType.heartbeat,
                        Id = Interlocked.Increment(ref hbId),
                        Timestamp = DateTime.UtcNow.Ticks
                    };

                    SendPacket(hb);
                }
            }
            catch
            {
                // Heartbeat thread terminates on disconnect
            }
        }

        public void SendPacket(DATA packet)
        {
            if (!IsConnected || _stream == null) return;

            try
            {
                /*
                 * =========================================================================
                 * TODO: [MWB Crypto Hook - Encryption]
                 * In Microsoft Mouse without Borders, outgoing packets are encrypted here
                 * using AES-256 (Common.GetEncryptedStream) with a 256-bit PBKDF2-derived key.
                 * Omitted here for didactic simplicity over a trusted LAN.
                 * =========================================================================
                 */

                byte[] bytes = packet.ToBytes();
                lock (_stream)
                {
                    _stream.Write(bytes, 0, bytes.Length);
                    _stream.Flush();
                }
            }
            catch (Exception ex)
            {
                Log("[NETWORK ERROR] Failed to send packet: " + ex.Message);
            }
        }

        private void CloseActiveConnection()
        {
            if (_stream != null)
            {
                try { _stream.Close(); } catch { }
                _stream = null;
            }

            if (_client != null)
            {
                try { _client.Close(); } catch { }
                _client = null;
            }
        }

        public void Stop()
        {
            _isRunning = false;
            CloseActiveConnection();

            if (_listener != null)
            {
                try { _listener.Stop(); } catch { }
                _listener = null;
            }
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
