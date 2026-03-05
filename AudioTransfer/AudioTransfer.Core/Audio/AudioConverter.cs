using System;
using System.IO;

namespace AudioTransfer.Core.Audio
{
    /// <summary>
    /// Centralized audio format conversion utilities.
    /// Handles PCM ↔ float conversion, channel mixing, resampling, and bit-depth changes.
    /// </summary>
    public static class AudioConverter
    {
        /// <summary>
        /// Full pipeline: convert raw PCM buffer between formats (sample rate, channels, bit depth).
        /// </summary>
        public static byte[] ConvertBuffer(
            byte[] buffer, int bytesRecorded,
            int inSampleRate, int inChannels, int inBits,
            int outSampleRate, int outChannels, int outBits)
        {
            int inBytesPerSample = inBits / 8;
            int inFrameCount = bytesRecorded / (inBytesPerSample * inChannels);

            var floats = PcmToFloat(buffer, inFrameCount, inChannels, inBits);
            var chanConverted = ConvertChannels(floats, inFrameCount, inChannels, outChannels);

            float[] resampled;
            int frameCountAfterResample;
            if (inSampleRate == outSampleRate)
            {
                resampled = chanConverted;
                frameCountAfterResample = inFrameCount;
            }
            else
            {
                resampled = ResampleLinear(chanConverted, inFrameCount, inSampleRate, outSampleRate,
                                          outChannels, out frameCountAfterResample);
            }

            return FloatToPcmBytes(resampled, frameCountAfterResample, outChannels, outBits);
        }

        /// <summary>
        /// Convert PCM byte buffer to normalized float array [-1.0, 1.0].
        /// Supports 8-bit, 16-bit, and 32-bit float samples.
        /// </summary>
        public static float[] PcmToFloat(byte[] buffer, int frameCount, int channels, int bits)
        {
            var floats = new float[frameCount * channels];
            int offset = 0;
            int bytesPerSample = bits / 8;

            for (int i = 0; i < floats.Length; i++)
            {
                floats[i] = bits switch
                {
                    32 => BitConverter.ToSingle(buffer, offset),
                    16 => BitConverter.ToInt16(buffer, offset) / 32768f,
                    8 => (buffer[offset] - 128) / 128f,
                    _ => throw new NotSupportedException($"Unsupported bit depth: {bits}")
                };
                offset += bytesPerSample;
            }

            return floats;
        }

        /// <summary>
        /// Convert between mono and stereo (or arbitrary channel counts).
        /// Stereo→Mono: average L+R. Mono→Stereo: duplicate.
        /// </summary>
        public static float[] ConvertChannels(float[] input, int frameCount, int inChannels, int outChannels)
        {
            if (inChannels == outChannels) return input;

            var output = new float[frameCount * outChannels];

            for (int frame = 0; frame < frameCount; frame++)
            {
                int inBase = frame * inChannels;
                int outBase = frame * outChannels;

                if (inChannels == 2 && outChannels == 1)
                {
                    output[outBase] = (input[inBase] + input[inBase + 1]) * 0.5f;
                }
                else if (inChannels == 1 && outChannels == 2)
                {
                    output[outBase] = output[outBase + 1] = input[inBase];
                }
                else
                {
                    for (int ch = 0; ch < outChannels; ch++)
                        output[outBase + ch] = input[inBase + (ch % inChannels)];
                }
            }

            return output;
        }

        /// <summary>
        /// Simple linear interpolation resampler.
        /// </summary>
        public static float[] ResampleLinear(float[] input, int inFrameCount, int inSampleRate,
                                             int outSampleRate, int channels, out int outFrameCount)
        {
            if (inSampleRate == outSampleRate)
            {
                outFrameCount = inFrameCount;
                return input;
            }

            double ratio = (double)outSampleRate / inSampleRate;
            outFrameCount = Math.Max(1, (int)Math.Round(inFrameCount * ratio));
            var output = new float[outFrameCount * channels];

            for (int outFrame = 0; outFrame < outFrameCount; outFrame++)
            {
                double inPos = outFrame / ratio;
                int idx = (int)Math.Floor(inPos);
                double frac = inPos - idx;
                int idxNext = Math.Min(idx + 1, inFrameCount - 1);

                for (int ch = 0; ch < channels; ch++)
                {
                    float s1 = input[idx * channels + ch];
                    float s2 = input[idxNext * channels + ch];
                    output[outFrame * channels + ch] = (float)(s1 + (s2 - s1) * frac);
                }
            }

            return output;
        }

        /// <summary>
        /// Convert normalized float array back to PCM bytes (16-bit or 32-bit float).
        /// </summary>
        public static byte[] FloatToPcmBytes(float[] floats, int frameCount, int channels, int bits)
        {
            int totalSamples = frameCount * channels;
            byte[] result;

            if (bits == 16)
            {
                result = new byte[totalSamples * 2];
                for (int i = 0; i < totalSamples; i++)
                {
                    var v = Math.Clamp(floats[i], -1f, 1f);
                    short s = (short)(v * 32767f);
                    result[i * 2] = (byte)(s & 0xFF);
                    result[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
                }
            }
            else if (bits == 32)
            {
                result = new byte[totalSamples * 4];
                Buffer.BlockCopy(floats, 0, result, 0, result.Length);
            }
            else
            {
                throw new NotSupportedException($"Unsupported bit depth: {bits}");
            }

            return result;
        }

        /// <summary>
        /// Convert float[] to short[] with proper clamping for Opus encoder input.
        /// </summary>
        public static short[] FloatToShort(float[] floats)
        {
            var shorts = new short[floats.Length];
            for (int i = 0; i < shorts.Length; i++)
            {
                float sample = floats[i] * 32768f;
                if (sample > 32767f) sample = 32767f;
                else if (sample < -32768f) sample = -32768f;
                shorts[i] = (short)sample;
            }
            return shorts;
        }
    }
}
