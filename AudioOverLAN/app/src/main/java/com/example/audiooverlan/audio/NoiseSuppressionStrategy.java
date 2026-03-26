package com.example.audiooverlan.audio;

public interface NoiseSuppressionStrategy {
    /**
     * Processes a frame of audio data for noise suppression.
     * @param frame The audio frame to process, modified in-place.
     */
    void process(short[] frame);

    /**
     * Updates the suppression strength/level if supported by the strategy.
     * @param level The new level (e.g. dB of attenuation).
     */
    default void updateLevel(float level) {}

    /**
     * Releases any resources associated with the noise suppression strategy.
     */
    void release();
}
