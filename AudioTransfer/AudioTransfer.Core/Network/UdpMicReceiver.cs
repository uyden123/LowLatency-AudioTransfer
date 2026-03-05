using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using AudioTransfer.Core.Buffers;
using AudioTransfer.Core.Logging;

namespace AudioTransfer.Core.Network
{
    /// <summary>
    /// UDP receiver for Android mic audio.
    /// Listens on a port, receives Opus-encoded audio packets from Android,
    /// and feeds them into a MicJitterBuffer.
    /// 
    /// Packet format (same as PC→Android):
    ///   [2B SeqNum BE] [1B Codec] [8B Timestamp LE] [8B WallClock LE] [N bytes Opus data]
    ///   Header: 19 bytes total
    ///   Codec: 1 = Opus, 255 = Control message
    /// </summary>
    public sealed class UdpMicReceiver : IDisposable
    {
        private UdpClient? _udpClient;
        private Thread? _receiveThread;
        private Thread? _keepAliveThread;
        private volatile bool _isRunning;
        private readonly byte[] _receiveBuffer = new byte[2048];

        private readonly int _listenPort;
        private readonly MicJitterBuffer _jitterBuffer;

        // Connected Android endpoint
        private IPEndPoint? _androidEp;
        private IPEndPoint? _targetEp; // Fixed target if provided
        private DateTime _lastPacketTime;
        private readonly object _epLock = new();

        // Statistics
        private long _packetsReceived;
        private long _bytesReceived;

        public event EventHandler<string>? OnControlMessage;
        public event EventHandler? OnAndroidConnected;
        public event EventHandler? OnAndroidDisconnected;

        public bool IsConnected
        {
            get
            {
                lock (_epLock)
                {
                    if (_androidEp == null) return false;
                    return (DateTime.UtcNow - _lastPacketTime).TotalSeconds < 5;
                }
            }
        }

        public IPEndPoint? ConnectedEndpoint
        {
            get { lock (_epLock) return _androidEp; }
        }

        public IPEndPoint? TargetEndpoint
        {
            get { lock (_epLock) return _targetEp; }
            set { lock (_epLock) _targetEp = value; }
        }

        public long PacketsReceived => Interlocked.Read(ref _packetsReceived);
        public long BytesReceived => Interlocked.Read(ref _bytesReceived);

        public UdpMicReceiver(int listenPort, MicJitterBuffer jitterBuffer, IPEndPoint? targetEp = null)
        {
            _listenPort = listenPort;
            _jitterBuffer = jitterBuffer;
            _targetEp = targetEp;
        }

