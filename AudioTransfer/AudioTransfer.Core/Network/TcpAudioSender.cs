using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AudioTransfer.Core.Network
{
    public sealed class TcpAudioSender : ITcpSender, IDisposable
    {
        private readonly TcpClient _client;
        private NetworkStream? _stream;

        public TcpAudioSender()
        {
            _client = new TcpClient();
        }

        public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException(nameof(host));
            if (port <= 0) throw new ArgumentOutOfRangeException(nameof(port));

            await _client.ConnectAsync(host, port, cancellationToken);
            _stream = _client.GetStream();
            _client.NoDelay = true; // Enable low-latency mode
        }

        public async Task SendAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
        {
            if (_stream == null) throw new InvalidOperationException("Not connected to a server.");

            await _stream.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public void Disconnect()
        {
            _stream?.Close();
            _client.Close();
        }

        public void Dispose()
        {
            Disconnect();
            _client.Dispose();
        }
    }
}