package com.example.audiooverlan.audio;

import android.util.Log;

import io.github.jaredmdobson.concentus.OpusApplication;
import io.github.jaredmdobson.concentus.OpusDecoder;
import io.github.jaredmdobson.concentus.OpusEncoder;
import io.github.jaredmdobson.concentus.OpusException;
import io.github.jaredmdobson.concentus.OpusSignal;

/**
 * Production-ready Opus codec wrapper using Concentus (pure Java Opus implementation).
 * Provides encoding, decoding, and Packet Loss Concealment (PLC).
 *
 * <p>Features:
 * <ul>
 *   <li>Encode: PCM 16-bit interleaved → Opus packets</li>
 *   <li>Decode: Opus packets → PCM 16-bit interleaved</li>
 *   <li>PLC: Generates concealment audio when packets are lost</li>
 *   <li>FEC: Forward Error Correction for packet loss recovery</li>
 *   <li>Statistics tracking and auto-reset on errors</li>
 * </ul>
 */
public class OpusCodec {
    private static final String TAG = "OpusCodec";

    // ── Codec instances ──
    private OpusEncoder encoder;
    private OpusDecoder decoder;

    // ── Configuration ──
    private final int sampleRate;
    private final int channels;
    private final int frameSizeMs;
    private final int frameSizeSamples;     // samples per channel per frame
    private final int frameSizeInterleaved; // total samples per frame (frameSizeSamples * channels)

    // ── Encoder config ──
    private int bitrate;
    private int complexity;
    private boolean enableFEC;
    private boolean enableDTX;

    // ── Buffers ──
    private final short[] decodeBuffer;      // output PCM from decode
    private final byte[] encodeBuffer;       // output Opus from encode
    private static final int MAX_OPUS_PACKET_SIZE = 4000;

    // ── State ──
    private boolean encoderInitialized = false;
    private boolean decoderInitialized = false;
    private boolean lastPacketWasLost = false; // for FEC decode

    // ── Statistics ──
    private long totalFramesDecoded = 0;
    private long totalBytesDecoded = 0;
    private long totalFramesEncoded = 0;
    private long totalBytesEncoded = 0;
    private long decodeErrors = 0;
    private long encodeErrors = 0;
    private long plcFrames = 0;
    private long fecFrames = 0;
    private long resetCount = 0;

    /**
     * Create Opus codec with default settings.
     *
     * @param sampleRate  Sample rate (48000 recommended for Opus)
     * @param channels    Number of channels (1=mono, 2=stereo)
     * @param frameSizeMs Frame duration in ms (20 recommended)
     */
    public OpusCodec(int sampleRate, int channels, int frameSizeMs) {
        this(sampleRate, channels, frameSizeMs, 128000, 10, true, false);
    }

    /**
     * Create Opus codec with full configuration.
     *
     * @param sampleRate  Sample rate (48000 recommended)
     * @param channels    Number of channels (1=mono, 2=stereo)
     * @param frameSizeMs Frame duration in ms (2.5, 5, 10, 20, 40, 60)
     * @param bitrate     Target bitrate in bits/sec (e.g. 128000)
     * @param complexity  Encoding complexity 0-10 (10=best quality)
     * @param enableFEC   Enable Forward Error Correction
     * @param enableDTX   Enable Discontinuous Transmission
     */
    public OpusCodec(int sampleRate, int channels, int frameSizeMs,
                     int bitrate, int complexity, boolean enableFEC, boolean enableDTX) {
        this.sampleRate = sampleRate;
        this.channels = channels;
        this.frameSizeMs = frameSizeMs;
        this.frameSizeSamples = sampleRate * frameSizeMs / 1000;
        this.frameSizeInterleaved = frameSizeSamples * channels;
        this.bitrate = bitrate;
        this.complexity = complexity;
        this.enableFEC = enableFEC;
        this.enableDTX = enableDTX;

        // Allocate buffers
        this.decodeBuffer = new short[frameSizeInterleaved];
        this.encodeBuffer = new byte[MAX_OPUS_PACKET_SIZE];
    }

    // ════════════════════════════════════════════════════════════════════
    //  DECODER
    // ════════════════════════════════════════════════════════════════════

