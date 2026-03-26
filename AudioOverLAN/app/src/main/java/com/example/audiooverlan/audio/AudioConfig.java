package com.example.audiooverlan.audio;

import android.media.AudioFormat;

public class AudioConfig {
    public final int sampleRate;
    public final int channels;
    public final int audioFormat;
    public final boolean exclusiveMode;
    public final boolean useAAudio;
    public final int deviceId;
    public final JitterBuffer.BufferMode bufferMode;
    public final int minBufferMs;
    public final int maxBufferMs;

    private AudioConfig(Builder builder) {
        this.sampleRate = builder.sampleRate;
        this.channels = builder.channels;
        this.audioFormat = builder.audioFormat;
        this.exclusiveMode = builder.exclusiveMode;
        this.useAAudio = builder.useAAudio;
        this.deviceId = builder.deviceId;
        this.bufferMode = builder.bufferMode;
        this.minBufferMs = builder.minBufferMs;
        this.maxBufferMs = builder.maxBufferMs;
    }

    public static class Builder {
        private int sampleRate = AudioConstants.SAMPLE_RATE;
        private int channels = AudioConstants.CHANNELS;
        private int audioFormat = AudioFormat.ENCODING_PCM_16BIT;
        private boolean exclusiveMode = false;
        private boolean useAAudio = false;
        private int deviceId = 0; // 0 = automatic
        private JitterBuffer.BufferMode bufferMode = JitterBuffer.BufferMode.MEDIUM;
        private int minBufferMs = 30;
        private int maxBufferMs = 80;

        public Builder deviceId(int id) {
            this.deviceId = id;
            return this;
        }

        public Builder sampleRate(int sampleRate) {
            this.sampleRate = sampleRate;
            return this;
        }

        public Builder channels(int channels) {
            this.channels = channels;
            return this;
        }

        public Builder audioFormat(int audioFormat) {
            this.audioFormat = audioFormat;
            return this;
        }

        public Builder exclusiveMode(boolean exclusiveMode) {
            this.exclusiveMode = exclusiveMode;
            return this;
        }

        public Builder useAAudio(boolean useAAudio) {
            this.useAAudio = useAAudio;
            return this;
        }

        public Builder bufferConfig(JitterBuffer.BufferMode mode, int minMs, int maxMs) {
            this.bufferMode = mode;
            this.minBufferMs = minMs;
            this.maxBufferMs = maxMs;
            return this;
        }

        public AudioConfig build() {
            return new AudioConfig(this);
        }
    }
}
