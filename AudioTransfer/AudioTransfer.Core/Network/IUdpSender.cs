using System.Threading;
using System.Threading.Tasks;

namespace AudioTransfer.Core.Network
{
    // Single responsibility: send raw bytes over UDP.
    public interface IUdpSender
    {
        Task SendAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default);
    }
}
