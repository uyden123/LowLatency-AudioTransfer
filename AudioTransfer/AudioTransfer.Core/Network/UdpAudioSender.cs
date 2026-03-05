using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AudioTransfer.Core.Network
{
    public sealed class UdpAudioSender : IUdpSender, IDisposable
    {
        private readonly UdpClient _client;
        private readonly IPEndPoint _endpoint;

        public UdpAudioSender(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException(nameof(host));
            if (port <= 0) throw new ArgumentOutOfRangeException(nameof(port));

            _client = new UdpClient();
            _endpoint = new IPEndPoint(Dns.GetHostAddresses(host)[0], port);
        }

        public Task SendAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
        {
            return _client.SendAsync(buffer, count, _endpoint);
        }

        public void Dispose()
        {
            _client?.Dispose();
        }
    }
}
