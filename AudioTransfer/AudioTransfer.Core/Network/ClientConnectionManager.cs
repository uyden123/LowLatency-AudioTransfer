using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace AudioTransfer.Core.Network
{
    /// <summary>
    /// Handles discovery, handshake, and state of multiple UDP audio clients.
    /// This is the 'Manager' for Single Producer / Multi-Consumer logic.
    /// </summary>
    public class ClientConnectionManager
    {
        private readonly ConcurrentDictionary<IPEndPoint, UdpClientSession> _clients = new();
        private readonly Action<byte[], IPEndPoint> _udpSender;
        private readonly Action<string> _logger;
        private string _deviceName = Environment.MachineName;

        public event Action<IPEndPoint, string>? ClientConnected;
        public event Action<IPEndPoint>? ClientDisconnected;

        // Codec constants from ServerEngine
        private const byte CODEC_SYN = 0xFA;          // 250
        private const byte CODEC_SYN_ACK = 0xFB;      // 251
        private const byte CODEC_ACK_HANDSHAKE = 0xFC; // 252
        private const byte CODEC_ACK = 254;           // 254

        public ClientConnectionManager(Action<byte[], IPEndPoint> udpSender, Action<string> logger)
        {
            _udpSender = udpSender;
            _logger = logger;
        }

        /// <summary>
        /// Process incoming UDP control/handshake packets.
        /// Returns true if the packet was handled by the manager.
        /// </summary>
        public bool ProcessUdpPacket(byte[] data, int length, IPEndPoint sender)
        {
            if (length < 3) return false;

            byte codec = data[2];

            switch (codec)
            {
                case CODEC_SYN:
                    HandleSyn(sender);
                    return true;

                case CODEC_ACK_HANDSHAKE:
                    HandleAckHandshake(sender, data, length);
                    return true;

                default:
                    // Check for heartbeat packets (CODEC_AUDIO = 1, but used here as dummy/hb too)
                    if (codec == 1 || codec == CODEC_ACK)
                    {
                        UpdateActive(sender);
                        return false; // Let ServerEngine handle audio data
                    }

                    // Check for string messages like "HEARTBEAT"
                    if (length > 2 && length < 50)
                    {
                        string msg = Encoding.UTF8.GetString(data, 0, length).Trim();
                        if (msg.StartsWith("HEARTBEAT"))
                        {
                            UpdateActive(sender);
                            return true;
                        }
                    }
                    break;
            }

            return false;
        }

        private void HandleSyn(IPEndPoint sender)
        {
            var session = _clients.GetOrAdd(sender, ep => new UdpClientSession(ep));
            session.State = HandshakeState.SynReceived;
            session.LastSeenUtc = DateTime.UtcNow;

            _logger($"[Manager] SYN from {sender}. Sending SYN_ACK.");

            // Construct SYN_ACK packet: [Header 2B] [Codec 1B] [Data...]
            byte[] synAck = new byte[3];
            synAck[2] = CODEC_SYN_ACK;
            
            // Fire-and-repeat: Send 3 times to ensure delivery over unreliable UDP
            for (int i = 0; i < 3; i++)
            {
                _udpSender(synAck, sender);
            }
        }

        private void HandleAckHandshake(IPEndPoint sender, byte[] data, int length)
        {
            if (_clients.TryGetValue(sender, out var session))
            {
                if (length > 3)
                {
                    string devName = Encoding.UTF8.GetString(data, 3, length - 3).Trim();
                    if (!string.IsNullOrEmpty(devName)) session.DeviceName = devName;
                }

                if (session.State == HandshakeState.SynReceived)
                {
                    session.State = HandshakeState.Authenticated;
                    session.LastSeenUtc = DateTime.UtcNow;
                    _logger($"[Manager] Handshake COMPLETE: {sender} is now AUTHENTICATED.");
                    SendDeviceName(sender);
                    ClientConnected?.Invoke(sender, session.DeviceName ?? "Client Device");
                }
            }
        }

        private void UpdateActive(IPEndPoint sender, bool forceAuthenticated = false)
        {
            var session = _clients.GetOrAdd(sender, ep => 
            {
                _logger($"[Manager] New client detected from {ep}. Waiting for handshake.");
                return new UdpClientSession(ep);
            });
            session.LastSeenUtc = DateTime.UtcNow;
            
            // If we receive a heartbeat, it means the client thinks it's connected.
            if (session.State == HandshakeState.SynReceived || forceAuthenticated)
            {
                bool newlyAuthenticated = session.State != HandshakeState.Authenticated;
                session.State = HandshakeState.Authenticated;
                if (newlyAuthenticated)
                {
                    _logger($"[Manager] {sender} promoted to AUTHENTICATED via activity/heartbeat.");
                    SendDeviceName(sender);
                    ClientConnected?.Invoke(sender, session.DeviceName ?? "Client Device");
                }
            }
        }

        private void SendDeviceName(IPEndPoint ep)
        {
            byte[] packet = Encoding.UTF8.GetBytes("DEVICE_NAME:" + _deviceName);
            for (int i = 0; i < 3; i++) {
                _udpSender(packet, ep);
            }
        }

        public List<IPEndPoint> GetAuthenticatedClients()
        {
            return _clients.Values
                .Where(s => s.State == HandshakeState.Authenticated && s.IsActive())
                .Select(s => s.EndPoint)
                .ToList();
        }

        public void StartMaintenance(string deviceName, CancellationToken token)
        {
            _deviceName = deviceName;
            _ = Task.Run(async () =>
            {
                byte[] deviceNamePacket = Encoding.UTF8.GetBytes("DEVICE_NAME:" + deviceName);
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(2000, token);
                        Cleanup();
                        var activeClients = GetAuthenticatedClients();
                        if (activeClients.Count > 0)
                        {
                            _sendAction(deviceNamePacket, activeClients.ToArray());
                        }
                    }
                    catch (TaskCanceledException) { break; }
                    catch (Exception ex) { _logger($"[ConnectionManager] Maintenance error: {ex.Message}"); }
                }
            }, token);
        }

        private void _sendAction(byte[] data, IPEndPoint[] targets)
        {
            foreach (var ep in targets)
            {
                _udpSender(data, ep); // Changed _rawSend to _udpSender
            }
        }

        public void Cleanup()
        {
            var timedOut = _clients.Where(kvp => !kvp.Value.IsActive()).ToList();
            foreach (var kvp in timedOut)
            {
                if (_clients.TryRemove(kvp.Key, out var session))
                {
                    _logger($"[Manager] Client {kvp.Key} removed due to timeout.");
                    if (session.State == HandshakeState.Authenticated)
                        ClientDisconnected?.Invoke(kvp.Key);
                }
            }
        }

        public void RemoveClient(IPEndPoint ep)
        {
            if (_clients.TryRemove(ep, out var session))
            {
                _logger($"[Manager] Client {ep} removed manually.");
                if (session.State == HandshakeState.Authenticated)
                    ClientDisconnected?.Invoke(ep);
            }
        }
    }
}
