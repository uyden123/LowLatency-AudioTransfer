package com.example.audiooverlan.audio;

import android.util.Log;

/**
 * JNI wrapper for native AAudio player using Google Oboe.
 * Provides lowest possible audio playback latency by bypassing
 * the Java AudioTrack layer and using AAudio directly.
 * 
 * Falls back to OpenSLES on devices that don't support AAudio (API < 27).
 */
public class AAudioPlayer {

    private static final String TAG = "AAudioPlayer";
    private static boolean nativeLibLoaded = false;
    private volatile boolean isStarted = false;

    static {
        try {
            System.loadLibrary("audiooverlan");
            nativeLibLoaded = true;
            Log.i(TAG, "Native library loaded successfully");
        } catch (UnsatisfiedLinkError e) {
            Log.e(TAG, "Failed to load native library", e);
            nativeLibLoaded = false;
        }
    }

    /**
     * Check if the native library is available.
     */
    public static boolean isAvailable() {
        return nativeLibLoaded;
    }

    /**
     * Check if AAudio API is supported on this device.
     * Requires API 27+ (Android 8.1).
     */
    public boolean isAAudioSupported() {
        if (!nativeLibLoaded) return false;
        try {
            return nativeIsAAudioSupported();
        } catch (Exception e) {
            Log.e(TAG, "Error checking AAudio support", e);
            return false;
        }
    }

    /**
     * Start the AAudio output stream.
     * @param sampleRate Audio sample rate (e.g., 48000)
     * @param channelCount Number of channels (1 or 2)
     * @param framesPerBuffer Desired frames per buffer, 0 for auto
     * @param exclusive Whether to request EXCLUSIVE sharing mode
     * @return true if stream started successfully
     */
    public boolean start(int sampleRate, int channelCount, int framesPerBuffer, boolean exclusive) {
        if (!nativeLibLoaded) {
            Log.e(TAG, "Cannot start: native library not loaded");
            return false;
        }

        try {
            boolean result = nativeStart(sampleRate, channelCount, framesPerBuffer, exclusive);
            isStarted = result;
            if (result) {
                Log.i(TAG, "Stream info: " + getStreamInfo());
            }
            return result;
        } catch (Exception e) {
            Log.e(TAG, "Failed to start", e);
            return false;
        }
    }

    /**
     * Write PCM samples to the native ring buffer.
     * @param samples Short array of PCM samples
     * @param offset Start offset in the array
     * @param length Number of samples to write
     * @return Number of samples actually written
     */
    public int write(short[] samples, int offset, int length) {
        if (!isStarted) return 0;
        try {
            return nativeWrite(samples, offset, length);
        } catch (Exception e) {
            Log.e(TAG, "Write error", e);
            return 0;
        }
    }

    /**
     * Stop and release the audio stream.
     */
    public void stop() {
        isStarted = false;
        if (!nativeLibLoaded) return;
        try {
            nativeStop();
        } catch (Exception e) {
            Log.e(TAG, "Stop error", e);
        }
    }

    /**
     * Get the current audio output latency in milliseconds.
     * @return Latency in ms, or -1 if unavailable
     */
    public double getLatencyMs() {
        if (!isStarted) return -1;
        try {
            return nativeGetLatencyMs();
        } catch (Exception e) {
            return -1;
        }
    }

    /**
     * Get stream info string for debugging.
     */
    public String getStreamInfo() {
        if (!isStarted) return "Not started";
        try {
            return nativeGetStreamInfo();
        } catch (Exception e) {
            return "Error";
        }
    }

    public boolean isStarted() {
        return isStarted;
    }

    // Native methods
    private native boolean nativeStart(int sampleRate, int channelCount, int framesPerBuffer, boolean exclusive);
    private native int nativeWrite(short[] samples, int offset, int length);
    private native void nativeStop();
    private native double nativeGetLatencyMs();
    private native boolean nativeIsAAudioSupported();
    private native String nativeGetStreamInfo();
}
