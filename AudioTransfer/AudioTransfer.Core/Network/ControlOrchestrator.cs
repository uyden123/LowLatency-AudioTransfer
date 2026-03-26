using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AudioTransfer.Core.Network
{
    /// <summary>
    /// Manages control signaling over both TCP (Command Server) and UDP (Reliable Control).
    /// </summary>
    public class ControlOrchestrator : IDisposable
    {
        private readonly TcpListener _tcpListener;
        private readonly UdpClient _udpClient;
        private readonly Action<string> _logger;
        private readonly Action<string, IPEndPoint?> _onCommandReceived;
        
        private readonly List<TcpClient> _controlClients = new();
        private readonly ConcurrentDictionary<TcpClient, SemaphoreSlim> _clientLocks = new();
        private readonly object _clientsLock = new();
        
        private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _pendingAcks = new();
        private int _msgIdCounter = 0;

        private CancellationTokenSource? _cts;
        private bool _isRunning;

        private const byte CODEC_ACK = 254;
        private const byte CODEC_CONTROL = 255;

        private readonly ClientConnectionManager _connectionManager;

        public ControlOrchestrator(int port, UdpClient udpClient, ClientConnectionManager connectionManager, Action<string, IPEndPoint?> commandHandler, Action<string> logger)
        {
            _tcpListener = new TcpListener(IPAddress.Any, port);
            _udpClient = udpClient;
            _connectionManager = connectionManager;
            _onCommandReceived = commandHandler;
            _logger = logger;
        }

        public void Start()
        {
            _isRunning = true;
            _cts = new CancellationTokenSource();
            _tcpListener.Start();
            _ = Task.Run(AcceptLoop, _cts.Token);
            _ = Task.Run(ReceiveUdpLoop, _cts.Token);
            _logger?.Invoke($"[Control] Control server started on port {_tcpListener.LocalEndpoint}.");
        }

        private async Task ReceiveUdpLoop()
        {
            while (_isRunning && _cts != null && !_cts.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync(_cts.Token);
                    if (!_connectionManager.ProcessUdpPacket(result.Buffer, result.Buffer.Length, result.RemoteEndPoint))
                    {
                        ProcessUdpControlPacket(result.Buffer, result.Buffer.Length, result.RemoteEndPoint);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    if (_isRunning) _logger?.Invoke($"[Control] UDP receive error: {ex.Message}");
                }
            }
        }

        public void Stop()
        {
            _isRunning = false;
            _cts?.Cancel();
            _tcpListener.Stop();
            
            lock (_clientsLock)
            {
                foreach (var client in _controlClients.ToArray())
                {
                    client.Dispose();
                }
                _controlClients.Clear();
            }
        }

        private async Task AcceptLoop()
        {
            while (_isRunning && _cts != null && !_cts.IsCancellationRequested)
            {
                try
                {
                    TcpClient client = await _tcpListener.AcceptTcpClientAsync(_cts.Token);
                    _clientLocks[client] = new SemaphoreSlim(1, 1);
                    _logger?.Invoke($"[Control] New TCP client: {client.Client.RemoteEndPoint}");
                    _ = Task.Run(() => HandleClient(client), _cts.Token);
                }
                catch (Exception ex) when (_isRunning)
                {
                    _logger?.Invoke($"[Control] Accept error: {ex.Message}");
                    await Task.Delay(1000);
                }
            }
        }

        private async Task HandleClient(TcpClient client)
        {
            lock (_clientsLock) _controlClients.Add(client);
            try
            {
                using var stream = client.GetStream();
                byte[] lenBuf = new byte[4];
                while (_isRunning && client.Connected)
                {
                    int read = await stream.ReadAsync(lenBuf, 0, 4, _cts!.Token);
                    if (read == 0) break;

                    int len = BitConverter.ToInt32(lenBuf, 0);
                    if (len <= 0 || len > 1024 * 1024) continue;

                    byte[] jsonBuf = new byte[len];
                    int totalRead = 0;
                    while (totalRead < len)
                    {
                        int r = await stream.ReadAsync(jsonBuf, totalRead, len - totalRead, _cts.Token);
                        if (r == 0) break;
                        totalRead += r;
                    }

                    if (totalRead == len)
                    {
                        string json = Encoding.UTF8.GetString(jsonBuf);
                        _onCommandReceived?.Invoke(json, client.Client.RemoteEndPoint as IPEndPoint);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[Control] Client disconnected: {ex.Message}");
            }
            finally
            {
                lock (_clientsLock) _controlClients.Remove(client);
                if (_clientLocks.TryRemove(client, out var sem)) sem.Dispose();
                client.Dispose();
            }
        }

        public void SendUdpRawAck(int msgId, IPEndPoint target)
        {
            byte[] packet = new byte[23];
            packet[2] = CODEC_ACK;
            BitConverter.TryWriteBytes(new Span<byte>(packet, 19, 4), msgId);
            try { _udpClient.Send(packet, packet.Length, target); } catch { }
        }

        public void ProcessUdpControlPacket(byte[] data, int length, IPEndPoint sender)
        {
            if (length < 23) return;
            byte codec = data[2];

            if (codec == CODEC_ACK)
            {
                int msgId = BitConverter.ToInt32(data, 19);
                if (_pendingAcks.TryRemove(msgId, out var tcs)) tcs.TrySetResult(true);
            }
            else if (codec == CODEC_CONTROL)
            {
                int msgId = BitConverter.ToInt32(data, 19);
                SendUdpRawAck(msgId, sender);
                string json = Encoding.UTF8.GetString(data, 23, length - 23);
                _onCommandReceived?.Invoke(json, sender);
            }
        }

        public async Task<bool> SendControlUdpReliableAsync(string json, IPEndPoint target)
        {
            int msgId = Interlocked.Increment(ref _msgIdCounter);
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            byte[] packet = new byte[23 + jsonBytes.Length];
            packet[2] = CODEC_CONTROL;
            BitConverter.TryWriteBytes(new Span<byte>(packet, 19, 4), msgId);
            Buffer.BlockCopy(jsonBytes, 0, packet, 23, jsonBytes.Length);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingAcks[msgId] = tcs;

            try
            {
                for (int i = 0; i < 5; i++)
                {
                    try { _udpClient.Send(packet, packet.Length, target); } catch { }
                    var completed = await Task.WhenAny(tcs.Task, Task.Delay(250));
                    if (completed == tcs.Task && await tcs.Task) return true;
                }
            }
            finally { _pendingAcks.TryRemove(msgId, out _); }
            return false;
        }

        public async Task<bool> SendBroadcastAsync(string json)
        {
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            byte[] prefix = BitConverter.GetBytes(jsonBytes.Length);

            TcpClient[] activeClients;
            lock (_clientsLock) activeClients = _controlClients.ToArray();

            bool sentAny = false;
            foreach (var client in activeClients)
            {
                if (!_clientLocks.TryGetValue(client, out var semaphore)) continue;
                await semaphore.WaitAsync();
                try
                {
                    var stream = client.GetStream();
                    await stream.WriteAsync(prefix, 0, prefix.Length);
                    await stream.WriteAsync(jsonBytes, 0, jsonBytes.Length);
                    sentAny = true;
                }
                catch { /* Ignore failed client */ }
                finally { semaphore.Release(); }
            }
            return sentAny;
        }

        public void Dispose()
        {
            Stop();
            _tcpListener.Stop();
        }
    }
}
