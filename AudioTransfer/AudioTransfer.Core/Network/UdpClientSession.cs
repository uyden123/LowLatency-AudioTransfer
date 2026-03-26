using System;
using System.Net;

namespace AudioTransfer.Core.Network
{
    public enum HandshakeState
    {
        None,
        SynReceived,
        Authenticated
    }

    public class UdpClientSession
    {
        public IPEndPoint EndPoint { get; }
        public HandshakeState State { get; set; } = HandshakeState.None;
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
        public string? DeviceName { get; set; }

        public UdpClientSession(IPEndPoint endPoint)
        {
            EndPoint = endPoint;
        }

        public bool IsActive(double timeoutSeconds = 10)
        {
            return (DateTime.UtcNow - LastSeenUtc).TotalSeconds < timeoutSeconds;
        }
    }
}