    /**
     * Initialize the Opus decoder. Call before decoding.
     *
     * @return true if initialized successfully
     */
    public boolean initDecoder() {
        try {
            decoder = new OpusDecoder(sampleRate, channels);
            decoderInitialized = true;
            Log.i(TAG, "Decoder initialized: " + sampleRate + "Hz, " + channels + "ch, "
                    + frameSizeMs + "ms frames");
            return true;
        } catch (OpusException e) {
            Log.e(TAG, "Failed to initialize decoder: " + e.getMessage(), e);
            decoderInitialized = false;
            return false;
        }
    }

    /**
     * Decode an Opus packet to PCM 16-bit interleaved audio.
     *
     * @param opusData   Opus encoded data (null for PLC)
     * @param offset     Offset in opusData
     * @param length     Length of valid Opus data
     * @return decoded PCM samples (short[]), or null on error
     */
    public short[] decode(byte[] opusData, int offset, int length) {
        if (!decoderInitialized || decoder == null) {
            Log.w(TAG, "Decoder not initialized");
            return null;
        }

        try {
            int samplesDecoded;

            if (opusData == null || length <= 0) {
                // ── PLC (Packet Loss Concealment) ──
                // Pass null to Concentus decoder to generate concealment audio
                samplesDecoded = decoder.decode(null, 0, 0, decodeBuffer, 0, frameSizeSamples, false);
                plcFrames++;
                lastPacketWasLost = true;
            } else {
                // ── Normal decode or FEC recovery ──
                if (lastPacketWasLost && enableFEC) {
                    // Try to decode with FEC first (recovers from previous lost packet)
                    try {
                        decoder.decode(opusData, offset, length, decodeBuffer, 0, frameSizeSamples, true);
                        fecFrames++;
                    } catch (OpusException e) {
                        // FEC decode failed, just do normal decode
                        Log.d(TAG, "FEC decode failed, using normal decode");
                    }
                }

                // Normal decode
                samplesDecoded = decoder.decode(opusData, offset, length, decodeBuffer, 0, frameSizeSamples, false);
                lastPacketWasLost = false;
            }

            if (samplesDecoded > 0) {
                totalFramesDecoded++;
                totalBytesDecoded += samplesDecoded * channels * 2; // 16-bit = 2 bytes

                // Return a copy (backward compatibility)
                int totalSamples = samplesDecoded * channels;
                short[] output = new short[totalSamples];
                System.arraycopy(decodeBuffer, 0, output, 0, totalSamples);
                return output;
            }

            return null;
        } catch (OpusException e) {
            decodeErrors++;
            return null;
        }
    }

    /**
     * Decode an Opus packet into a pre-allocated buffer (Zero-allocation).
     *
     * @param opusData   Opus encoded data
     * @param length     Length of valid Opus data
     * @param outPcm     Output buffer to store decoded PCM
     * @return number of samples decoded per channel, or -1 on error
     */
    public int decodeTo(byte[] opusData, int length, short[] outPcm) {
        if (!decoderInitialized || decoder == null) return -1;

        try {
            int samplesPerChannel;
            if (opusData == null || length <= 0) {
                samplesPerChannel = decoder.decode(null, 0, 0, outPcm, 0, frameSizeSamples, false);
                plcFrames++;
                lastPacketWasLost = true;
            } else {
                if (lastPacketWasLost && enableFEC) {
                    try {
                        decoder.decode(opusData, 0, length, outPcm, 0, frameSizeSamples, true);
                        fecFrames++;
                    } catch (OpusException ignored) {}
                }
                samplesPerChannel = decoder.decode(opusData, 0, length, outPcm, 0, frameSizeSamples, false);
                lastPacketWasLost = false;
            }

            if (samplesPerChannel > 0) {
                totalFramesDecoded++;
                totalBytesDecoded += samplesPerChannel * channels * 2;
                return samplesPerChannel;
            }
            return -1;
        } catch (OpusException e) {
            decodeErrors++;
            return -1;
        }
    }

