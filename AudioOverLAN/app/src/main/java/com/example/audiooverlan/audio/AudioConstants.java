package com.example.audiooverlan.audio;

public class AudioConstants {
    // --- Audio Format ---
    public static final int SAMPLE_RATE = 48000;
    public static final int CHANNELS = 2;
    
    // --- Opus Configuration ---
    public static final float OPUS_FRAME_SIZE_MS = 10f;
    
    // --- Jitter Buffer Configuration ---
    public static final int MIN_TARGET_PACKETS = 1; 
    public static final int MAX_TARGET_PACKETS = 6; 
    public static final int MAX_BUFFER_PACKETS = 150; 
    
    // --- Calculated Values ---
    public static final int FRAME_SIZE_SAMPLES = (int) (SAMPLE_RATE * OPUS_FRAME_SIZE_MS / 1000.0f);
    public static final int FRAME_SIZE_SAMPLES_STEREO = FRAME_SIZE_SAMPLES * CHANNELS;
}
