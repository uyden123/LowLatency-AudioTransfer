package com.example.audiooverlan.services;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.util.Log;

import com.example.audiooverlan.audio.PlayerAudioPipeline;

/**
 * Listens for SCREEN_ON broadcasts and restarts the audio engine ONLY if needed.
 * This fixes Xiaomi (MIUI) aggressive battery optimization that kills/suspends
 * audio streams when the screen is off and no sound is playing.
 */
public class ScreenWakeReceiver extends BroadcastReceiver {
    private static final String TAG = "ScreenWakeReceiver";

    private PlayerAudioPipeline pipeline;

    public void setPipeline(PlayerAudioPipeline pipeline) {
        this.pipeline = pipeline;
    }

    @Override
    public void onReceive(Context context, Intent intent) {
        if (Intent.ACTION_SCREEN_ON.equals(intent.getAction())) {
            Log.d(TAG, "Screen ON detected, checking audio health...");
            checkAndRecoverAudio();
        }
    }

    private void checkAndRecoverAudio() {
        if (pipeline == null) {
            Log.w(TAG, "Pipeline is null, nothing to recover");
            return;
        }

        // Only restart if the audio output is actually dead
        if (pipeline.isAudioOutputAlive()) {
            Log.d(TAG, "Audio output is alive, no recovery needed");
            return;
        }

        Log.w(TAG, "Audio output is DEAD, recovering...");

        // Un-filter jitter buffer so packets flow through
        if (pipeline.getJitterBuffer() != null) {
            pipeline.getJitterBuffer().setFilterPackets(false);
        }

        // Restart the audio output engine (AAudio/AudioTrack)
        pipeline.restartAudioEngine();

        // Restore volume
        pipeline.setVolume(1.0f);

        Log.i(TAG, "Audio engine recovered after screen wake");
    }
}
