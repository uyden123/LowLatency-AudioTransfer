using System;

namespace AudioTransfer.Core.Audio
{
    /// <summary>
    /// Handles linear interpolation resampling of PCM audio streams.
    /// Maintains phase state across multiple buffers for gapless playback.
    /// </summary>
    public class AudioResampler
    {
        private double _phase = 0.0;

        /// <summary>
        /// Resamples input buffer into output buffer based on a target ratio.
        /// Ratio > 1.0 = Speed up (shrink), Ratio < 1.0 = Slow down (stretch).
        /// </summary>
        /// <returns>Number of samples written to output.</returns>
        public int Resample(short[] input, int samplesIn, int channels, double ratio, short[] output)
        {
            if (Math.Abs(ratio - 1.0) < 0.0001)
            {
                int total = samplesIn * channels;
                if (output.Length < total) throw new ArgumentOutOfRangeException(nameof(output), "Output buffer too small.");
                Array.Copy(input, 0, output, 0, total);
                return total;
            }

            int outIdx = 0;
            double step = ratio;

            while (_phase < samplesIn)
            {
                int idx = (int)_phase;
                double frac = _phase - idx;

                for (int c = 0; c < channels; c++)
                {
                    short val0 = input[idx * channels + c];
                    short val1 = (idx + 1 < samplesIn) ? input[(idx + 1) * channels + c] : val0;
                    output[outIdx * channels + c] = (short)(val0 + frac * (val1 - val0));
                }
                outIdx++;
                _phase += step;
            }

            _phase -= samplesIn;
            return outIdx * channels;
        }

        public void Reset()
        {
            _phase = 0.0;
        }
    }
}
