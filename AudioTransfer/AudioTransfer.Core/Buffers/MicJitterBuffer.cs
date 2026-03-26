using System;
using System.Collections.Generic;
using System.Threading;

namespace AudioTransfer.Core.Buffers
{
    /// <summary>
    /// Sequence-number based jitter buffer for incoming UDP mic audio.
    /// Port of the Android JitterBuffer with adaptive/fixed modes,
    /// clock drift monitoring, PLC gap detection, and object pooling.
    ///
    /// PERFORMANCE NOTES (vs original):
    ///   - Delay stats: O(200) full-scan per packet → O(1) incremental (running sum +
    ///     smart min tracking + Welford online variance). Full rescan only on min-eviction.
    ///   - Take(): recursive late-packet draining → iterative while-loop (no stack growth).
    ///   - Monitor.PulseAll removed (no consumer ever calls Monitor.Wait on _lock).
    ///   - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() called once per Add() (was twice).
    ///   - _lastReportTime now uses the same 'now' variable from Add() (one syscall).
    /// </summary>
    public sealed class MicJitterBuffer
    {
        private const int PACKET_DURATION_MS   = 20;
        private const int MIN_TARGET_PACKETS   = 2;   // 40ms minimum
        private const int MAX_TARGET_PACKETS   = 15;  // 300ms maximum
        private const int MAX_BUFFER_PACKETS   = 150;

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

        // Sliding window for loss rate (10s × 50pps ≈ 500 packets)
        private const int WINDOW_SIZE_SEC = 10;
        private readonly long[] _lostPerSec     = new long[WINDOW_SIZE_SEC];
        private readonly long[] _receivedPerSec = new long[WINDOW_SIZE_SEC];
        private int  _windowIdx;
        private long _lastWindowUpdate;
        private double _currentLossRate;

        // Adaptive target
        private bool _isAdaptive        = true;
        private int  _fixedTargetPackets = 4;
        private int  _targetPackets      = 4;

        // ── O(1) incremental delay statistics ──────────────────────────────────
        // Sentinel for unfilled circular-buffer slots (avoids confusing 0ms with empty).
        private const long DELAY_SLOT_EMPTY = long.MinValue;

        // Circular buffer of raw transit delays (arrival − wallClock).
        private readonly long[] _delayHistory = new long[200]; // ~4 s at 50 pps
        private int _delayIndex;

        // Running sum & count for O(1) mean (add new, subtract evicted).
        private long _runningSum;
        private int  _runningCount;

        // Running minimum with lazy-rescan strategy:
        //   • If new delay ≤ current min  → update directly (O(1)).
        //   • If the evicted slot WAS the min → mark stale and rescan on next query.
        //   • Otherwise                    → no work needed.
        private long _runningMin = long.MaxValue;
        private bool _minStale;                // true when we need a rescan

        // Welford online algorithm for variance (O(1) per sample, no removal needed).
        // We use a fixed α EMA instead of true Welford because we slide a window and
        // true Welford doesn't support O(1) removal. α=0.05 gives ~20-packet half-life.
        private const double EMA_ALPHA = 0.05;
        private double _emaVariance;           // running EMA of (delay − mean)²

        private long   _currentMinDelay;
        private double _currentJitterSD;
        private long   _lastMeasuredTransitDelay;
        // ───────────────────────────────────────────────────────────────────────

        public MicJitterBuffer()
        {
            // Mark all history slots as empty so the stats loop ignores them on startup.
            Array.Fill(_delayHistory, DELAY_SLOT_EMPTY);

            // Pre-warm object pool to avoid GC pressure during playback.
            for (int i = 0; i < MAX_BUFFER_PACKETS; i++)
                _pool.Push(new AudioPacket());
        }

        // ── Object pool ────────────────────────────────────────────────────────
        private AudioPacket AcquirePacket()
        {
            // Called only from within _lock, so no extra sync needed.
            return _pool.Count > 0 ? _pool.Pop() : new AudioPacket();
        }

