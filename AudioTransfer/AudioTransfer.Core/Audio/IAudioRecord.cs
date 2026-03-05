using System;
using System.Threading;
using System.Threading.Tasks;

namespace AudioTransfer.Core.Audio
{
    public interface IAudioRecorder : IDisposable
    {
        AudioFormat Format { get; }
        event EventHandler<RecordedBufferEventArgs> BufferRecorded;

        Task StartAsync(CancellationToken cancellationToken = default);
        Task StopAsync();
    }
}
