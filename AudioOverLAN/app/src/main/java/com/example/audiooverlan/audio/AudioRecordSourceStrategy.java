package com.example.audiooverlan.audio;

import android.media.AudioFormat;
import android.media.AudioRecord;
import android.util.Log;

public class AudioRecordSourceStrategy implements AudioSourceStrategy {
    private static final String TAG = "AudioRecordSource";
    private AudioRecord audioRecord;
    private final AudioConfig config;
    private final int micSource;
    private final int frameSizeSamples;

    private final android.content.Context context;

    public AudioRecordSourceStrategy(android.content.Context context, AudioConfig config, int micSource, int frameSizeSamples) {
        this.context = context;
        this.config = config;
        this.micSource = micSource;
        this.frameSizeSamples = frameSizeSamples;
    }

    @Override
    public boolean start() {
        int channelConfig = config.channels == 1 ? AudioFormat.CHANNEL_IN_MONO : AudioFormat.CHANNEL_IN_STEREO;
        int minBufferSize = AudioRecord.getMinBufferSize(config.sampleRate, channelConfig, config.audioFormat);
        audioRecord = new AudioRecord(micSource, config.sampleRate, channelConfig, config.audioFormat, 
                Math.max(minBufferSize * 2, frameSizeSamples * 2 * 4));

        if (audioRecord.getState() != AudioRecord.STATE_INITIALIZED) {
            Log.e(TAG, "AudioRecord initialization failed!");
            audioRecord = null;
            return false;
        } else {
            if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.M && config.deviceId != 0) {
                android.media.AudioManager am = (android.media.AudioManager) context.getSystemService(android.content.Context.AUDIO_SERVICE);
                android.media.AudioDeviceInfo[] devices = am.getDevices(android.media.AudioManager.GET_DEVICES_INPUTS);
                for (android.media.AudioDeviceInfo d : devices) {
                    if (d.getId() == config.deviceId) {
                        audioRecord.setPreferredDevice(d);
                        Log.i(TAG, "Set preferred device: " + d.getProductName());
                        break;
                    }
                }
            }
            audioRecord.startRecording();
            Log.i(TAG, "AudioRecord started with source: " + micSource);
            return true;
        }
    }

    @Override
    public boolean isStarted() {
        return audioRecord != null && audioRecord.getRecordingState() == AudioRecord.RECORDSTATE_RECORDING;
    }

    public AudioRecord getAudioRecord() {
        return audioRecord;
    }

    @Override
    public int read(short[] buf, int offset, int len) {
        if (audioRecord != null) {
            return audioRecord.read(buf, offset, len);
        }
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
    }
}
