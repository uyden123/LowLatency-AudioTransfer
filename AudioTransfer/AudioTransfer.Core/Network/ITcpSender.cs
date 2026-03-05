using System.Threading;
using System.Threading.Tasks;

namespace AudioTransfer.Core.Network
{
    // Single responsibility: send raw bytes over TCP.
    public interface ITcpSender
    {
        Task SendAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default);
        Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default);
        void Disconnect();
    }
}