        /// <summary>Returns a used packet to the pool. Thread-safe.</summary>
        public void RecyclePacket(AudioPacket packet)
        {
            lock (_lock) RecyclePacketInternal(packet);
        }

        private void RecyclePacketInternal(AudioPacket packet)
        {
            if (packet != null && _pool.Count < MAX_BUFFER_PACKETS)
                _pool.Push(packet);
        }

        // ── O(1) delay-stats update ────────────────────────────────────────────
        /// <summary>
        /// Update incremental min/mean/variance state with a new delay sample.
        /// Amortised O(1): full rescan only when the evicted slot held the global min.
        /// </summary>
        private void UpdateDelayStats(long delay)
        {
            // --- evict the slot we are about to overwrite ---
            long evicted = _delayHistory[_delayIndex];
            if (evicted != DELAY_SLOT_EMPTY)
            {
                _runningSum -= evicted;
                _runningCount--;
                // If the evicted value was our tracked minimum we need a rescan.
                if (evicted <= _runningMin)
                    _minStale = true;
            }

            // --- insert new sample ---
            _delayHistory[_delayIndex] = delay;
            _delayIndex = (_delayIndex + 1) % _delayHistory.Length;
            _runningSum += delay;
            _runningCount++;

            // --- update minimum (O(1) fast path, O(N) rescan only when stale) ---
            if (!_minStale && delay <= _runningMin)
            {
                _runningMin = delay;
            }
            else if (_minStale)
            {
                // Rescan — happens at most once per window period in stable networks.
                long m = long.MaxValue;
                foreach (long d in _delayHistory)
                    if (d != DELAY_SLOT_EMPTY && d < m) m = d;
                _runningMin = m;
                _minStale   = false;
            }

            if (_runningMin != long.MaxValue)
                _currentMinDelay = _runningMin;

            // --- Welford EMA variance (O(1)) ---
            if (_runningCount > 0)
            {
                double mean = (double)_runningSum / _runningCount;
                double diff = delay - mean;
                _emaVariance   = EMA_ALPHA * diff * diff + (1.0 - EMA_ALPHA) * _emaVariance;
                _currentJitterSD = Math.Sqrt(_emaVariance);
            }

            // --- transit delay (jitter relative to min) ---
            _lastMeasuredTransitDelay = (delay - _currentMinDelay) + 5;
            if (_lastMeasuredTransitDelay < 0) _lastMeasuredTransitDelay = 0;

            // --- adaptive target update ---
            if (_runningCount > 10)
            {
                if (_isAdaptive)
                {
                    int targetMs = 20 + (int)(_currentJitterSD * 3);
                    _targetPackets = Math.Clamp(targetMs / PACKET_DURATION_MS,
                                                MIN_TARGET_PACKETS, MAX_TARGET_PACKETS);
                }
                else
                {
                    _targetPackets = _fixedTargetPackets;
                }
            }
        }
        // ───────────────────────────────────────────────────────────────────────

