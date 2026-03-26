package com.example.audiooverlan.audio;

import android.util.Log;

public class NativeRNNoise {
    private static final String TAG = "NativeRNNoise";
    private long statePointer = 0;

    static {
        try {
            System.loadLibrary("rnnoise"); // Load the provided rnnoise lib
            System.loadLibrary("audiooverlan"); // Load our wrapper
            Log.i(TAG, "Native library 'rnnoise' loaded successfully.");
        } catch (UnsatisfiedLinkError e) {
            Log.e(TAG, "Failed to load native library 'rnnoise': " + e.getMessage());
        }
    }

    public NativeRNNoise() {
        statePointer = createNative();
        if (statePointer == 0) {
            Log.e(TAG, "Failed to create RNNoise state");
        } else {
            Log.i(TAG, "RNNoise state created successfully.");
        }
    }

    // Process a frame of 480 samples. Overwrites the outFrame with processed data.
    // RNNoise requires float array, but we can pass short array to C++ and do the conversion there to save GC overhead.
    public float processFrame(short[] inFrame, short[] outFrame) {
        if (statePointer == 0) return 0f;
        return processFrameNative(statePointer, inFrame, outFrame);
    }

    public void release() {
        if (statePointer != 0) {
            destroyNative(statePointer);
            statePointer = 0;
            Log.i(TAG, "RNNoise resources released.");
        }
    }

    private native long createNative();
    private native void destroyNative(long statePtr);
    private native float processFrameNative(long statePtr, short[] inFrame, short[] outFrame);
}