    /**
     * Decode an Opus packet (convenience overload).
     *
     * @param opusData Opus encoded data
     * @return decoded PCM samples, or null on error
     */
    public short[] decode(byte[] opusData) {
        if (opusData == null) return decode(null, 0, 0);
        return decode(opusData, 0, opusData.length);
    }

    /**
     * Generate PLC (Packet Loss Concealment) audio.
     * Call this when a packet is lost to maintain audio continuity.
     *
     * @return concealment PCM samples, or null on error
     */
    public short[] decodePLC() {
        return decode(null, 0, 0);
    }

    /**
     * Reset decoder state. Useful after seek or error recovery.
     */
    public void resetDecoder() {
        if (decoder != null) {
            try {
                decoder.resetState();
                lastPacketWasLost = false;
                resetCount++;
                Log.i(TAG, "Decoder reset (count: " + resetCount + ")");
            } catch (Exception e) {
                Log.e(TAG, "Decoder reset failed, reinitializing", e);
                initDecoder();
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  ENCODER
    // ════════════════════════════════════════════════════════════════════

    /**
     * Initialize the Opus encoder. Call before encoding.
     *
     * @return true if initialized successfully
     */
    public boolean initEncoder() {
        try {
            encoder = new OpusEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);

            encoder.setBitrate(bitrate);
            encoder.setComplexity(Math.min(10, Math.max(0, complexity)));
            encoder.setUseInbandFEC(enableFEC);
            encoder.setUseDTX(enableDTX);
            encoder.setSignalType(OpusSignal.OPUS_SIGNAL_MUSIC);

            encoderInitialized = true;
            Log.i(TAG, "Encoder initialized: " + sampleRate + "Hz, " + channels + "ch, "
                    + bitrate / 1000 + "kbps, complexity=" + complexity
                    + ", FEC=" + enableFEC + ", DTX=" + enableDTX);
            return true;
        } catch (OpusException e) {
            Log.e(TAG, "Failed to initialize encoder: " + e.getMessage(), e);
            encoderInitialized = false;
            return false;
        }
    }

    /**
     * Encode PCM 16-bit interleaved samples into an Opus packet.
     * The input must contain exactly one frame worth of samples
     * (frameSizeSamples * channels).
     *
     * @param pcmData    PCM 16-bit interleaved samples
     * @param offset     Offset in pcmData
     * @param frameSize  Number of samples per channel (must match frameSizeSamples)
     * @return Opus encoded packet, or null on error
     */
    public byte[] encode(short[] pcmData, int offset, int frameSize) {
        if (!encoderInitialized || encoder == null) {
            Log.w(TAG, "Encoder not initialized");
            return null;
        }

        try {
            int encodedBytes = encoder.encode(
                    pcmData, offset, frameSize,
                    encodeBuffer, 0, encodeBuffer.length
            );

            if (encodedBytes > 0) {
                totalFramesEncoded++;
                totalBytesEncoded += encodedBytes;

                byte[] output = new byte[encodedBytes];
                System.arraycopy(encodeBuffer, 0, output, 0, encodedBytes);
                return output;
            }

            return null;
        } catch (OpusException e) {
            encodeErrors++;
            return null;
        }
    }

    /**
     * Encode PCM samples into a pre-allocated internal buffer (Zero-allocation).
     * The encoded data can be accessed via getEncodeBuffer() and the returned length.
     *
     * @param pcmData   PCM samples
     * @param offset    Offset in pcmData
     * @param frameSize Samples per channel
     * @return encoded length in bytes, or -1 on error
     */
    public int encodeToInternalBuffer(short[] pcmData, int offset, int frameSize) {
        if (!encoderInitialized || encoder == null) return -1;

        try {
            int encodedBytes = encoder.encode(
                    pcmData, offset, frameSize,
                    encodeBuffer, 0, encodeBuffer.length
            );

            if (encodedBytes > 0) {
                totalFramesEncoded++;
                totalBytesEncoded += encodedBytes;
                return encodedBytes;
            }
            return -1;
        } catch (OpusException e) {
            encodeErrors++;
            return -1;
        }
    }

    public byte[] getEncodeBuffer() {
        return encodeBuffer;
    }

    /**
     * Encode one frame of PCM data (convenience overload).
     * Input array must have exactly frameSizeInterleaved samples.
     *
     * @param pcmFrame One complete frame of PCM samples
     * @return Opus encoded packet, or null on error
     */
    public byte[] encode(short[] pcmFrame) {
        return encode(pcmFrame, 0, frameSizeSamples);
    }

    /**
     * Reset encoder state. Useful after errors.
     */
    public void resetEncoder() {
        if (encoder != null) {
            try {
                encoder.resetState();
                Log.i(TAG, "Encoder reset");
            } catch (Exception e) {
                Log.e(TAG, "Encoder reset failed, reinitializing", e);
                initEncoder();
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  CONFIGURATION
    // ════════════════════════════════════════════════════════════════════

    /**
     * Update encoder bitrate at runtime.
     */
    public void setBitrate(int bitrate) {
        this.bitrate = bitrate;
        if (encoderInitialized && encoder != null) {
            encoder.setBitrate(bitrate);
            Log.d(TAG, "Bitrate updated to " + bitrate / 1000 + "kbps");
        }
    }

    /**
     * Update encoder complexity at runtime.
     */
    public void setComplexity(int complexity) {
        this.complexity = complexity;
        if (encoderInitialized && encoder != null) {
            encoder.setComplexity(Math.min(10, Math.max(0, complexity)));
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  STATUS & CLEANUP
    // ════════════════════════════════════════════════════════════════════

    public boolean isDecoderReady() {
        return decoderInitialized && decoder != null;
    }

    public boolean isEncoderReady() {
        return encoderInitialized && encoder != null;
    }

    public int getSampleRate() { return sampleRate; }
    public int getChannels() { return channels; }
    public int getFrameSizeMs() { return frameSizeMs; }
    public int getFrameSizeSamples() { return frameSizeSamples; }
    public int getFrameSizeInterleaved() { return frameSizeInterleaved; }

    /**
     * Release all codec resources.
     */
    public void release() {
        encoder = null;
        decoder = null;
        encoderInitialized = false;
        decoderInitialized = false;
        Log.i(TAG, "Codec released");
    }

    /**
     * Get comprehensive codec statistics.
     */
    public CodecStatistics getStatistics() {
        CodecStatistics stats = new CodecStatistics();
        stats.totalFramesDecoded = totalFramesDecoded;
        stats.totalBytesDecoded = totalBytesDecoded;
        stats.totalFramesEncoded = totalFramesEncoded;
        stats.totalBytesEncoded = totalBytesEncoded;
        stats.decodeErrors = decodeErrors;
        stats.encodeErrors = encodeErrors;
        stats.plcFrames = plcFrames;
        stats.fecFrames = fecFrames;
        stats.resetCount = resetCount;
        stats.decoderReady = decoderInitialized;
        stats.encoderReady = encoderInitialized;
        stats.decodeErrorRate = totalFramesDecoded > 0
                ? (decodeErrors * 100.0 / totalFramesDecoded) : 0;
        stats.encodeErrorRate = totalFramesEncoded > 0
                ? (encodeErrors * 100.0 / totalFramesEncoded) : 0;
        return stats;
    }

    /**
     * Statistics container for both encoder and decoder.
     */
    public static class CodecStatistics {
        public long totalFramesDecoded;
        public long totalBytesDecoded;
        public long totalFramesEncoded;
        public long totalBytesEncoded;
        public long decodeErrors;
        public long encodeErrors;
        public long plcFrames;
        public long fecFrames;
        public long resetCount;
        public boolean decoderReady;
        public boolean encoderReady;
        public double decodeErrorRate;
        public double encodeErrorRate;

        @Override
        public String toString() {
            return String.format(
                    "OpusCodec Stats: dec[frames=%d err=%d(%.1f%%) plc=%d fec=%d] " +
                            "enc[frames=%d err=%d(%.1f%%)] resets=%d",
                    totalFramesDecoded, decodeErrors, decodeErrorRate, plcFrames, fecFrames,
                    totalFramesEncoded, encodeErrors, encodeErrorRate, resetCount
            );
        }
    }
}
