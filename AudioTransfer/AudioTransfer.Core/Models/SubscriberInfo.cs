using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace AudioTransfer.Core.Models
{
    /// <summary>
    /// Tracks state and metrics for an individual TCP subscriber.
    /// Thread-safe counters via Interlocked.
    /// </summary>
    public sealed class SubscriberInfo : IDisposable
    {
        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        public IPEndPoint EndPoint { get; }
        public DateTime ConnectedUtc { get; }
        public DateTime LastSeenUtc { get; private set; }
        public DateTime LastSuccessUtc { get; private set; }

        private long _packetsSent;
        private long _bytesSent;
        private int _consecutiveFailures;

        public int ConsecutiveFailures => _consecutiveFailures;
        public long PacketsSent => Interlocked.Read(ref _packetsSent);
        public long BytesSent => Interlocked.Read(ref _bytesSent);
        public TimeSpan Uptime => DateTime.UtcNow - ConnectedUtc;

        public SubscriberInfo(TcpClient client)
        {
            Client = client ?? throw new ArgumentNullException(nameof(client));
            Stream = client.GetStream();
            EndPoint = (IPEndPoint)client.Client.RemoteEndPoint!;
            ConnectedUtc = DateTime.UtcNow;
            LastSeenUtc = DateTime.UtcNow;
            LastSuccessUtc = DateTime.UtcNow;
        }

        public bool ShouldSend()
        {
            return _consecutiveFailures < 10 && Client.Connected;
        }

        public void OnPacketSent(int bytes)
        {
            Interlocked.Increment(ref _packetsSent);
            Interlocked.Add(ref _bytesSent, bytes);
            _consecutiveFailures = 0;
            LastSeenUtc = DateTime.UtcNow;
            LastSuccessUtc = DateTime.UtcNow;
        }

        public void OnSendFailure()
        {
            Interlocked.Increment(ref _consecutiveFailures);
        }

        public void Dispose()
        {
            Stream?.Dispose();
            Client?.Dispose();
        }
    }
}
