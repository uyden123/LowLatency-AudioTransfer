using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using AudioTransfer.Core.Buffers;
using AudioTransfer.Core.Logging;

namespace AudioTransfer.Core.Network
{
    public sealed class UdpMicReceiver : IDisposable
    {
        private UdpClient? _udpClient;
        private Thread? _receiveThread;
        private Thread? _keepAliveThread;
        private volatile bool _isRunning;
        private readonly byte[] _receiveBuffer = new byte[2048];

        private readonly int _listenPort;
        private readonly MicJitterBuffer _jitterBuffer;

        private IPEndPoint? _androidEp;
        private IPEndPoint? _targetEp;
        private DateTime _lastPacketTime;
        private readonly object _epLock = new();

        private long _packetsReceived;
        private long _bytesReceived;

        private const byte CODEC_AUDIO = 1;
        private const byte CODEC_SYN = 250;
        private const byte CODEC_SYN_ACK = 251;
        private const byte CODEC_ACK_HANDSHAKE = 252;
        private const byte CODEC_ACK = 254;
        private const byte CODEC_CONTROL = 255;

        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<bool>> _pendingAcks = new();
        private int _controlMessageId;

        // Handshake state
        private enum HandshakeState { Disconnected, SynSent, Authenticated }
        private volatile HandshakeState _state = HandshakeState.Disconnected;

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
                    return (DateTime.UtcNow - _lastPacketTime).TotalSeconds < 5 && _state == HandshakeState.Authenticated;
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

                    lock (_epLock)
                    {
                        // Fallback logic for discovering endpoints implicitly if no target provided
                        if (_androidEp == null && _targetEp == null && length > 3 && _receiveBuffer[2] == CODEC_AUDIO)
                        {
                            _androidEp = remoteIpEp;
                            _state = HandshakeState.Authenticated;
                            _lastPacketTime = DateTime.UtcNow;
                            _jitterBuffer.Clear();
                            CoreLogger.Instance.Log($"[UdpMicReceiver] Android connected implicitly: {remoteIpEp}");
                            OnAndroidConnected?.Invoke(this, EventArgs.Empty);
                        }

                        if (_androidEp != null && _androidEp.Equals(remoteIpEp))
                        {
                            _lastPacketTime = DateTime.UtcNow;
                        }
                    }

                    if (length < 3) continue;

                    int codec = _receiveBuffer[2];

                    if (codec == CODEC_SYN_ACK)
                    {
                        lock (_epLock)
                        {
                            if (_state == HandshakeState.SynSent)
                            {
                                _state = HandshakeState.Authenticated;
                                _androidEp = remoteIpEp;
                                _lastPacketTime = DateTime.UtcNow;
                                _jitterBuffer.Clear();
                                CoreLogger.Instance.Log($"[UdpMicReceiver] Handshake Complete! Connected to: {remoteIpEp}");
                                OnAndroidConnected?.Invoke(this, EventArgs.Empty);
                            }
                        }
                        SendBinaryHandshake(CODEC_ACK_HANDSHAKE, remoteIpEp);
                        continue;
                    }

                    if (length < 19) continue;

                    int seqNum = (_receiveBuffer[0] << 8) | _receiveBuffer[1];
                    long timestamp = BitConverter.ToInt64(_receiveBuffer, 3);
                    long wallClock = BitConverter.ToInt64(_receiveBuffer, 11);
                    int audioLength = length - 19;

                    if (audioLength < 0) continue;

                    Interlocked.Increment(ref _packetsReceived);
                    Interlocked.Add(ref _bytesReceived, audioLength);

                    if (codec == CODEC_ACK)
                    {
                        if (length >= 23)
                        {
                            int msgId = BitConverter.ToInt32(_receiveBuffer, 19);
                            if (_pendingAcks.TryRemove(msgId, out var tcs))
                                tcs.TrySetResult(true);
                        }
                        continue;
                    }

