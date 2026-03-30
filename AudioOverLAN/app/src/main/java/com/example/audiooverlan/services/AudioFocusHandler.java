package com.example.audiooverlan.services;

import android.content.Context;
import android.media.AudioAttributes;
import android.media.AudioFocusRequest;
import android.media.AudioManager;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;

import com.example.audiooverlan.audio.PlayerAudioPipeline;

public class AudioFocusHandler {
    private static final String TAG = "AudioFocusHandler";

    private final AudioManager audioManager;
    private PlayerAudioPipeline pipeline;
    private boolean isExclusiveMode = false;
    private Object focusRequest; // AudioFocusRequest on API 26+, null below
    
    // Handler for "Persistent Loss (-1)" retry logic
    private final Handler retryHandler = new Handler(Looper.getMainLooper());
    private final Runnable retryRunnable = this::reRequestFocus;
    private final int RETRY_DELAY_MS = 1000; 
    private long lastActiveTime = 0;

    public AudioFocusHandler(Context context) {
        this.audioManager = (AudioManager) context.getSystemService(Context.AUDIO_SERVICE);
    }

    public void setPipeline(PlayerAudioPipeline pipeline) {
        this.pipeline = pipeline;
    }

    public void setExclusiveMode(boolean exclusiveMode) {
        this.isExclusiveMode = exclusiveMode;
        if (exclusiveMode) {
            reRequestFocus();
        } else {
            abandonFocus();
            retryHandler.removeCallbacks(retryRunnable);
            Log.i(TAG, "Coexist mode: Abandoned Audio Focus");
        }
    }

    private void reRequestFocus() {
        if (!isExclusiveMode || audioManager == null) return;

        // POLITE CHECK: Ensure other apps have been silent for at least 2 seconds.
        if (audioManager.isMusicActive()) {
            lastActiveTime = System.currentTimeMillis();
            Log.d(TAG, "Music is active elsewhere, staying polite and waiting...");
            retryHandler.removeCallbacks(retryRunnable);
            retryHandler.postDelayed(retryRunnable, RETRY_DELAY_MS); 
            return;
        }

        long silentDuration = System.currentTimeMillis() - lastActiveTime;
        if (silentDuration < 1000) {
            Log.d(TAG, "Silence detected, but waiting for 1s quiet period (" + silentDuration + "ms elapsed)...");
            retryHandler.removeCallbacks(retryRunnable);
            retryHandler.postDelayed(retryRunnable, 1000 - silentDuration + 10);
            return;
        }

        int result;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            AudioAttributes playbackAttributes = new AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_MEDIA)
                    .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                    .build();

            focusRequest = new AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN)
                    .setAudioAttributes(playbackAttributes)
                    .setAcceptsDelayedFocusGain(true) // Fast recovery supported on API 26+
                    .setOnAudioFocusChangeListener(this::onAudioFocusChange)
                    .build();

            result = audioManager.requestAudioFocus((AudioFocusRequest) focusRequest);
        } else {
            // Fallback for API 24-25
            result = audioManager.requestAudioFocus(this::onAudioFocusChange,
                    AudioManager.STREAM_MUSIC,
                    AudioManager.AUDIOFOCUS_GAIN);
        }

        if (result == AudioManager.AUDIOFOCUS_REQUEST_GRANTED) {
            Log.i(TAG, "Polite mode: Audio Focus GRANTED");
            onAudioFocusChange(AudioManager.AUDIOFOCUS_GAIN);
        } else if (result == AudioManager.AUDIOFOCUS_REQUEST_DELAYED) {
            Log.i(TAG, "Polite mode: Audio Focus DELAYED (queued by system)");
            // We are in queue, system will call onAudioFocusChange(GAIN) when ready.
        } else {
            Log.w(TAG, "Polite mode: Audio Focus FAILED, will retry...");
            // Manual retry if system rejected it entirely
            retryHandler.removeCallbacks(retryRunnable);
            retryHandler.postDelayed(retryRunnable, RETRY_DELAY_MS);
        }
    }

    private void onAudioFocusChange(int focusChange) {
        Log.d(TAG, "Audio Focus change: " + focusChange);
        if (!isExclusiveMode) return;

        switch (focusChange) {
            case AudioManager.AUDIOFOCUS_LOSS:
                Log.i(TAG, "Focus lost PERMANENTLY (-1), yielding politely...");
                muteAndFilterAndStop();
                // If we got PINK-SLIPPED (-1), we MUST re-request but only when it's quiet
                retryHandler.removeCallbacks(retryRunnable);
                retryHandler.postDelayed(retryRunnable, RETRY_DELAY_MS); 
                break;
            case AudioManager.AUDIOFOCUS_LOSS_TRANSIENT:
                Log.i(TAG, "Focus lost TRANSIENTLY (-2), waiting for system...");
                muteAndFilter();
                retryHandler.removeCallbacks(retryRunnable);
                break;
            case AudioManager.AUDIOFOCUS_GAIN:
                Log.i(TAG, "Focus GAINED (1), resuming audio stream");
                retryHandler.removeCallbacks(retryRunnable);
                if (pipeline != null) {
                    if (pipeline.getJitterBuffer() != null) {
                        pipeline.getJitterBuffer().setFilterPackets(false);
                    }
                    pipeline.restartAudioEngine(); // Restore output device
                    pipeline.setVolume(1.0f);
                }
                break;
            case AudioManager.AUDIOFOCUS_LOSS_TRANSIENT_CAN_DUCK:
                Log.i(TAG, "Focus lost (CAN_DUCK), reducing volume to 20%");
                if (pipeline != null) {
                    pipeline.setVolume(0.2f);
                }
                break;
        }
    }

    private void muteAndFilter() {
        if (pipeline != null) {
            pipeline.setVolume(0.0f);
            if (pipeline.getJitterBuffer() != null) {
                pipeline.getJitterBuffer().setFilterPackets(true);
            }
        }
    }

    private void muteAndFilterAndStop() {
        muteAndFilter();
        if (pipeline != null) {
            // ASYNC STOP: Don't block the focus callback thread (usually Main Thread).
            // This allows the system to grant focus to the next app faster.
            new Thread(() -> {
                Log.d(TAG, "Stopping audio output in background...");
                pipeline.stopAudioOutput(); 
            }).start();
        }
    }

    public void abandonFocus() {
        if (audioManager != null) {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && focusRequest != null) {
                audioManager.abandonAudioFocusRequest((AudioFocusRequest) focusRequest);
            } else {
                audioManager.abandonAudioFocus(this::onAudioFocusChange);
            }
        }
        retryHandler.removeCallbacks(retryRunnable);
    }
}
