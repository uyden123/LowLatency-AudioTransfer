package com.example.audiooverlan.audio;

import android.util.Log;

public class AudioCapturePipeline {
    private static final String TAG = "CapturePipeline";

    private final int sampleRate;
    private final int channels;
    private final int frameSize;
    
    private AudioSourceStrategy audioSource;
    private NoiseSuppressionStrategy noiseSuppression;
    private final OpusCodec opusCodec;
    
    private Thread capturingThread;
    private volatile boolean isRunning = false;
    private float volumeGain = 1.0f;
    
    private final CaptureListener listener;

    public interface CaptureListener {
        void onFrameCaptured(byte[] opusData, int length);
        void onError(String message);
    }

    public AudioCapturePipeline(AudioConfig config, CaptureListener listener) {
        this.sampleRate = config.sampleRate;
        this.channels = config.channels;
        this.frameSize = sampleRate * 20 / 1000; // 20ms
        this.listener = listener;

        this.opusCodec = new OpusCodec(sampleRate, channels, 20, 64000, 10, true, false);
    }

    public void setStrategies(AudioSourceStrategy source, NoiseSuppressionStrategy ns) {
        this.audioSource = source;
        this.noiseSuppression = ns;
    }

    public void setVolumeGain(float gain) {
        this.volumeGain = gain;
    }

    public void updateNoiseSuppressionLevel(float level) {
        if (noiseSuppression != null) {
            noiseSuppression.updateLevel(level);
        }
    }

    public synchronized void start() {
        if (isRunning) return;
        isRunning = true;

        if (!opusCodec.initEncoder()) {
            if (listener != null) listener.onError("Failed to init Opus encoder");
            return;
        }

        capturingThread = new Thread(this::captureLoop, "CaptureThread");
        capturingThread.setPriority(Thread.MAX_PRIORITY);
        capturingThread.start();
    }

    private void captureLoop() {
        android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_AUDIO);

        short[] pcmFrame = new short[frameSize * channels];

        try {
            while (isRunning) {
                // Read frame
                int totalRead = 0;
                while (totalRead < pcmFrame.length && isRunning) {
                    int read = audioSource != null ? audioSource.read(pcmFrame, totalRead, pcmFrame.length - totalRead) : -1;
                    if (read > 0) totalRead += read;
                    else if (read < 0) {
                        isRunning = false; // Error - stop pipeline
                        break;
                    } else {
                        Thread.sleep(1); // Better yield when empty
                    }
                }
                if (!isRunning) break;

                if (totalRead < pcmFrame.length) continue;

                // 1. Noise Suppression
                if (noiseSuppression != null) {
                    noiseSuppression.process(pcmFrame);
                }

                // 2. Volume Gain
                if (Math.abs(volumeGain - 1.0f) > 0.01f) {
                    for (int i = 0; i < pcmFrame.length; i++) {
                        int val = (int) (pcmFrame[i] * volumeGain);
                        pcmFrame[i] = (short) Math.max(-32768, Math.min(32767, val));
                    }
                }

                // 3. Encode
                int encoded = opusCodec.encodeToInternalBuffer(pcmFrame, 0, frameSize);
                if (encoded > 0 && listener != null) {
                    listener.onFrameCaptured(opusCodec.getEncodeBuffer(), encoded);
                }
            }
        } catch (InterruptedException e) {
            // Normal shutdown
            Log.i(TAG, "Capture loop interrupted (shutting down)");
        } catch (Exception e) {
            Log.e(TAG, "Capture loop error", e);
            if (isRunning && listener != null) listener.onError(e.getMessage());
        }
    }

    public boolean isStarted() {
        return isRunning;
    }

    public void stop() {
        isRunning = false;
        if (capturingThread != null) {
            capturingThread.interrupt();
            capturingThread = null;
        }
        if (audioSource != null) {
            audioSource.stop();
        }
        if (noiseSuppression != null) {
            noiseSuppression.release();
        }
        if (opusCodec != null) {
            opusCodec.release();
        }
    }
}
