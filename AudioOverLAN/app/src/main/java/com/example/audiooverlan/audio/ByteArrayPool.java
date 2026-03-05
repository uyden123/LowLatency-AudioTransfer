package com.example.audiooverlan.audio;

import java.util.concurrent.BlockingQueue;
import java.util.concurrent.LinkedBlockingQueue;

public class ByteArrayPool {
    private final BlockingQueue<byte[]> pool;
    private final int bufferSize;

    public ByteArrayPool(int poolSize, int bufferSize) {
        this.pool = new LinkedBlockingQueue<>(poolSize);
        this.bufferSize = bufferSize;
        for (int i = 0; i < poolSize; i++) {
            pool.offer(new byte[bufferSize]);
        }
    }

    public byte[] acquire() {
        byte[] buf = pool.poll();
        return (buf != null) ? buf : new byte[bufferSize];
    }

    public void release(byte[] buf) {
        if (buf != null && buf.length == bufferSize) {
            pool.offer(buf);
        }
    }
}
