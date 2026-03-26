package com.example.audiooverlan.services;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.media.AudioDeviceCallback;
import android.media.AudioDeviceInfo;
import android.media.AudioManager;
import android.os.Build;
import android.util.Log;

import com.example.audiooverlan.audio.PlayerAudioPipeline;

public class AudioDeviceMonitor {
    private static final String TAG = "AudioDeviceMonitor";

    private final Context context;
    private final AudioManager audioManager;
    private PlayerAudioPipeline pipeline;
    private AudioDeviceCallback audioDeviceCallback;

    private final BroadcastReceiver scoReceiver = new BroadcastReceiver() {
        @Override
        public void onReceive(Context context, Intent intent) {
            int state = intent.getIntExtra(AudioManager.EXTRA_SCO_AUDIO_STATE, -1);
            Log.d(TAG, "Player: Bluetooth SCO state changed: " + state);
            if (state == AudioManager.SCO_AUDIO_STATE_CONNECTED || state == AudioManager.SCO_AUDIO_STATE_DISCONNECTED) {
                if (pipeline != null) {
                    boolean isSco = (state == AudioManager.SCO_AUDIO_STATE_CONNECTED);
                    Log.i(TAG, "Restarting player engine due to SCO state change (isSco=" + isSco + ")...");
                    pipeline.restartAudioEngine(isSco);
                }
            }
        }
    };

    public AudioDeviceMonitor(Context context) {
        this.context = context;
        this.audioManager = (AudioManager) context.getSystemService(Context.AUDIO_SERVICE);
    }

    public void setPipeline(PlayerAudioPipeline pipeline) {
        this.pipeline = pipeline;
    }

    public void register() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M && audioManager != null) {
            audioDeviceCallback = new AudioDeviceCallback() {
                @Override
                public void onAudioDevicesAdded(AudioDeviceInfo[] addedDevices) {
                    handleDeviceChange();
                }

                @Override
                public void onAudioDevicesRemoved(AudioDeviceInfo[] removedDevices) {
                    handleDeviceChange();
                }

                private void handleDeviceChange() {
                    if (pipeline != null) {
                        Log.i(TAG, "Audio device change detected, restarting engine...");
                        pipeline.restartAudioEngine();
                    }
                }
            };
            audioManager.registerAudioDeviceCallback(audioDeviceCallback, null);
        }

        context.registerReceiver(scoReceiver, new IntentFilter(AudioManager.ACTION_SCO_AUDIO_STATE_UPDATED));
    }

    public void unregister() {
        try {
            context.unregisterReceiver(scoReceiver);
        } catch (Exception ignored) {}

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M && audioDeviceCallback != null && audioManager != null) {
            audioManager.unregisterAudioDeviceCallback(audioDeviceCallback);
        }
    }
}
