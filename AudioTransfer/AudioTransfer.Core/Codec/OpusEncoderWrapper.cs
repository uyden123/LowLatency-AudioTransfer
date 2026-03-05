using System;
using System.Collections.Generic;
using Concentus.Structs;
using Concentus.Enums;

namespace AudioTransfer.Core.Codec
{
    /// <summary>
    /// Production-ready Opus encoder wrapper using Concentus.
    /// Encodes 16-bit interleaved PCM into Opus packets with configurable settings.
    /// Compatible with AudioServer v2.0 packet format.
    /// </summary>
    public sealed class OpusEncoderWrapper : IDisposable
    {
        private readonly OpusEncoder _encoder;
        private readonly int _sampleRate = 48000; // Opus always uses 48kHz internally
        private readonly int _channels;
        private readonly int _frameSamples; // samples per channel per frame (e.g. 960 for 20ms @ 48kHz)
        private readonly int _frameMilliseconds;
        private readonly int _maxPacketBytes = 4000; // Max Opus packet size
        
        // Statistics
        private long _totalFramesEncoded = 0;
        private long _totalBytesEncoded = 0;

        public int Bitrate
        {
            get => _encoder.Bitrate;
            set
            {
                _encoder.Bitrate = value;
                CoreLogger.Instance.Log($"[OpusEncoder] Bitrate changed to {value / 1000}kbps");
            }
        }

        /// <summary>
        /// Create Opus encoder with specified settings
        /// </summary>
        /// <param name="channels">Number of channels (1=mono, 2=stereo)</param>
        /// <param name="bitrate">Target bitrate in bits/sec (default 128000)</param>
        /// <param name="frameMilliseconds">Frame size in ms: 2.5, 5, 10, 20, 40, 60 (default 20)</param>
        /// <param name="complexity">Encoding complexity 0-10 (default 10, higher=better quality)</param>
        /// <param name="enableFEC">Enable Forward Error Correction (default true)</param>
        /// <param name="enableDTX">Enable Discontinuous Transmission (default false)</param>
        public OpusEncoderWrapper(
            int channels, 
            int bitrate = 128000, 
            int frameMilliseconds = 20,
            int complexity = 10,
            bool enableFEC = true,
            bool enableDTX = false)
        {
            if (channels != 1 && channels != 2)
                throw new ArgumentOutOfRangeException(nameof(channels), "Channels must be 1 or 2");
            
            if (!IsValidFrameSize(frameMilliseconds))
                throw new ArgumentOutOfRangeException(nameof(frameMilliseconds), 
                    "Frame size must be 2.5, 5, 10, 20, 40, or 60 ms");

            _channels = channels;
            _frameMilliseconds = frameMilliseconds;
            _frameSamples = _sampleRate * frameMilliseconds / 1000;

            // Create encoder
            _encoder = new OpusEncoder(_sampleRate, _channels, OpusApplication.OPUS_APPLICATION_AUDIO);
            
            // Configure encoder settings
            try
            {
                _encoder.Bitrate = bitrate;
                _encoder.Complexity = Math.Clamp(complexity, 0, 10);
                _encoder.UseInbandFEC = enableFEC;
                _encoder.UseDTX = enableDTX;
                
                // Set signal type for better quality
                _encoder.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC;
                
                CoreLogger.Instance.Log($"[OpusEncoder] Initialized: {_sampleRate}Hz, {_channels}ch, " +
                                $"{bitrate / 1000}kbps, {frameMilliseconds}ms frames, " +
                                $"complexity={complexity}, FEC={enableFEC}, DTX={enableDTX}");
            }
            catch (Exception ex)
            {
                AudioTransfer.Core.Logging.CoreLogger.Instance.Log($"[OpusEncoder] Warning: Some settings not applied: {ex.Message}");
            }
        }