        public void Add(int sequence, long timestamp, long wallClock,
                        byte[] data, int offset, int length)
        {
            lock (_lock)
            {
                // One syscall covers stats, bitrate window, and window update.
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                // O(1) incremental stats (was O(200) full-scan every packet).
                long delay = now - wallClock;
                UpdateDelayStats(delay);

                // Detect server reset or large forward jumps.
                if (_nextSequence != -1)
                {
                    int diff = (sequence - _nextSequence + 65536) % 65536;
                    if (diff > 32768)
                    {
                        if (diff < 65536 - 200)
                        {
                            CoreLogger.Instance.Log(
                                $"[MicJitter] Probable server reset (seq={sequence}, expected~{_nextSequence})");
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
                        CoreLogger.Instance.Log(
                            $"[MicJitter] Large forward jump ({diff} packets), resetting...");
                        ClearInternal();
                    }
                }

                // Evict oldest if queue is at capacity.
                if (_queue.Count >= MAX_BUFFER_PACKETS)
                {
                    if (_queue.TryDequeue(out var old, out _))
                        RecyclePacketInternal(old);
                }

                var packet = AcquirePacket();
                packet.Set(sequence, timestamp, wallClock, data, offset, length, false);
                _queue.Enqueue(packet, SequencePriority(sequence));

                _totalReceived++;

                // ── Windowed loss-rate (runs at most once per second) ──
                if (_lastWindowUpdate == 0) _lastWindowUpdate = now;
                if (now - _lastWindowUpdate >= 1000)
                {
                    _windowIdx = (_windowIdx + 1) % WINDOW_SIZE_SEC;
                    _lostPerSec[_windowIdx]     = 0;
                    _receivedPerSec[_windowIdx] = 0;

                    long totalLost = 0, totalRecv = 0;
                    for (int i = 0; i < WINDOW_SIZE_SEC; i++)
                    {
                        totalLost += _lostPerSec[i];
                        totalRecv += _receivedPerSec[i];
                    }
                    long expected   = totalRecv + totalLost;
                    _currentLossRate = expected > 0 ? totalLost * 100.0 / expected : 0;
                    _lastWindowUpdate = now;
                }
                _receivedPerSec[_windowIdx]++;

                // ── Bitrate (once per second) ──
                if (_lastReportTime == 0) _lastReportTime = now;
                if (now - _lastReportTime >= 1000)
                {
                    long deltaPackets    = _totalReceived - _lastReportedPackets;
                    double bits          = deltaPackets * length * 8.0;
                    _currentBitrateKbps  = bits / (now - _lastReportTime);
                    _lastReportedPackets = _totalReceived;
                    _lastReportTime      = now;
                }

                // NOTE: Monitor.PulseAll removed — nothing calls Monitor.Wait on _lock,
                // so it was a no-op that wasted an OS kernel transition each packet.
            }
        }

        public AudioPacket? Take()
        {
            lock (_lock)
            {
                // ── Iterative late-packet drain (was recursive → stack risk) ──
                // Loop until we either return a real packet, a PLC packet, or null.
                while (true)
                {
                    // Initial buffering phase: wait until we have enough packets.
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
                        // In-order packet — deliver it.
                        _queue.Dequeue();
                        _nextSequence = (_nextSequence + 1) & 0xFFFF;
                        return packet;
                    }
                    else if (IsEarlier(packet.Sequence, _nextSequence))
                    {
                        // Late (duplicate/reordered) packet — discard and keep looping
                        // instead of recursing (avoids O(N) stack depth).
                        _queue.Dequeue();
                        RecyclePacketInternal(packet);
                        _latePackets++;
                        continue; // iterate instead of recurse
                    }
                    else
                    {
                        // Gap: future packet arrived before we expected.
                        int gap = (packet.Sequence - _nextSequence + 65536) % 65536;
                        if (gap > 50)
                        {
                            // Gap too large — jump forward and re-buffer.
                            CoreLogger.Instance.Log(
                                $"[MicJitter] Gap too large ({gap}), jumping to {packet.Sequence}");
                            _nextSequence = packet.Sequence;
                            _isBuffering  = true;
                            return null;
                        }

                        // Issue a PLC packet for the missing slot.
                        var plcPacket = AcquirePacket();
                        plcPacket.Set(_nextSequence, packet.Timestamp, 0, null, 0, 0, true);
                        _nextSequence = (_nextSequence + 1) & 0xFFFF;
                        _lostPackets++;
                        _lostPerSec[_windowIdx]++;
                        return plcPacket;
                    }
                }
            }
        }

        public bool IsBuffering
        {
            get { lock (_lock) return _isBuffering; }
        }

        // Returns true if seq1 is earlier (older) than seq2, accounting for wrap-around.
        private static bool IsEarlier(int seq1, int seq2)
        {
            int diff = (seq1 - seq2 + 65536) % 65536;
            return diff > 32768;
        }

