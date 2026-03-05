package com.example.audiooverlan.audio;

import android.media.MediaCodec;
import android.media.MediaFormat;
import android.util.Log;

import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/**
 * Production-ready Opus decoder using Android MediaCodec.
 * Compatible with AudioServer v2.0 Opus packets.
 *
 * Features:
 * - Proper error handling and recovery
 * - Statistics tracking
 * - Auto-reset on errors
 * - Buffer management
 */
public class OpusDecoder {
    private static final String TAG = "OpusDecoder";

    private MediaCodec decoder;
    private final int sampleRate;
    private final int channels;

    // Statistics
    private long totalFramesDecoded = 0;
    private long totalBytesDecoded = 0;
    private long decodeErrors = 0;
    private long resetCount = 0;

    // State
    private boolean isInitialized = false;

    /**
     * Callback interface for decoded PCM data
     */
    public interface OnDecodedListener {
        void onDecoded(ByteBuffer pcmData, int size);
    }

    /**
     * Create Opus decoder
     * @param sampleRate Sample rate (typically 48000)
     * @param channels Number of channels (1=mono, 2=stereo)
     */
    public OpusDecoder(int sampleRate, int channels) throws IOException {
        this.sampleRate = sampleRate;
        this.channels = channels;
        initDecoder();
    }

