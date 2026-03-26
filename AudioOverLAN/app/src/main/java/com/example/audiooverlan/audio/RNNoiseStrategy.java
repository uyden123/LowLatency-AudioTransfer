package com.example.audiooverlan.audio;

import com.example.audiooverlan.audio.NativeRNNoise;
import android.util.Log;

public class RNNoiseStrategy implements NoiseSuppressionStrategy {
    private static final String TAG = "RNNoiseStrategy";
    private NativeRNNoise rnnoise;
    private final short[] rnnWorkspace = new short[480];

    public RNNoiseStrategy() {
        try {
            rnnoise = new NativeRNNoise();
            Log.i(TAG, "RNNoise initialized successfully.");
        } catch (Exception e) {
            Log.e(TAG, "CRITICAL: Failed to init RNNoise: " + e.getMessage());
            rnnoise = null;
        }
    }

    @Override
    public void process(short[] pcmFrame) {
        if (rnnoise == null) return;
        
        int processedPos = 0;
        int remaining = pcmFrame.length;

        // Process in RNNoise fixed frame chunks (480 samples)
        while (remaining >= 480) {
            System.arraycopy(pcmFrame, processedPos, rnnWorkspace, 0, 480);
            rnnoise.processFrame(rnnWorkspace, rnnWorkspace);
            System.arraycopy(rnnWorkspace, 0, pcmFrame, processedPos, 480);
            processedPos += 480;
            remaining -= 480;
        }

        // Output remaining directly without processing
        if (remaining > 0) {
            Log.w(TAG, "RNNoise frame mismatch, unprocessed samples at end: " + remaining);
        }
    }

    @Override
    public void release() {
        if (rnnoise != null) {
            rnnoise.release();
            rnnoise = null;
        }
    }
}
