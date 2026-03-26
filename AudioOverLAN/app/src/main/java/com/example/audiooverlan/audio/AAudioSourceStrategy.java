package com.example.audiooverlan.audio;

import android.util.Log;

public class AAudioSourceStrategy implements AudioSourceStrategy {
    private static final String TAG = "AAudioSource";
    private AAudioRecorder aaudioRecorder;
    private boolean isStarted = false;
    private final AudioConfig config;

    public AAudioSourceStrategy(AudioConfig config) {
        this.config = config;
    }

    @Override
    public boolean start() {
        if (AAudioRecorder.isAvailable()) {
            aaudioRecorder = new AAudioRecorder();
            if (aaudioRecorder.isAAudioSupported()) {
                isStarted = aaudioRecorder.start(config.sampleRate, config.channels, config.exclusiveMode, config.deviceId);
                if (isStarted) {
                    Log.i(TAG, "AAudio capture started: " + aaudioRecorder.getStreamInfo());
                } else {
                    Log.w(TAG, "AAudio failed to start.");
                }
            } else {
                Log.w(TAG, "AAudio not supported by device/OS.");
            }
        }
        return isStarted;
    }

    @Override
    public boolean isStarted() {
        return isStarted;
    }

    @Override
    public int read(short[] buf, int offset, int len) {
        if (isStarted && aaudioRecorder != null) {
            return aaudioRecorder.read(buf, offset, len);
        }
        return -1;
    }

    @Override
    public void stop() {
        if (aaudioRecorder != null) {
            try {
                aaudioRecorder.stop();
            } catch (Exception ignored) {}
            aaudioRecorder = null;
            isStarted = false;
        }
    }
}
