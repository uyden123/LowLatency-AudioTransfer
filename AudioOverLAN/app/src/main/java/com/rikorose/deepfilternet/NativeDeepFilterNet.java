package com.rikorose.deepfilternet;

import android.content.Context;
import android.util.Log;

import com.example.audiooverlan.R;

import java.io.InputStream;
import java.nio.ByteBuffer;

/**
 * Java wrapper for the native DeepFilterNet library.
 * This class follows the package name com.rikorose.deepfilternet to match the pre-compiled libdf.so.
 */
public class NativeDeepFilterNet {
    private static final String TAG = "DeepFilterNet";
    private long nativePointer = 0;
    private long frameLength = -1;

    static {
        try {
            System.loadLibrary("df");
            Log.i(TAG, "Native library 'df' loaded successfully.");
        } catch (UnsatisfiedLinkError e) {
            Log.e(TAG, "Failed to load native library 'df': " + e.getMessage());
        }
    }

    public NativeDeepFilterNet(Context context) throws Exception {
        this(context, 50.0f);
    }

    public NativeDeepFilterNet(Context context, float attenuationLimit) throws Exception {
        byte[] modelBytes = loadModelFromRaw(context);
        if (modelBytes == null) {
            throw new Exception("Failed to load model from raw resources");
        }

        nativePointer = newNative(modelBytes, attenuationLimit);
        if (nativePointer == 0) {
            throw new Exception("Failed to initialize native DeepFilterNet state");
        }

        frameLength = getFrameLengthNative(nativePointer);
        Log.i(TAG, "DeepFilterNet initialized. Frame length: " + frameLength);
    }

    private byte[] loadModelFromRaw(Context context) {
        try (InputStream is = context.getResources().openRawResource(R.raw.deep_filter_mobile_model)) {
            byte[] buffer = new byte[is.available()];
            int read = is.read(buffer);
            if (read != buffer.length) {
                // Secondary fallback if available() is not accurate
                java.io.ByteArrayOutputStream byteStream = new java.io.ByteArrayOutputStream();
                byteStream.write(buffer, 0, read);
                byte[] temp = new byte[4096];
                int n;
                while ((n = is.read(temp)) != -1) {
                    byteStream.write(temp, 0, n);
                }
                return byteStream.toByteArray();
            }
            return buffer;
        } catch (Exception e) {
            Log.e(TAG, "Error loading model from raw: " + e.getMessage());
            return null;
        }
    }

    public long getFrameLength() {
        return frameLength;
    }

    public boolean setAttenuationLimit(float thresholdDb) {
        if (nativePointer == 0) return false;
        return setAttenLimNative(nativePointer, thresholdDb);
    }

    public boolean setPostFilterBeta(float beta) {
        if (nativePointer == 0) return false;
        return setPostFilterBetaNative(nativePointer, beta);
    }

    public float processFrame(ByteBuffer inputFrame) {
        if (nativePointer == 0) return -1f;
        if (!inputFrame.isDirect()) {
            Log.e(TAG, "processFrame requires a DIRECT ByteBuffer");
            return -1f;
        }
        return processFrameNative(nativePointer, inputFrame);
    }

    public synchronized void release() {
        if (nativePointer != 0) {
            freeNative(nativePointer);
            nativePointer = 0;
            Log.i(TAG, "DeepFilterNet resources released.");
        }
    }

    // Native methods
    private native long newNative(byte[] modelBytes, float attenuationLimit);
    private native long getFrameLengthNative(long statePtr);
    private native boolean setAttenLimNative(long statePtr, float limDb);
    private native boolean setPostFilterBetaNative(long statePtr, float beta);
    private native float processFrameNative(long statePtr, ByteBuffer inputFrame);
    private native void freeNative(long statePtr);
}
