using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace AudioTransfer.Core.Network
{
    /// <summary>
    /// Handles the "Delivery" of audio packets to multiple UDP consumers.
    /// Decouples the broadcast loop from the main engine logic.
    /// </summary>
    public class UdpBroadcaster
    {
        private readonly UdpClient _udpClient;
        private readonly Action<string> _logger;

        public UdpBroadcaster(UdpClient udpClient, Action<string> logger)
        {
            _udpClient = udpClient ?? throw new ArgumentNullException(nameof(udpClient));
            _logger = logger;
        }

        /// <summary>
        /// Sends a packet to all provided endpoints.
        /// </summary>
        public void Broadcast(byte[] data, int length, IEnumerable<IPEndPoint> targets)
        {
            foreach (var ep in targets)
            {
                try
                {
                    _udpClient.Send(data, length, ep);
                }
                catch (Exception ex)
                {
                    // Network errors for a specific client shouldn't stop the broadcaster.
                    // The ConnectionManager will eventually time out this client.
                    _logger?.Invoke($"[Broadcaster] Error sending to {ep}: {ex.Message}");
                }
            }
        }
    }
}
