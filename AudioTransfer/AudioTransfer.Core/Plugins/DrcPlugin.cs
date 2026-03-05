using System;

namespace AudioTransfer.Core.Plugins
{
    /// <summary>
    /// A simple Dynamic Range Compressor plugin.
    /// Reduces the volume of samples exceeding a certain threshold.
    /// </summary>
    public class DrcPlugin : IAudioPlugin
    {
        public string Name => "DRC";
        public bool IsEnabled { get; set; } = false;

        private const float Threshold = 0.3f; // -10.4 dB approx
        private const float Ratio = 5.0f;     // 5:1 compression ratio
        private const float MakeupGain = 1.2f; // Slight boost to compensate for compression

        public void Process(short[] buffer, int length, int sampleRate, int channels)
        {
            if (!IsEnabled) return;

            for (int i = 0; i < length; i++)
            {
                // Convert to float range [-1.0, 1.0]
                float sample = buffer[i] / 32768f;
                float absSample = Math.Abs(sample);

                if (absSample > Threshold)
                {
                    // Basic "Hard Knee" compression logic
                    float excess = absSample - Threshold;
                    float compressedAbs = Threshold + (excess / Ratio);
                    sample = (sample > 0) ? compressedAbs : -compressedAbs;
                }

                // Apply makeup gain and convert back to short
                float finalSample = sample * MakeupGain;
                
                // Clamp to avoid clipping
                if (finalSample > 1.0f) finalSample = 1.0f;
                else if (finalSample < -1.0f) finalSample = -1.0f;

                buffer[i] = (short)(finalSample * 32767);
            }
        }
    }
}
