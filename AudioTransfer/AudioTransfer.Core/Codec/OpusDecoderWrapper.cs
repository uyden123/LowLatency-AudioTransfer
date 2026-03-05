using System;
using Concentus.Structs;
using Concentus.Enums;

namespace AudioTransfer.Core.Codec
{
    /// <summary>
    /// Opus decoder wrapper using Concentus with PLC and FEC support.
    /// Mirrors the Android OpusCodec decoder capabilities.
    /// </summary>
    public sealed class OpusDecoderWrapper : IDisposable
    {
        private readonly OpusDecoder _decoder;
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly int _frameSizeSamples;     // samples per channel per frame
        private readonly int _frameSizeInterleaved;  // total samples per frame
        private readonly short[] _decodeBuffer;

        private bool _lastPacketWasLost;

        // Statistics
        private long _totalFramesDecoded;
        private long _plcFrames;
        private long _fecFrames;
        private long _decodeErrors;
        private long _resetCount;

        /// <summary>
        /// Create Opus decoder.
        /// </summary>
        /// <param name="sampleRate">Sample rate (48000 recommended for Opus)</param>
        /// <param name="channels">Number of channels (1=mono, 2=stereo)</param>
        /// <param name="frameSizeMs">Frame duration in ms (20 recommended)</param>
        public OpusDecoderWrapper(int sampleRate = 48000, int channels = 1, int frameSizeMs = 20)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            _frameSizeSamples = sampleRate * frameSizeMs / 1000;
            _frameSizeInterleaved = _frameSizeSamples * channels;

            _decodeBuffer = new short[_frameSizeInterleaved];

            _decoder = new OpusDecoder(sampleRate, channels);
            CoreLogger.Instance.Log($"[OpusDecoder] Initialized: {sampleRate}Hz, {channels}ch, {frameSizeMs}ms frames");
        }

        /// <summary>
        /// Decode an Opus packet to PCM 16-bit interleaved audio.
        /// </summary>
        public short[]? Decode(byte[] opusData, int offset, int length)
        {
            try
            {
                int samplesDecoded;

                if (opusData == null || length <= 0)
                {
                    // PLC (Packet Loss Concealment)
                    samplesDecoded = _decoder.Decode(null, 0, 0, _decodeBuffer, 0, _frameSizeSamples, false);
                    _plcFrames++;
                    _lastPacketWasLost = true;
                }
                else
                {
                    // FEC recovery attempt if previous packet was lost
                    if (_lastPacketWasLost)
                    {
                        try
                        {
                            _decoder.Decode(opusData, offset, length, _decodeBuffer, 0, _frameSizeSamples, true);
                            _fecFrames++;
                        }
                        catch
                        {
                            // FEC failed, continue with normal decode
                        }
                    }

                    // Normal decode
                    samplesDecoded = _decoder.Decode(opusData, offset, length, _decodeBuffer, 0, _frameSizeSamples, false);
                    _lastPacketWasLost = false;
                }

                if (samplesDecoded > 0)
                {
                    _totalFramesDecoded++;
                    int totalSamples = samplesDecoded * _channels;
                    short[] output = new short[totalSamples];
                    Array.Copy(_decodeBuffer, 0, output, 0, totalSamples);
                    return output;
                }

                return null;
            }
            catch (Exception ex)
            {
                _decodeErrors++;
                return null;
            }
        }

        /// <summary>
        /// Decode Opus packet into a pre-allocated buffer (Zero-allocation).
        /// </summary>
        /// <returns>Number of samples per channel decoded, or -1 on error</returns>
        public int DecodeTo(byte[] opusData, int offset, int length, short[] outPcm)
        {
            try
            {
                int samplesDecoded;
                if (opusData == null || length <= 0)
                {
                    samplesDecoded = _decoder.Decode(null, 0, 0, outPcm, 0, _frameSizeSamples, false);
                    _plcFrames++;
                    _lastPacketWasLost = true;
                }
                else
                {
                    if (_lastPacketWasLost)
                    {
                        try { _decoder.Decode(opusData, offset, length, outPcm, 0, _frameSizeSamples, true); _fecFrames++; } catch { }
                    }
                    samplesDecoded = _decoder.Decode(opusData, offset, length, outPcm, 0, _frameSizeSamples, false);
                    _lastPacketWasLost = false;
                }

                if (samplesDecoded > 0) _totalFramesDecoded++;
                return samplesDecoded;
            }
            catch
            {
                _decodeErrors++;
                return -1;
            }
        }

        public short[] InternalBuffer => _decodeBuffer;

        /// <summary>
        /// Generate PLC audio for a lost packet.
        /// </summary>
        public int DecodePLCTo(short[] outPcm) => DecodeTo(null!, 0, 0, outPcm);

        /// <summary>
        /// Reset decoder state.
        /// </summary>
        public void Reset()
        {
            try
            {
                _decoder.ResetState();
                _lastPacketWasLost = false;
                _resetCount++;
                CoreLogger.Instance.Log($"[OpusDecoder] Reset (count: {_resetCount})");
            }
            catch (Exception ex)
            {
                CoreLogger.Instance.Log($"[OpusDecoder] Reset failed: {ex.Message}");
            }
        }

        public int SampleRate => _sampleRate;
        public int Channels => _channels;
        public int FrameSizeSamples => _frameSizeSamples;
        public int FrameSizeInterleaved => _frameSizeInterleaved;
        public long TotalFramesDecoded => _totalFramesDecoded;
        public long PlcFrames => _plcFrames;
        public long FecFrames => _fecFrames;
        public long DecodeErrors => _decodeErrors;

        public void Dispose()
        {
            CoreLogger.Instance.Log($"[OpusDecoder] Disposed. Decoded: {_totalFramesDecoded}, PLC: {_plcFrames}, FEC: {_fecFrames}, Errors: {_decodeErrors}");
        }
    }
}
