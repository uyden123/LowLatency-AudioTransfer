package com.example.audiooverlan.services;

import android.content.Context;
import android.media.AudioManager;
import android.util.Log;

import com.example.audiooverlan.audio.PlayerAudioPipeline;

public class AudioFocusHandler {
    private static final String TAG = "AudioFocusHandler";

    private final AudioManager audioManager;
    private final AudioManager.OnAudioFocusChangeListener focusChangeListener;
    private PlayerAudioPipeline pipeline;
    private boolean isExclusiveMode = false;

    public AudioFocusHandler(Context context) {
        this.audioManager = (AudioManager) context.getSystemService(Context.AUDIO_SERVICE);
        this.focusChangeListener = this::onAudioFocusChange;
    }

    public void setPipeline(PlayerAudioPipeline pipeline) {
        this.pipeline = pipeline;
    }

    public void setExclusiveMode(boolean exclusiveMode) {
        this.isExclusiveMode = exclusiveMode;
        if (exclusiveMode) {
            // Polite Mode: We want to play, but we'll pause if others start
            if (audioManager != null) {
                audioManager.requestAudioFocus(focusChangeListener,
                        AudioManager.STREAM_MUSIC,
                        AudioManager.AUDIOFOCUS_GAIN);
                Log.i(TAG, "Polite mode: Requested Audio Focus GAIN");
            }
        } else {
            // Coexist: Don't strictly care about focus, or duck others
            if (audioManager != null) {
                audioManager.abandonAudioFocus(focusChangeListener);
                Log.i(TAG, "Coexist mode: Abandoned Audio Focus");
            }
        }
    }

    private void onAudioFocusChange(int focusChange) {
        Log.d(TAG, "Audio Focus change: " + focusChange);
        if (!isExclusiveMode) return; // Only react if in Polite mode

        switch (focusChange) {
            case AudioManager.AUDIOFOCUS_LOSS:
            case AudioManager.AUDIOFOCUS_LOSS_TRANSIENT:
                if (pipeline != null) {
                    Log.i(TAG, "Focus lost, pausing audio stream (Native mute)");
                    pipeline.setVolume(0.0f); // Mute for pause effect
                }
                break;
            case AudioManager.AUDIOFOCUS_GAIN:
                if (pipeline != null) {
                    Log.i(TAG, "Focus gained, resuming audio stream");
                    pipeline.setVolume(1.0f);
                }
                break;
            case AudioManager.AUDIOFOCUS_LOSS_TRANSIENT_CAN_DUCK:
                if (pipeline != null) {
                    pipeline.setVolume(0.2f); // Duck
                }
                break;
        }
    }

    public void abandonFocus() {
        if (audioManager != null) {
            audioManager.abandonAudioFocus(focusChangeListener);
        }
    }
}
