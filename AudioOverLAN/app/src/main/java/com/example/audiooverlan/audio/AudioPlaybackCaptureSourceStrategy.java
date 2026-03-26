package com.example.audiooverlan.audio;

import android.content.Context;
import android.content.Intent;
import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioPlaybackCaptureConfiguration;
import android.media.AudioRecord;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.util.Log;

import androidx.annotation.RequiresApi;

import com.example.audiooverlan.utils.SettingsRepository;

import java.util.Set;

public class AudioPlaybackCaptureSourceStrategy implements AudioSourceStrategy {
    private static final String TAG = "AppCaptureSource";
    private AudioRecord audioRecord;
    private MediaProjection mediaProjection;
    private final Context context;
    private final AudioConfig config;
    private final Intent projectionData;

    public AudioPlaybackCaptureSourceStrategy(Context context, AudioConfig config, Intent projectionData) {
        this.context = context;
        this.config = config;
        this.projectionData = projectionData;
    }

    @Override
    public boolean start() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) return false;

        MediaProjectionManager mpm = (MediaProjectionManager) context.getSystemService(Context.MEDIA_PROJECTION_SERVICE);
        mediaProjection = mpm.getMediaProjection(android.app.Activity.RESULT_OK, projectionData);

        if (mediaProjection == null) {
            Log.e(TAG, "Failed to get MediaProjection");
            return false;
        }

        AudioPlaybackCaptureConfiguration.Builder builder = new AudioPlaybackCaptureConfiguration.Builder(mediaProjection);
        builder.addMatchingUsage(AudioAttributes.USAGE_MEDIA);
        builder.addMatchingUsage(AudioAttributes.USAGE_GAME);
        builder.addMatchingUsage(AudioAttributes.USAGE_UNKNOWN);

        // Optional: Filter by selected apps
        Set<String> selectedApps = SettingsRepository.getInstance(context).getSelectedApps();
        if (!selectedApps.isEmpty()) {
            // Note: AudioPlaybackCaptureConfiguration only allows adding UIDs or excluding.
            // Getting UIDs for package names is required here.
            // For now, we'll just capture everything allowed.
        }

        AudioPlaybackCaptureConfiguration captureConfig = builder.build();

        int channelConfig = config.channels == 1 ? AudioFormat.CHANNEL_IN_MONO : AudioFormat.CHANNEL_IN_STEREO;
        int minBufferSize = AudioRecord.getMinBufferSize(config.sampleRate, channelConfig, config.audioFormat);

        audioRecord = new AudioRecord.Builder()
                .setAudioFormat(new AudioFormat.Builder()
                        .setEncoding(config.audioFormat)
                        .setSampleRate(config.sampleRate)
                        .setChannelMask(channelConfig)
                        .build())
                .setAudioPlaybackCaptureConfig(captureConfig)
                .setBufferSizeInBytes(Math.max(minBufferSize * 2, config.sampleRate * 2 * 20 / 1000 * 4))
                .build();

        if (audioRecord.getState() != AudioRecord.STATE_INITIALIZED) {
            Log.e(TAG, "AudioRecord (Capture) initialization failed!");
            return false;
        }

        audioRecord.startRecording();
        Log.i(TAG, "AudioPlaybackCaptureRecord started");
        return true;
    }

    @Override
    public boolean isStarted() {
        return audioRecord != null && audioRecord.getRecordingState() == AudioRecord.RECORDSTATE_RECORDING;
    }

    @Override
    public int read(short[] buf, int offset, int len) {
        if (audioRecord != null) return audioRecord.read(buf, offset, len);
        return -1;
    }

    @Override
    public void stop() {
        if (audioRecord != null) {
            try {
                audioRecord.stop();
                audioRecord.release();
            } catch (Exception ignored) {}
            audioRecord = null;
        }
        if (mediaProjection != null) {
            mediaProjection.stop();
            mediaProjection = null;
        }
    }
}