        /// <summary>
        /// Encode interleaved 16-bit PCM samples into Opus frames.
        /// Returns a sequence of encoded Opus packets (byte arrays).
        /// Only complete frames are encoded; partial frames are ignored.
        /// </summary>
        /// <param name="interleavedPcm">Interleaved 16-bit PCM samples</param>
        /// <returns>Enumerable of encoded Opus packets</returns>
        [Obsolete]
        public IEnumerable<byte[]> Encode(short[] interleavedPcm)
        {
            if (interleavedPcm == null || interleavedPcm.Length == 0)
                yield break;

            int samplesPerFrameInterleaved = _frameSamples * _channels;
            var tmp = new byte[_maxPacketBytes];

            for (int offset = 0; offset + samplesPerFrameInterleaved <= interleavedPcm.Length; 
                 offset += samplesPerFrameInterleaved)
            {
                int encoded = 0;
                bool encodeSuccess = false;
                try
                {
                    // Encode one frame
                    encoded = _encoder.Encode(
                        interleavedPcm, 
                        offset, 
                        _frameSamples, 
                        tmp, 
                        0, 
                        tmp.Length
                    );
                    encodeSuccess = encoded > 0;
                    if (encoded < 0)
                    {
                        AudioTransfer.Core.Logging.CoreLogger.Instance.Log($"[OpusEncoder] Encode error: {encoded}");
                    }
                }
                catch (Exception ex)
                {
                    AudioTransfer.Core.Logging.CoreLogger.Instance.Log($"[OpusEncoder] Exception during encoding: {ex.Message}");
                }

                if (encodeSuccess)
                {
                    // Copy to output buffer
                    var outb = new byte[encoded];
                    Buffer.BlockCopy(tmp, 0, outb, 0, encoded);

                    // Update statistics
                    _totalFramesEncoded++;
                    _totalBytesEncoded += encoded;

                    yield return outb;
                }
            }
        }

        /// <summary>
        /// Get encoding statistics
        /// </summary>
        public EncoderStatistics GetStatistics()
        {
            return new EncoderStatistics
            {
                TotalFramesEncoded = _totalFramesEncoded,
                TotalBytesEncoded = _totalBytesEncoded,
                AverageBytesPerFrame = _totalFramesEncoded > 0 
                    ? (double)_totalBytesEncoded / _totalFramesEncoded 
                    : 0,
                AverageBitrate = _totalFramesEncoded > 0
                    ? (int)(_totalBytesEncoded * 8 * 1000 / (_totalFramesEncoded * _frameMilliseconds))
                    : 0
            };
        }

        /// <summary>
        /// Reset encoder state (useful after errors)
        /// </summary>
        public void Reset()
        {
            try
            {
                _encoder.ResetState();
                AudioTransfer.Core.Logging.CoreLogger.Instance.Log("[OpusEncoder] State reset");
            }
            catch (Exception ex)
            {
                AudioTransfer.Core.Logging.CoreLogger.Instance.Log($"[OpusEncoder] Reset error: {ex.Message}");
            }
        }

        private static bool IsValidFrameSize(int ms)
        {
            // Opus supports: 2.5, 5, 10, 20, 40, 60 ms
            return ms == 20 || ms == 10 || ms == 40 || ms == 60 || ms == 5;
            // Note: 2.5ms requires special handling, omitted for simplicity
        }

        public void Dispose()
        {
            // Log final statistics
            var stats = GetStatistics();
            CoreLogger.Instance.Log($"[OpusEncoder] Disposed. Total frames: {stats.TotalFramesEncoded}, " +
                            $"bytes: {stats.TotalBytesEncoded}, avg bitrate: {stats.AverageBitrate / 1000}kbps");
        }

        public class EncoderStatistics
        {
            public long TotalFramesEncoded { get; set; }
            public long TotalBytesEncoded { get; set; }
            public double AverageBytesPerFrame { get; set; }
            public int AverageBitrate { get; set; }
        }
    }
}