    /**
     * Initialize MediaCodec Opus decoder
     */
    private void initDecoder() throws IOException {
        try {
            // Create Opus audio format
            MediaFormat format = MediaFormat.createAudioFormat(
                    MediaFormat.MIMETYPE_AUDIO_OPUS,
                    sampleRate,
                    channels
            );

            // Create OpusHead header (required for MediaCodec)
            // Format: "OpusHead" + version + channels + pre-skip + sample rate + gain + channel mapping
            ByteBuffer csd0 = ByteBuffer.allocate(19).order(ByteOrder.LITTLE_ENDIAN);
            csd0.put("OpusHead".getBytes());  // Magic signature (8 bytes)
            csd0.put((byte) 1);                // Version (1 byte)
            csd0.put((byte) channels);         // Channel count (1 byte)
            csd0.putShort((short) 312);        // Pre-skip: 312 samples @ 48kHz = 6.5ms (2 bytes)
            csd0.putInt(sampleRate);           // Original sample rate (4 bytes)
            csd0.putShort((short) 0);          // Output gain in dB (2 bytes)
            csd0.put((byte) 0);                // Channel mapping family (1 byte)
            csd0.flip();
            format.setByteBuffer("csd-0", csd0);

            // Empty csd-1 and csd-2 (required by some devices)
            ByteBuffer emptyCsd = ByteBuffer.allocate(8).order(ByteOrder.nativeOrder());
            emptyCsd.putLong(0L);
            emptyCsd.flip();
            format.setByteBuffer("csd-1", emptyCsd.duplicate());
            format.setByteBuffer("csd-2", emptyCsd.duplicate());

            // Create and configure decoder
            decoder = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_AUDIO_OPUS);
            decoder.configure(format, null, null, 0);
            decoder.start();

            isInitialized = true;
            Log.i(TAG, "Opus decoder initialized: " + sampleRate + "Hz, " + channels + " channels");

        } catch (Exception e) {
            isInitialized = false;
            Log.e(TAG, "Failed to initialize decoder", e);
            throw new IOException("Opus decoder initialization failed", e);
        }
    }

    private final MediaCodec.BufferInfo bufferInfo = new MediaCodec.BufferInfo();

    /**
     * Decode Opus packet to PCM
     * @param data Opus encoded data
     * @param length Length of valid data in the array
     * @param presentationTimeUs Presentation timestamp in microseconds
     * @param listener Callback for decoded PCM data
     */
    public void decode(byte[] data, int length, long presentationTimeUs, OnDecodedListener listener) {
        if (!isInitialized || decoder == null) {
            Log.w(TAG, "Decoder not initialized, attempting reset...");
            reset();
            if (!isInitialized) {
                Log.e(TAG, "Cannot decode: decoder initialization failed");
                return;
            }
        }

        try {
            // Queue input buffer
            int inputIndex = decoder.dequeueInputBuffer(10000); // 10ms timeout
            if (inputIndex >= 0) {
                ByteBuffer inputBuffer = decoder.getInputBuffer(inputIndex);
                if (inputBuffer != null) {
                    inputBuffer.clear();

                    if (data != null && length > 0) {
                        // Copy only the valid data
                        inputBuffer.put(data, 0, length);
                        decoder.queueInputBuffer(inputIndex, 0, length, presentationTimeUs, 0);
                    } else {
                        // Empty packet (silence or error)
                        decoder.queueInputBuffer(inputIndex, 0, 0, presentationTimeUs, 0);
                    }
                } else {
                    Log.w(TAG, "Input buffer is null");
                }
            } else {
                Log.w(TAG, "No input buffer available: " + inputIndex);
            }

            // Dequeue output buffers
            int outputIndex = decoder.dequeueOutputBuffer(bufferInfo, 10000); // 10ms timeout

            int outputCount = 0;
            while (outputIndex >= 0 && outputCount < 10) { // Limit iterations to prevent infinite loop
                ByteBuffer outputBuffer = decoder.getOutputBuffer(outputIndex);

                if (outputBuffer != null && bufferInfo.size > 0) {
                    // Deliver decoded PCM data
                    listener.onDecoded(outputBuffer, bufferInfo.size);

                    // Update statistics
                    totalFramesDecoded++;
                    totalBytesDecoded += bufferInfo.size;
                }

                decoder.releaseOutputBuffer(outputIndex, false);
                outputIndex = decoder.dequeueOutputBuffer(bufferInfo, 0); // Non-blocking
                outputCount++;
            }

        } catch (IllegalStateException e) {
            Log.e(TAG, "Decoder in illegal state", e);
            decodeErrors++;
            reset();
        } catch (Exception e) {
            Log.e(TAG, "Decoding error: " + e.getMessage(), e);
            decodeErrors++;

            // Reset if too many errors
            if (decodeErrors % 10 == 0) {
                Log.w(TAG, "Multiple decode errors (" + decodeErrors + "), resetting decoder");
                reset();
            }
        }
    }

    /**
     * Flush decoder buffers (call when seeking or after packet loss)
     */
    public void flush() {
        try {
            if (decoder != null && isInitialized) {
                decoder.flush();
                Log.d(TAG, "Decoder flushed");
            }
        } catch (Exception e) {
            Log.e(TAG, "Flush error", e);
        }
    }

    /**
     * Reset decoder (recreate after errors)
     */
    public void reset() {
        Log.i(TAG, "Resetting decoder...");
        release();

        try {
            initDecoder();
            resetCount++;
            Log.i(TAG, "Decoder reset successful (count: " + resetCount + ")");
        } catch (IOException e) {
            Log.e(TAG, "Failed to reset decoder", e);
            isInitialized = false;
        }
    }

    /**
     * Release decoder resources
     */
    public void release() {
        if (decoder != null) {
            try {
                decoder.stop();
                decoder.release();
                Log.i(TAG, "Decoder released");
            } catch (Exception e) {
                Log.e(TAG, "Error releasing decoder", e);
            } finally {
                decoder = null;
                isInitialized = false;
            }
        }
    }

    /**
     * Get decoder statistics
     */
    public DecoderStatistics getStatistics() {
        DecoderStatistics stats = new DecoderStatistics();
        stats.totalFramesDecoded = totalFramesDecoded;
        stats.totalBytesDecoded = totalBytesDecoded;
        stats.decodeErrors = decodeErrors;
        stats.resetCount = resetCount;
        stats.isInitialized = isInitialized;
        stats.errorRate = totalFramesDecoded > 0
                ? (decodeErrors * 100.0 / totalFramesDecoded)
                : 0;
        return stats;
    }

    /**
     * Check if decoder is ready
     */
    public boolean isReady() {
        return isInitialized && decoder != null;
    }

    /**
     * Statistics container
     */
    public static class DecoderStatistics {
        public long totalFramesDecoded;
        public long totalBytesDecoded;
        public long decodeErrors;
        public long resetCount;
        public boolean isInitialized;
        public double errorRate;

        @Override
        public String toString() {
            return String.format(
                    "OpusDecoder Stats: frames=%d, bytes=%d, errors=%d (%.2f%%), resets=%d, ready=%s",
                    totalFramesDecoded, totalBytesDecoded, decodeErrors, errorRate, resetCount, isInitialized
            );
        }
    }
}
