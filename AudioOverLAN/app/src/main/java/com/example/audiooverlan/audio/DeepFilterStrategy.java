package com.example.audiooverlan.audio;

import com.rikorose.deepfilternet.NativeDeepFilterNet;
import android.content.Context;
import android.util.Log;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;

public class DeepFilterStrategy implements NoiseSuppressionStrategy {
    private static final String TAG = "DeepFilterStrategy";
    private NativeDeepFilterNet deepFilterNet;
    private ByteBuffer dfBuffer;
    private java.nio.ShortBuffer dfShortView;
    private short[] dfOverflow;
    private final short[] dfCombinedWorkspace = new short[4096];
    private int dfOverflowCount = 0;

    public DeepFilterStrategy(Context context, float attenuationLevel) {
        try {
            deepFilterNet = new NativeDeepFilterNet(context, attenuationLevel);
            int frameSizeBytes = (int) deepFilterNet.getFrameLength();
            int frameSizeSamples = frameSizeBytes / 2;
            dfBuffer = ByteBuffer.allocateDirect(frameSizeBytes).order(ByteOrder.LITTLE_ENDIAN);
            dfShortView = dfBuffer.asShortBuffer();
            dfOverflow = new short[frameSizeSamples];
            deepFilterNet.setAttenuationLimit(attenuationLevel);
            Log.i(TAG, "DeepFilterNet initialized (Frame=" + frameSizeSamples + 
                    ", MaxAtten=" + attenuationLevel + "dB)");
        } catch (Exception e) {
            Log.e(TAG, "Failed to init DeepFilterNet", e);
            deepFilterNet = null;
        }
    }

    @Override
    public void updateLevel(float attenuationLevel) {
        if (deepFilterNet != null) {
            deepFilterNet.setAttenuationLimit(attenuationLevel);
        }
    }

    @Override
    public void process(short[] pcmFrame) {
        if (deepFilterNet == null) return;
        
        int dfFrameSize = (int) deepFilterNet.getFrameLength() / 2;
        int totalAvailable = dfOverflowCount + pcmFrame.length;

        // Check buffer boundary
        if (totalAvailable > dfCombinedWorkspace.length) {
            Log.e(TAG, "DeepFilter workspace overflow! Resetting state.");
            dfOverflowCount = 0;
            return;
        }

        // Copy overflow + new frame into workspace
        if (dfOverflowCount > 0) {
            System.arraycopy(dfOverflow, 0, dfCombinedWorkspace, 0, dfOverflowCount);
        }
        System.arraycopy(pcmFrame, 0, dfCombinedWorkspace, dfOverflowCount, pcmFrame.length);

        int processedPos = 0;
        int outPos = 0;

        // Process chunk by chunk using DeepFilterNet frame size
        while ((totalAvailable - processedPos) >= dfFrameSize) {
            dfShortView.clear();
            dfShortView.put(dfCombinedWorkspace, processedPos, dfFrameSize);
            
            deepFilterNet.processFrame(dfBuffer);
            
            dfShortView.position(0);
            dfShortView.get(pcmFrame, outPos, dfFrameSize);
            
            processedPos += dfFrameSize;
            outPos += dfFrameSize;
        }

        // Store remainder in overflow
        dfOverflowCount = totalAvailable - processedPos;
        if (dfOverflowCount > 0) {
            System.arraycopy(dfCombinedWorkspace, processedPos, dfOverflow, 0, dfOverflowCount);
        }

        // Zero out the rest of the output buffer (if output is smaller than input)
        // This shouldn't happen, but just in case
        for (int i = outPos; i < pcmFrame.length; i++) {
            pcmFrame[i] = 0;
        }
    }

    @Override
    public void release() {
        if (deepFilterNet != null) {
            deepFilterNet.release();
            deepFilterNet = null;
        }
    }
}
