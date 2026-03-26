using System;
using AudioTransfer.Core.Network;

namespace AudioTransfer.Core.Audio
{
    /// <summary>
    /// Manages clock drift calculation and resampling ratio adjustments.
    /// Balances the local playback buffer level with the remote jitter buffer level.
    /// </summary>
    public class DriftManager
    {
        private long _baseMinDelay = -1;
        private double _smoothedRatio = 1.0;
        
        private const int TARGET_BUFFER_MS = 80;
        private const int WARN_BUFFER_MS = 120;
        private const int DRAIN_BUFFER_MS = 300;
        private const int LOW_BUFFER_MS = 30;

        public double CalculateRatio(MicJitterBuffer jitterBuffer, int playbackBufferedMs)
        {
            double targetRatio = 1.0;
            var stats = jitterBuffer.GetStatistics();

            // 1. Initial basis for relative delay
            if (_baseMinDelay == -1 && stats.MinDelay > 0)
            {
                _baseMinDelay = stats.MinDelay;
            }
            else if (_baseMinDelay > 0 && stats.MinDelay > 0)
            {
                // Transit drift (clock mismatch between producer/consumer)
                long transitDrift = stats.MinDelay - _baseMinDelay;
                if (transitDrift > 10) { targetRatio = 1.003; _baseMinDelay += 3; }
                else if (transitDrift < -10) { targetRatio = 0.997; _baseMinDelay -= 3; }
            }

            // 2. Playback buffer level compensation
            if (playbackBufferedMs > DRAIN_BUFFER_MS)
            {
                // Critical: too much buffer, need to speed up significantly
                targetRatio = 1.05; 
            }
            else if (playbackBufferedMs > 150)
            {
                double deviation = playbackBufferedMs - TARGET_BUFFER_MS;
                targetRatio = 1.0 + (deviation * 0.0003);
            }
            else if (playbackBufferedMs > WARN_BUFFER_MS)
            {
                double deviation = playbackBufferedMs - TARGET_BUFFER_MS;
                targetRatio = 1.0 + (deviation * 0.0001);
            }
            else if (playbackBufferedMs < LOW_BUFFER_MS)
            {
                double deficit = TARGET_BUFFER_MS - playbackBufferedMs;
                targetRatio = 1.0 - (deficit * 0.00005);
            }

            targetRatio = Math.Clamp(targetRatio, 0.98, 1.05);

            // 3. Alpha smoothing to prevent clicks/pops
            double alpha = (Math.Abs(targetRatio - 1.0) > 0.005) ? 0.4 : 0.05;
            _smoothedRatio = _smoothedRatio * (1.0 - alpha) + targetRatio * alpha;
            
            return _smoothedRatio;
        }

        public double CurrentRatio => _smoothedRatio;

        public void Reset()
        {
            _baseMinDelay = -1;
            _smoothedRatio = 1.0;
        }
    }
}
