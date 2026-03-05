using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace AudioTransfer.Core.Buffers
{
    /// <summary>
    /// Sequence-number based jitter buffer for incoming UDP mic audio.
    /// Port of the Android JitterBuffer with adaptive/fixed modes,
    /// clock drift monitoring, PLC gap detection, and object pooling.
    /// </summary>
    public sealed class MicJitterBuffer
    {
        private const int PACKET_DURATION_MS = 20;
        private const int MIN_TARGET_PACKETS = 2;  // 40ms minimum
        private const int MAX_TARGET_PACKETS = 15;  // 300ms maximum
        private const int MAX_BUFFER_PACKETS = 150;

        // Buffer state
        private readonly PriorityQueue<AudioPacket, int> _queue = new();
        private readonly Stack<AudioPacket> _pool = new();
        private int _nextSequence = -1;
        private bool _isBuffering = true;
        private readonly object _lock = new();

        // Stats
        private long _totalReceived;
        private long _lostPackets;
        private long _latePackets;
        private long _lastReportedPackets;
        private long _lastReportTime;
        private double _currentBitrateKbps;

        // Sliding window for loss rate
        private const int WINDOW_SIZE_SEC = 10;
        private readonly long[] _lostPerSec = new long[WINDOW_SIZE_SEC];
        private readonly long[] _receivedPerSec = new long[WINDOW_SIZE_SEC];
        private int _windowIdx;
        private long _lastWindowUpdate;
        private double _currentLossRate;

        // Adaptive target
        private bool _isAdaptive = true;
        private int _fixedTargetPackets = 4;
        private int _targetPackets = 4;

        // MinDelay Clock Drift Monitor
        private readonly long[] _delayHistory = new long[200]; // 4s window at 20ms/packet
        private int _delayIndex;
        private long _currentMinDelay;
        private double _currentJitterSD;
        private long _lastMeasuredTransitDelay;

        public MicJitterBuffer()
        {
            // Pre-fill pool
            for (int i = 0; i < MAX_BUFFER_PACKETS; i++)
                _pool.Push(new AudioPacket());
        }

        private AudioPacket AcquirePacket()
        {
            if (_pool.Count > 0) return _pool.Pop();
            return new AudioPacket();
        }

        public void RecyclePacket(AudioPacket packet)
        {
            lock (_lock)
            {
                if (packet != null && _pool.Count < MAX_BUFFER_PACKETS)
                    _pool.Push(packet);
            }
        }

        public void Add(int sequence, long timestamp, long wallClock, byte[] data, int offset, int length)
        {
            lock (_lock)
            {
                // Compute transit delay
                long localArrival = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long delay = localArrival - wallClock;

                _delayHistory[_delayIndex] = delay;
                _delayIndex = (_delayIndex + 1) % _delayHistory.Length;

                long m = long.MaxValue;
                long sum = 0;
                int count = 0;
                foreach (long d in _delayHistory)
                {
                    if (d != 0)
                    {
                        if (d < m) m = d;
                        sum += d;
                        count++;
                    }
                }
                if (m != long.MaxValue) _currentMinDelay = m;

                _lastMeasuredTransitDelay = (delay - _currentMinDelay) + 5;
                if (_lastMeasuredTransitDelay < 0) _lastMeasuredTransitDelay = 0;

                // Standard deviation for adaptive jitter buffer
                if (count > 10)
                {
                    double mean = (double)sum / count;
                    double varianceSum = 0;
                    foreach (long d in _delayHistory)
                    {
                        if (d != 0)
                            varianceSum += (d - mean) * (d - mean);
                    }
                    _currentJitterSD = Math.Sqrt(varianceSum / count);

                    if (_isAdaptive)
                    {
                        int targetMs = 20 + (int)(_currentJitterSD * 3);
                        int packets = targetMs / PACKET_DURATION_MS;
                        _targetPackets = Math.Clamp(packets, MIN_TARGET_PACKETS, MAX_TARGET_PACKETS);
                    }
                    else
                    {
                        _targetPackets = _fixedTargetPackets;
                    }
                }

                // Detect server reset or large sequence jumps  
                if (_nextSequence != -1)
                {
                    int diff = (sequence - _nextSequence + 65536) % 65536;
                    if (diff > 32768)
                    {
                        if (diff < 65536 - 200)
                        {
                            CoreLogger.Instance.Log($"[MicJitter] Probable server reset (seq={sequence}, expected~{_nextSequence})");
                            ClearInternal();
                        }
                        else
                        {
                            _latePackets++;
                            return;
                        }
                    }
                    else if (diff > 500)
                    {
                        CoreLogger.Instance.Log($"[MicJitter] Large forward jump ({diff} packets), resetting...");
                        ClearInternal();
                    }
                }

                // Evict oldest if full
                if (_queue.Count >= MAX_BUFFER_PACKETS)
                {
                    if (_queue.TryDequeue(out var old, out _))
                        RecyclePacketInternal(old);
                }

                var packet = AcquirePacket();
                packet.Set(sequence, timestamp, wallClock, data, offset, length, false);
                _queue.Enqueue(packet, SequencePriority(sequence));

                _totalReceived++;

                // Windowed stats
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (_lastWindowUpdate == 0) _lastWindowUpdate = now;
                if (now - _lastWindowUpdate >= 1000)
                {
                    _windowIdx = (_windowIdx + 1) % WINDOW_SIZE_SEC;
                    _lostPerSec[_windowIdx] = 0;
                    _receivedPerSec[_windowIdx] = 0;

                    long totalLost = 0, totalRecv = 0;
                    for (int i = 0; i < WINDOW_SIZE_SEC; i++)
                    {
                        totalLost += _lostPerSec[i];
                        totalRecv += _receivedPerSec[i];
                    }
                    long expected = totalRecv + totalLost;
                    _currentLossRate = expected > 0 ? (totalLost * 100.0 / expected) : 0;
                    _lastWindowUpdate = now;
                }
                _receivedPerSec[_windowIdx]++;

                // Bitrate calculation
                if (_lastReportTime == 0) _lastReportTime = now;
                if (now - _lastReportTime >= 1000)
                {
                    long deltaPackets = _totalReceived - _lastReportedPackets;
                    double bits = deltaPackets * length * 8;
                    _currentBitrateKbps = bits / (now - _lastReportTime);
                    _lastReportedPackets = _totalReceived;
                    _lastReportTime = now;
                }

                Monitor.PulseAll(_lock);
            }
        }

        public AudioPacket? Take()
        {
            lock (_lock)
            {
                // Initial buffering
                if (_isBuffering)
                {
                    if (_queue.Count >= _targetPackets)
                    {
                        _isBuffering = false;
                        if (_nextSequence == -1 && _queue.TryPeek(out var peek, out _))
                            _nextSequence = peek.Sequence;
                    }
                    else
                    {
                        return null;
                    }
                }

                if (!_queue.TryPeek(out var packet, out _))
                {
                    _isBuffering = true;
                    return null;
                }

                if (packet.Sequence == _nextSequence)
                {
                    _queue.Dequeue();
                    _nextSequence = (_nextSequence + 1) & 0xFFFF;
                    return packet;
                }
                else if (IsEarlier(packet.Sequence, _nextSequence))
                {
                    _queue.Dequeue();
                    RecyclePacketInternal(packet);
                    _latePackets++;
                    return Take(); // recurse
                }
                else
                {
                    // Gap detected
                    int gap = (packet.Sequence - _nextSequence + 65536) % 65535;
                    if (gap > 50)
                    {
                        CoreLogger.Instance.Log($"[MicJitter] Gap too large ({gap}), jumping to {packet.Sequence}");
                        _nextSequence = packet.Sequence;
                        _isBuffering = true;
                        return null;
                    }

                    // PLC packet
                    var plcPacket = AcquirePacket();
                    plcPacket.Set(_nextSequence, packet.Timestamp, 0, null, 0, 0, true);
                    _nextSequence = (_nextSequence + 1) & 0xFFFF;
                    _lostPackets++;
                    _lostPerSec[_windowIdx]++;
                    return plcPacket;
                }
            }
        }

        public bool IsBuffering
        {
            get { lock (_lock) return _isBuffering; }
        }

        private bool IsEarlier(int seq1, int seq2)
        {
            int diff = (seq1 - seq2 + 65536) % 65536;
            return diff > 32768;
        }

        /// <summary>
        /// Priority for PriorityQueue: lower = higher priority.
        /// Handles wraparound by normalizing relative to a reference.
        /// </summary>
        private static int SequencePriority(int sequence)
        {
            return sequence; // PriorityQueue dequeues lowest first, matching ascending sequence
        }

        public Statistics GetStatistics()
        {
            lock (_lock)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return new Statistics
                {
                    BufferLevel = _queue.Count,
                    DelayMs = _queue.Count * PACKET_DURATION_MS,
                    MinDelay = _currentMinDelay,
                    TargetPackets = _targetPackets,
                    JitterDev = _currentJitterSD,
                    TotalReceived = _totalReceived,
                    LostPackets = _lostPackets,
                    LatePackets = _latePackets,
                    BitrateKbps = (now - _lastReportTime > 2000) ? 0 : _currentBitrateKbps,
                    LastTransitDelay = _lastMeasuredTransitDelay,
                    LossRate = (now - _lastReportTime > 2000) ? 0 : _currentLossRate
                };
            }
        }

        public void ResetStats()
        {
            lock (_lock)
            {
                _totalReceived = 0;
                _lostPackets = 0;
                _latePackets = 0;
                _currentBitrateKbps = 0;
                _lastReportedPackets = 0;
                _lastReportTime = 0;
                _currentLossRate = 0;
                Array.Clear(_lostPerSec);
                Array.Clear(_receivedPerSec);
            }
        }

        public void Clear()
        {
            lock (_lock) ClearInternal();
        }

        private void ClearInternal()
        {
            while (_queue.TryDequeue(out var p, out _))
                RecyclePacketInternal(p);
            _nextSequence = -1;
            _isBuffering = true;
        }

        private void RecyclePacketInternal(AudioPacket packet)
        {
            if (packet != null && _pool.Count < MAX_BUFFER_PACKETS)
                _pool.Push(packet);
        }

        public void SetBufferingMode(bool adaptive, int fixedPackets)
        {
            lock (_lock)
            {
                _isAdaptive = adaptive;
                _fixedTargetPackets = fixedPackets;
                if (!adaptive) _targetPackets = fixedPackets;
            }
        }

        /// <summary>Audio packet with object pooling support.</summary>
        public sealed class AudioPacket
        {
            public int Sequence;
            public long Timestamp;
            public long WallClock;
            public byte[] Data;
            public int Length;
            public bool IsPLC;

            public AudioPacket()
            {
                Data = new byte[2048];
            }

            public void Set(int sequence, long timestamp, long wallClock, byte[]? data, int offset, int length, bool isPLC)
            {
                Sequence = sequence;
                Timestamp = timestamp;
                WallClock = wallClock;
                if (Data.Length < length) Data = new byte[length];
                if (data != null) Buffer.BlockCopy(data, offset, Data, 0, length);
                Length = length;
                IsPLC = isPLC;
            }
        }

        public sealed class Statistics
        {
            public int BufferLevel;
            public int DelayMs;
            public long MinDelay;
            public int TargetPackets;
            public double JitterDev;
            public long TotalReceived;
            public long LostPackets;
            public long LatePackets;
            public double BitrateKbps;
            public long LastTransitDelay;
            public double LossRate;

            public override string ToString() =>
                $"Buffer: {BufferLevel}/{TargetPackets} pkts Drift: {MinDelay} Jitter: {JitterDev:F1}ms " +
                $"Loss: {LostPackets} ({LossRate:F1}%) Late: {LatePackets} " +
                $"Bitrate: {BitrateKbps:F1} kbps Net: {LastTransitDelay}ms";
        }
    }
}
