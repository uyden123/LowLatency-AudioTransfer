package com.example.audiooverlan.audio;

import android.util.Log;

/**
 * JNI wrapper for native AAudio recorder using Google Oboe.
 * Provides lowest possible audio capture latency.
 *
 * Falls back to OpenSLES on devices that don't support AAudio (API < 27).
 */
public class AAudioRecorder {

    private static final String TAG = "AAudioRecorder";
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
     * Start the audio capture stream.
     * @param sampleRate Audio sample rate (e.g., 48000)
     * @param channelCount Number of channels (1 or 2)
     * @param exclusive Whether to request EXCLUSIVE sharing mode
     * @return true if stream started successfully
     */
    public boolean start(int sampleRate, int channelCount, boolean exclusive) {
        if (!nativeLibLoaded) {
            Log.e(TAG, "Cannot start: native library not loaded");
            return false;
        }

        try {
            boolean result = nativeStart(sampleRate, channelCount, exclusive);
            isStarted = result;
            if (result) {
                Log.i(TAG, "Recorder started. Info: " + getStreamInfo());
            }
            return result;
        } catch (Exception e) {
            Log.e(TAG, "Failed to start recorder", e);
            return false;
        }
    }

    /**
     * Read PCM samples from the native ring buffer.
     * @param samples Short array to store PCM samples
     * @param offset Start offset in the array
     * @param length Number of samples to read
     * @return Number of samples actually read, or -1 on error
     */
    public int read(short[] samples, int offset, int length) {
        if (!isStarted) return -1;
        try {
            return nativeRead(samples, offset, length);
        } catch (Exception e) {
            Log.e(TAG, "Read error", e);
            return -1;
        }
    }

    /**
     * Stop and release the audio stream.
     */
    public synchronized void stop() {
        if (!isStarted) return;
        isStarted = false;
        if (!nativeLibLoaded) return;
        try {
            nativeStop();
            Log.i(TAG, "Recorder stopped");
        } catch (Exception e) {
            Log.e(TAG, "Stop error", e);
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
            return "Error getting info";
        }
    }

    public boolean isStarted() {
        return isStarted;
    }

    // Native methods
    private native boolean nativeStart(int sampleRate, int channelCount, boolean exclusive);
    private native int nativeRead(short[] samples, int offset, int length);
    private native void nativeStop();
    private native boolean nativeIsAAudioSupported();
    private native String nativeGetStreamInfo();
}
