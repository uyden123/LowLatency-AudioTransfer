package com.example.audiooverlan.audio;

public class NoOpStrategy implements NoiseSuppressionStrategy {
    @Override
    public void process(short[] frame) {
        // Do nothing, pass through
    }

    @Override
    public void release() {
        // No resources to release
    }
}
