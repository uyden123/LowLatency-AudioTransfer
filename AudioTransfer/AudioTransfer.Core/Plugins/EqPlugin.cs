using System;

namespace AudioTransfer.Core.Plugins
{
    public class EqPlugin : IAudioPlugin
    {
        public string Name => "EQ";
        public bool IsEnabled { get; set; } = false;

        public enum EqPreset
        {
            None,
            BassBoost,
            VocalBoost,
            NightMode
        }

        public EqPreset CurrentPreset { get; set; } = EqPreset.None;

        // Biquad filters for Bass (Low Shelf), Mid (Peaking), High (High Shelf)
        private BiquadFilter _bassFilterL = new BiquadFilter();
        private BiquadFilter _bassFilterR = new BiquadFilter();
        private BiquadFilter _midFilterL = new BiquadFilter();
        private BiquadFilter _midFilterR = new BiquadFilter();
        private BiquadFilter _highFilterL = new BiquadFilter();
        private BiquadFilter _highFilterR = new BiquadFilter();

        public void Process(short[] buffer, int length, int sampleRate, int channels)
        {
            if (!IsEnabled || CurrentPreset == EqPreset.None) return;

            UpdateFilters(sampleRate);

            for (int i = 0; i < length; i += channels)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    float sample = buffer[i + ch] / 32768f;
                    
                    if (ch == 0)
                    {
                        sample = _bassFilterL.Process(sample);
                        sample = _midFilterL.Process(sample);
                        sample = _highFilterL.Process(sample);
                    }
                    else
                    {
                        sample = _bassFilterR.Process(sample);
                        sample = _midFilterR.Process(sample);
                        sample = _highFilterR.Process(sample);
                    }

                    // Clamp
                    if (sample > 1.0f) sample = 1.0f;
                    else if (sample < -1.0f) sample = -1.0f;

                    buffer[i + ch] = (short)(sample * 32767);
                }
            }
        }

        private void UpdateFilters(int sampleRate)
        {
            float bassGain = 0, midGain = 0, highGain = 0;

            switch (CurrentPreset)
            {
                case EqPreset.BassBoost:
                    bassGain = 8.0f;   // +8dB
                    midGain = 0f;
                    highGain = -2.0f;  // -2dB
                    break;
                case EqPreset.VocalBoost:
                    bassGain = -2.0f;
                    midGain = 6.0f;
                    highGain = 2.0f;
                    break;
                case EqPreset.NightMode:
                    bassGain = -6.0f;  // Reduce bass significantly
                    midGain = 2.0f;    // Boost vocals slightly
                    highGain = -10.0f; // Cut harsh highs
                    break;
                default:
                    return;
            }

            // Low Shelf @ 200Hz
            _bassFilterL.SetLowShelf(sampleRate, 200, 0.707f, bassGain);
            _bassFilterR.SetLowShelf(sampleRate, 200, 0.707f, bassGain);
            
            // Peaking EQ @ 1000Hz
            _midFilterL.SetPeakingEq(sampleRate, 1000, 1.0f, midGain);
            _midFilterR.SetPeakingEq(sampleRate, 1000, 1.0f, midGain);
            
            // High Shelf @ 4000Hz
            _highFilterL.SetHighShelf(sampleRate, 4000, 0.707f, highGain);
            _highFilterR.SetHighShelf(sampleRate, 4000, 0.707f, highGain);
        }

        public void SetPreset(string presetName)
        {
            if (Enum.TryParse<EqPreset>(presetName, true, out var preset))
            {
                if (CurrentPreset != preset)
                {
                    CurrentPreset = preset;
                    IsEnabled = (preset != EqPreset.None);
                    // Reset filters states to avoid clicks/pops
                    _bassFilterL.Reset(); _bassFilterR.Reset();
                    _midFilterL.Reset(); _midFilterR.Reset();
                    _highFilterL.Reset(); _highFilterR.Reset();
                }
            }
        }

        private class BiquadFilter
        {
            private float a0, a1, a2, b1, b2;
            private float x1, x2, y1, y2;

            public void Reset() { x1 = x2 = y1 = y2 = 0; }

            public float Process(float x)
            {
                float y = a0 * x + a1 * x1 + a2 * x2 - b1 * y1 - b2 * y2;
                x2 = x1;
                x1 = x;
                y2 = y1;
                y1 = y;
                return y;
            }

            public void SetLowShelf(float sampleRate, float frequency, float q, float dbGain)
            {
                float w0 = 2.0f * (float)Math.PI * frequency / sampleRate;
                float A = (float)Math.Pow(10, dbGain / 40.0f);
                float alpha = (float)Math.Sin(w0) / 2.0f * (float)Math.Sqrt((A + 1.0f / A) * (1.0f / q - 1.0f) + 2.0f);

                float cosW0 = (float)Math.Cos(w0);
                float sqrtA2Alpha = 2.0f * (float)Math.Sqrt(A) * alpha;

                float norm = 1.0f / ((A + 1.0f) + (A - 1.0f) * cosW0 + sqrtA2Alpha);
                a0 = A * ((A + 1.0f) - (A - 1.0f) * cosW0 + sqrtA2Alpha) * norm;
                a1 = 2.0f * A * ((A - 1.0f) - (A + 1.0f) * cosW0) * norm;
                a2 = A * ((A + 1.0f) - (A - 1.0f) * cosW0 - sqrtA2Alpha) * norm;
                b1 = -2.0f * ((A - 1.0f) + (A + 1.0f) * cosW0) * norm;
                b2 = ((A + 1.0f) + (A - 1.0f) * cosW0 - sqrtA2Alpha) * norm;
            }

            public void SetHighShelf(float sampleRate, float frequency, float q, float dbGain)
            {
                float w0 = 2.0f * (float)Math.PI * frequency / sampleRate;
                float A = (float)Math.Pow(10, dbGain / 40.0f);
                float alpha = (float)Math.Sin(w0) / 2.0f * (float)Math.Sqrt((A + 1.0f / A) * (1.0f / q - 1.0f) + 2.0f);

                float cosW0 = (float)Math.Cos(w0);
                float sqrtA2Alpha = 2.0f * (float)Math.Sqrt(A) * alpha;

                float norm = 1.0f / ((A + 1.0f) - (A - 1.0f) * cosW0 + sqrtA2Alpha);
                a0 = A * ((A + 1.0f) + (A - 1.0f) * cosW0 + sqrtA2Alpha) * norm;
                a1 = -2.0f * A * ((A - 1.0f) + (A + 1.0f) * cosW0) * norm;
                a2 = A * ((A + 1.0f) + (A - 1.0f) * cosW0 - sqrtA2Alpha) * norm;
                b1 = 2.0f * ((A - 1.0f) - (A + 1.0f) * cosW0) * norm;
                b2 = ((A + 1.0f) - (A - 1.0f) * cosW0 - sqrtA2Alpha) * norm;
            }

            public void SetPeakingEq(float sampleRate, float frequency, float q, float dbGain)
            {
                float w0 = 2.0f * (float)Math.PI * frequency / sampleRate;
                float alpha = (float)Math.Sin(w0) / (2.0f * q);
                float A = (float)Math.Pow(10, dbGain / 40.0f);

                float norm = 1.0f / (1.0f + alpha / A);
                a0 = (1.0f + alpha * A) * norm;
                a1 = -2.0f * (float)Math.Cos(w0) * norm;
                a2 = (1.0f - alpha * A) * norm;
                b1 = a1;
                b2 = (1.0f - alpha / A) * norm;
            }
        }
    }
}
