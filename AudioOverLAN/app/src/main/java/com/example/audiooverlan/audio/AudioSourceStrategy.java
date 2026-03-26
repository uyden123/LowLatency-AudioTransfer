package com.example.audiooverlan.audio;

public interface AudioSourceStrategy {
    /**
     * Reads audio data into the provided buffer.
     * @param buf The buffer to read into.
     * @param offset The offset to start reading at.
     * @param len The number of elements to read.
     * @return The number of elements read, or a negative error code if read fails.
     */
    int read(short[] buf, int offset, int len);

    /**
     * Starts the audio source.
     * @return true if started successfully.
     */
    boolean start();

    /**
     * Stops and releases the audio source.
     */
    void stop();

    /**
     * @return true if the source is currently started.
     */
    boolean isStarted();
}