        /// <summary>
        /// PriorityQueue priority: lower value = dequeued first.
        /// Returns the distance of <paramref name="sequence"/> from _nextSequence
        /// so that wrap-around is handled correctly (seq=0 is NOT given priority
        /// over seq=65535 when we are near the wrap point).
        /// </summary>
        private int SequencePriority(int sequence)
        {
            if (_nextSequence < 0) return sequence;  // pre-buffering fallback
            return (sequence - _nextSequence + 65536) % 65536;
        }

        public Statistics GetStatistics()
        {
            lock (_lock)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                bool stale = (now - _lastReportTime) > 2000;
                return new Statistics
                {
                    BufferLevel      = _queue.Count,
                    DelayMs          = _queue.Count * PACKET_DURATION_MS,
                    MinDelay         = _currentMinDelay,
                    TargetPackets    = _targetPackets,
                    JitterDev        = _currentJitterSD,
                    TotalReceived    = _totalReceived,
                    LostPackets      = _lostPackets,
                    LatePackets      = _latePackets,
                    BitrateKbps      = stale ? 0 : _currentBitrateKbps,
                    LastTransitDelay = _lastMeasuredTransitDelay,
                    LossRate         = stale ? 0 : _currentLossRate
                };
            }
        }

        public void ResetStats()
        {
            lock (_lock)
            {
                _totalReceived      = 0;
                _lostPackets        = 0;
                _latePackets        = 0;
                _currentBitrateKbps = 0;
                _lastReportedPackets = 0;
                _lastReportTime     = 0;
                _currentLossRate    = 0;
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
            _isBuffering  = true;
            // Reset incremental stats so stale history doesn't influence the new session.
            _runningSum   = 0;
            _runningCount = 0;
            _runningMin   = long.MaxValue;
            _minStale     = false;
            _emaVariance  = 0;
            Array.Fill(_delayHistory, DELAY_SLOT_EMPTY);
        }

        public void SetBufferingMode(bool adaptive, int fixedPackets)
        {
            lock (_lock)
            {
                _isAdaptive          = adaptive;
                _fixedTargetPackets  = fixedPackets;
                if (!adaptive) _targetPackets = fixedPackets;
            }
        }

        // ── Inner types ────────────────────────────────────────────────────────

        /// <summary>Audio packet with object pooling support.</summary>
        public sealed class AudioPacket
        {
            public int    Sequence;
            public long   Timestamp;
            public long   WallClock;
            public byte[] Data;
            public int    Length;
            public bool   IsPLC;

            public AudioPacket() { Data = new byte[2048]; }

            public void Set(int sequence, long timestamp, long wallClock,
                            byte[]? data, int offset, int length, bool isPLC)
            {
                Sequence  = sequence;
                Timestamp = timestamp;
                WallClock = wallClock;
                // Only reallocate if the pooled buffer is too small (rare).
                if (Data.Length < length) Data = new byte[length];
                if (data != null) Buffer.BlockCopy(data, offset, Data, 0, length);
                Length = length;
                IsPLC  = isPLC;
            }
        }

        public sealed class Statistics
        {
            public int    BufferLevel;
            public int    DelayMs;
            public long   MinDelay;
            public int    TargetPackets;
            public double JitterDev;
            public long   TotalReceived;
            public long   LostPackets;
            public long   LatePackets;
            public double BitrateKbps;
            public long   LastTransitDelay;
            public double LossRate;

            public override string ToString() =>
                $"Buffer: {BufferLevel}/{TargetPackets} pkts  Drift: {MinDelay}ms  " +
                $"Jitter: {JitterDev:F1}ms  Loss: {LostPackets} ({LossRate:F1}%)  " +
                $"Late: {LatePackets}  Bitrate: {BitrateKbps:F1} kbps  Net: {LastTransitDelay}ms";
        }
    }
}