        public void Start()
        {
            if (_isRunning) throw new InvalidOperationException("Already running.");

            _udpClient = new UdpClient(_listenPort);
            _udpClient.Client.ReceiveBufferSize = 1024 * 1024;
            _isRunning = true;

            CoreLogger.Instance.Log($"[UdpMicReceiver] Listening on UDP port {_listenPort}");

            _receiveThread = new Thread(ReceiveLoop)
            {
                Name = "UdpMicReceiverThread",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _receiveThread.Start();

            _keepAliveThread = new Thread(KeepAliveLoop)
            {
                Name = "UdpMicKeepAlive",
                IsBackground = true
            };
            _keepAliveThread.Start();
        }

        private void ReceiveLoop()
        {
            try
            {
                while (_isRunning)
                {
                    EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
                    int length;
                    try
                    {
                        length = _udpClient!.Client.ReceiveFrom(_receiveBuffer, ref remoteEp);
                    }
                    catch (SocketException)
                    {
                        if (!_isRunning) break;
                        continue;
                    }

                    IPEndPoint remoteIpEp = (IPEndPoint)remoteEp;

                    // Track last packet time for connection detection
                    lock (_epLock)
                    {
                        _lastPacketTime = DateTime.UtcNow;
                        if (_androidEp == null)
                        {
                            _androidEp = remoteIpEp;
                            _jitterBuffer.Clear();
                            CoreLogger.Instance.Log($"[UdpMicReceiver] Android connected: {remoteIpEp}");
                            OnAndroidConnected?.Invoke(this, EventArgs.Empty);
                        }
                    }

                    // Check for control messages (text-only packets)
                    if (length < 19)
                    {
                        // Could be SUBSCRIBE, HEARTBEAT, etc.
                        string msg = Encoding.UTF8.GetString(_receiveBuffer, 0, length).Trim();
                        HandleControlText(msg, remoteIpEp);
                        continue;
                    }

                    // Parse packet header
                    int seqNum = (_receiveBuffer[0] << 8) | _receiveBuffer[1];
                    int codec = _receiveBuffer[2];

                    // Timestamp (8 bytes LE)
                    long timestamp = BitConverter.ToInt64(_receiveBuffer, 3);

                    // WallClock (8 bytes LE)
                    long wallClock = BitConverter.ToInt64(_receiveBuffer, 11);

                    int audioLength = length - 19;
                    if (audioLength < 0) continue;

                    Interlocked.Increment(ref _packetsReceived);
                    Interlocked.Add(ref _bytesReceived, audioLength);

                    // Handle control codec
                    if (codec == 255)
                    {
                        string controlMsg = Encoding.UTF8.GetString(_receiveBuffer, 19, audioLength).Trim();
                        CoreLogger.Instance.Log($"[UdpMicReceiver] Control: {controlMsg}");
                        OnControlMessage?.Invoke(this, controlMsg);

                        if (controlMsg == "SERVER_SHUTDOWN")
                        {
                            lock (_epLock)
                            {
                                _androidEp = null;
                            }
                            OnAndroidDisconnected?.Invoke(this, EventArgs.Empty);
                        }
                        continue;
                    }

                    // Feed into jitter buffer (Zero-allocation)
                    _jitterBuffer.Add(seqNum, timestamp, wallClock, _receiveBuffer, 19, audioLength);
                }
            }
            catch (Exception ex)
            {
                if (_isRunning)
                    CoreLogger.Instance.LogError("[UdpMicReceiver] Receive error", ex);
            }
            finally
            {
                CoreLogger.Instance.Log("[UdpMicReceiver] Receive loop ended.");
            }
        }

        private void HandleControlText(string msg, IPEndPoint remoteEp)
        {
            if (string.Equals(msg, "SUBSCRIBE", StringComparison.OrdinalIgnoreCase))
            {
                lock (_epLock)
                {
                    _androidEp = remoteEp;
                    _lastPacketTime = DateTime.UtcNow;
                }

                _jitterBuffer.Clear();
                OnAndroidConnected?.Invoke(this, EventArgs.Empty);

                // Send ACK
                try
                {
                    byte[] ack = Encoding.UTF8.GetBytes("SUBSCRIBE_ACK");
                    _udpClient!.Send(ack, ack.Length, remoteEp);
                    CoreLogger.Instance.Log($"[UdpMicReceiver] Android subscribed: {remoteEp}");
                }
                catch { }
            }
            else if (string.Equals(msg, "HEARTBEAT", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    byte[] ack = Encoding.UTF8.GetBytes("HEARTBEAT_ACK");
                    _udpClient!.Send(ack, ack.Length, remoteEp);
                    
                    // Also ensure we recognize it if we haven't yet
                    lock (_epLock)
                    {
                        if (_androidEp == null)
                        {
                            _androidEp = remoteEp;
                            _jitterBuffer.Clear();
                            OnAndroidConnected?.Invoke(this, EventArgs.Empty);
                        }
                    }
                }
                catch { }
            }
            else if (string.Equals(msg, "SUBSCRIBE_ACK", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(msg, "HEARTBEAT_ACK", StringComparison.OrdinalIgnoreCase))
            {
                // Clear jitter buffer on SUBSCRIBE_ACK as it indicates a new session start
                if (msg.Equals("SUBSCRIBE_ACK", StringComparison.OrdinalIgnoreCase))
                {
                    _jitterBuffer.Clear();
                }

                lock (_epLock)
                {
                    _lastPacketTime = DateTime.UtcNow;
                    if (_androidEp == null || !_androidEp.Equals(remoteEp))
                    {
                        _androidEp = remoteEp;
                        _jitterBuffer.Clear();
                        OnAndroidConnected?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
            else if (string.Equals(msg, "UNSUBSCRIBE", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(msg, "DISCONNECT", StringComparison.OrdinalIgnoreCase))
            {
                lock (_epLock)
                {
                    if (_androidEp != null && _androidEp.Equals(remoteEp))
                    {
                        CoreLogger.Instance.Log($"[UdpMicReceiver] Android unsubscribed: {remoteEp}");
                        _androidEp = null;
                        OnAndroidDisconnected?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
        }

        private void KeepAliveLoop()
        {
            while (_isRunning)
            {
                try
                {
                    Thread.Sleep(2000);

                    // Check timeout
                    lock (_epLock)
                    {
                        if (_androidEp != null && (DateTime.UtcNow - _lastPacketTime).TotalSeconds > 5)
                        {
                            CoreLogger.Instance.Log($"[UdpMicReceiver] Android {_androidEp} timed out.");
                            _androidEp = null;
                            OnAndroidDisconnected?.Invoke(this, EventArgs.Empty);
                        }
                    }

                    // Heartbeat/timeout check loop
                    IPEndPoint? sendTo = null;
                    byte[]? payload = null;

                    lock (_epLock)
                    {
                        if (_androidEp != null)
                        {
                            sendTo = _androidEp;
                            payload = Encoding.UTF8.GetBytes("HEARTBEAT");
                        }
                        else if (_targetEp != null)
                        {
                            sendTo = _targetEp;
                            payload = Encoding.UTF8.GetBytes("SUBSCRIBE");
                        }
                    }

                    if (sendTo != null && payload != null)
                    {
                        try
                        {
                            _udpClient?.Send(payload, payload.Length, sendTo);
                        }
                        catch { }
                    }

                }
                catch { if (!_isRunning) break; }
            }
        }

        public void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;

            CoreLogger.Instance.Log("[UdpMicReceiver] Stopping...");

            try { _udpClient?.Close(); } catch { }

            if (_receiveThread != null && _receiveThread.IsAlive)
                _receiveThread.Join(2000);
            if (_keepAliveThread != null && _keepAliveThread.IsAlive)
                _keepAliveThread.Join(2000);

            CoreLogger.Instance.Log("[UdpMicReceiver] Stopped.");
        }

        public void Dispose()
        {
            Stop();
            _udpClient?.Dispose();
        }
    }
}
