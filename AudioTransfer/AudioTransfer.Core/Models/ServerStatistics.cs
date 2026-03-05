using System.Threading;

namespace AudioTransfer.Core.Models
{
    /// <summary>
    /// Thread-safe server statistics counters.
    /// All operations use Interlocked for lock-free concurrency.
    /// </summary>
    public sealed class ServerStatistics
    {
        private long _packetsSent;
        private long _bytesSent;
        private long _buffersReceived;
        private long _processingErrors;
        private long _recordingErrors;
        private long _emptyBuffers;
        private long _totalSubscribers;
        private long _activeSubscribers;

        // Snapshot fields for rate calculation
        private long _lastSnapshotPackets;
        private long _lastSnapshotBytes;
        private DateTime _lastSnapshotTime = DateTime.UtcNow;

        public long PacketsSent => Interlocked.Read(ref _packetsSent);
        public long BytesSent => Interlocked.Read(ref _bytesSent);
        public long BuffersReceived => Interlocked.Read(ref _buffersReceived);
        public long ProcessingErrors => Interlocked.Read(ref _processingErrors);
        public long RecordingErrors => Interlocked.Read(ref _recordingErrors);
        public long EmptyBuffers => Interlocked.Read(ref _emptyBuffers);
        public long TotalSubscribers => Interlocked.Read(ref _totalSubscribers);
        public long ActiveSubscribers => Interlocked.Read(ref _activeSubscribers);

        public void IncrementPacketsSent() => Interlocked.Increment(ref _packetsSent);
        public void IncrementBytesSent(long bytes) => Interlocked.Add(ref _bytesSent, bytes);
        public void IncrementBuffersReceived() => Interlocked.Increment(ref _buffersReceived);
        public void IncrementProcessingErrors() => Interlocked.Increment(ref _processingErrors);
        public void IncrementRecordingErrors() => Interlocked.Increment(ref _recordingErrors);
        public void IncrementEmptyBuffers() => Interlocked.Increment(ref _emptyBuffers);
        public void IncrementTotalSubscribers() => Interlocked.Increment(ref _totalSubscribers);
        public void SetActiveSubscribers(long count) => Interlocked.Exchange(ref _activeSubscribers, count);

        /// <summary>
        /// Calculate current throughput rates and reset snapshot counters.
        /// </summary>
        public (double PacketsPerSec, double KbitsPerSec, double ElapsedSec) TakeRateSnapshot()
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastSnapshotTime).TotalSeconds;
            if (elapsed < 0.001) return (0, 0, 0);

            long currentPackets = PacketsSent;
            long currentBytes = BytesSent;

            double pps = (currentPackets - _lastSnapshotPackets) / elapsed;
            double kbps = ((currentBytes - _lastSnapshotBytes) * 8.0) / (elapsed * 1000.0);

            _lastSnapshotPackets = currentPackets;
            _lastSnapshotBytes = currentBytes;
            _lastSnapshotTime = now;

            return (pps, kbps, elapsed);
        }

        /// <summary>
        /// Get a frozen snapshot of all stats for display purposes.
        /// </summary>
        public StatsSnapshot GetSnapshot()
        {
            return new StatsSnapshot
            {
                PacketsSent = PacketsSent,
                BytesSent = BytesSent,
                BuffersReceived = BuffersReceived,
                ProcessingErrors = ProcessingErrors,
                RecordingErrors = RecordingErrors,
                EmptyBuffers = EmptyBuffers,
                TotalSubscribers = TotalSubscribers,
                ActiveSubscribers = ActiveSubscribers,
            };
        }
    }

    /// <summary>
    /// Immutable snapshot of server statistics for safe UI display.
    /// </summary>
    public sealed class StatsSnapshot
    {
        public long PacketsSent { get; init; }
        public long BytesSent { get; init; }
        public long BuffersReceived { get; init; }
        public long ProcessingErrors { get; init; }
        public long RecordingErrors { get; init; }
        public long EmptyBuffers { get; init; }
        public long TotalSubscribers { get; init; }
        public long ActiveSubscribers { get; init; }

        public double BytesSentMB => BytesSent / (1024.0 * 1024.0);
    }
}