                    if (codec == CODEC_CONTROL)
                    {
                        if (length >= 23)
                        {
                            int msgId = BitConverter.ToInt32(_receiveBuffer, 19);
                            string json = Encoding.UTF8.GetString(_receiveBuffer, 23, length - 23).Trim();
                            
                            SendUdpRawAck(msgId, remoteIpEp);
                            CoreLogger.Instance.Log($"[UdpMicReceiver] Control ID {msgId}: {json}");
                            OnControlMessage?.Invoke(this, json);

                            if (json == "SERVER_SHUTDOWN")
                            {
                                lock (_epLock) { _androidEp = null; _state = HandshakeState.Disconnected; }
                                OnAndroidDisconnected?.Invoke(this, EventArgs.Empty);
                            }
                        }
                        continue;
                    }

                    if (codec == CODEC_AUDIO && _state == HandshakeState.Authenticated)
                    {
                        _jitterBuffer.Add(seqNum, timestamp, wallClock, _receiveBuffer, 19, audioLength);
                    }
                }
            }
            catch (Exception ex)
            {
                if (_isRunning) CoreLogger.Instance.LogError("[UdpMicReceiver] Receive error", ex);
            }
            finally
            {
                CoreLogger.Instance.Log("[UdpMicReceiver] Receive loop ended.");
            }
        }

        private void KeepAliveLoop()
        {
            while (_isRunning)
            {
                try
                {
                    Thread.Sleep(2000);

                    lock (_epLock)
                    {
                        if (_androidEp != null && (DateTime.UtcNow - _lastPacketTime).TotalSeconds > 5)
                        {
                            CoreLogger.Instance.Log($"[UdpMicReceiver] Android {_androidEp} timed out.");
                            _androidEp = null;
                            _state = HandshakeState.Disconnected;
                            OnAndroidDisconnected?.Invoke(this, EventArgs.Empty);
                        }
                    }

                    lock (_epLock)
                    {
                        if (_state == HandshakeState.Authenticated && _androidEp != null)
                        {
                            SendBinaryHandshake(CODEC_ACK_HANDSHAKE, _androidEp);
                        }
                        else if ((_state == HandshakeState.Disconnected || _state == HandshakeState.SynSent) && _targetEp != null)
                        {
                            _state = HandshakeState.SynSent;
                            SendBinaryHandshake(CODEC_SYN, _targetEp);
                        }
                    }
                }
                catch { if (!_isRunning) break; }
            }
        }

        private void SendBinaryHandshake(byte codec, IPEndPoint target)
        {
            if (_udpClient == null || _udpClient.Client == null || target == null) return;
            try
            {
                byte[] packet = new byte[3];
                packet[2] = codec;
                _udpClient.Client.SendTo(packet, packet.Length, SocketFlags.None, target);
            }
            catch { }
        }

        private void SendUdpRawAck(int msgId, IPEndPoint target)
        {
            if (_udpClient == null || _udpClient.Client == null || target == null) return;
            byte[] packet = new byte[23];
            packet[2] = CODEC_ACK;
            BitConverter.TryWriteBytes(new Span<byte>(packet, 19, 4), msgId);
            try { _udpClient.Client.SendTo(packet, packet.Length, SocketFlags.None, target); } catch { }
        }

        public async Task<bool> SendReliableControlAsync(string json)
        {
            if (_udpClient == null) return false;
            IPEndPoint? target;
            lock (_epLock) { target = _androidEp; }
            if (target == null) return false;

            int msgId = Interlocked.Increment(ref _controlMessageId);
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            byte[] packet = new byte[23 + jsonBytes.Length];
            packet[2] = CODEC_CONTROL;
            BitConverter.TryWriteBytes(new Span<byte>(packet, 19, 4), msgId);
            Buffer.BlockCopy(jsonBytes, 0, packet, 23, jsonBytes.Length);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingAcks[msgId] = tcs;

            try
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try { _udpClient.Client.SendTo(packet, packet.Length, SocketFlags.None, target); } catch { }
                    var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(250));
                    if (completedTask == tcs.Task && await tcs.Task) return true;
                }
            }
            finally
            {
                _pendingAcks.TryRemove(msgId, out _);
            }
            return false;
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
