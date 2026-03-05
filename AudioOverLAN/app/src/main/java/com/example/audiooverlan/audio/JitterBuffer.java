package com.example.audiooverlan.audio;

import android.util.Log;
import java.util.PriorityQueue;
import java.util.Stack;

/**
 * Simple sequence-number based jitter buffer.
 * Buffers a fixed amount of audio before starting playback.
 * Optimized with Object Pooling to eliminate GC pressure.
 */
public class JitterBuffer {
    private static final String TAG = "JitterBuffer";

    // Configuration
    private static final int PACKET_DURATION_MS = 20;
    private static final int MIN_TARGET_PACKETS = 2; // 40ms minimum
    private static final int MAX_TARGET_PACKETS = 15; // 300ms maximum
    private static final int MAX_BUFFER_PACKETS = 150;

    // Buffer state
    private final PriorityQueue<AudioPacket> queue;
    private final Stack<AudioPacket> pool = new Stack<>();
    private int nextSequence = -1;
    private boolean isBuffering = true;
    
    // Stats counters
    private long totalReceived = 0;
    private long lostPackets = 0;
    private long latePackets = 0;
    private long lastReportedPackets = 0;
    private long lastReportTime = 0;
    private double currentBitrateKbps = 0;

    // Sliding window for Loss Rate (PLC)
    private static final int WINDOW_SIZE_SEC = 10;
    private final long[] lostPerSec = new long[WINDOW_SIZE_SEC];
    private final long[] receivedPerSec = new long[WINDOW_SIZE_SEC];
    private int windowIdx = 0;
    private long lastWindowUpdate = 0;
    private double currentLossRate = 0; // % over the window
    
    // Adaptive target
    private boolean isAdaptive = true;
    private int fixedTargetPackets = 4; // Default 80ms
    private int targetPackets = 4; // Final current target (adaptive or fixed)

    public static class AudioPacket {
        public int sequence;
        public long timestamp;
        public byte[] data;
        public int length;
        public boolean isPLC;
        public long wallClock;

        public AudioPacket() {
            this.data = new byte[2048]; // Max Opus packet size
        }

        public void set(int sequence, long timestamp, long wallClock, byte[] data, int length, boolean isPLC) {
            this.sequence = sequence;
            this.timestamp = timestamp;
            this.wallClock = wallClock;
            if (this.data.length < length) this.data = new byte[length];
            if (data != null) {
                System.arraycopy(data, 0, this.data, 0, length);
            }
            this.length = length;
            this.isPLC = isPLC;
        }
    }

    public static class Statistics {
        public int bufferLevel;
        public int delayMs;
        public long minDelay;
        public int targetPackets;
        public double jitterDev;
        public long totalReceived;
        public long lostPackets;
        public long latePackets;
        public double bitrateKbps;
        public long lastTransitDelay; // New: Network transit delay of last packet
        public double lossRate; // New: % loss in recent window

        @Override
        public String toString() {
            return String.format("Buffer: %d/%d pkts Drift: %d Jitter: %.1fms Loss: %d (%.1f%%) Late: %d Bitrate: %.1f kbps Net: %dms", 
                                 bufferLevel, targetPackets, minDelay, jitterDev, lostPackets, lossRate, latePackets, bitrateKbps, lastTransitDelay);
        }
    }

    // MinDelay Clock Drift Monitor
    private long[] delayHistory = new long[200]; // 4 seconds window at 20ms/packet
    private int delayIndex = 0;
    private long currentMinDelay = 0;
    private double currentJitterSD = 0;
    private long lastMeasuredTransitDelay = 0; // Relative to minDelay

    public JitterBuffer() {
        this.queue = new PriorityQueue<>((p1, p2) -> {
            int diff = (p1.sequence - p2.sequence + 65536) % 65536;
            if (diff > 32768) return -1; // Wraparound
            return 1;
        });

        // Pre-fill pool to avoid allocation during initial bursts
        for (int i = 0; i < MAX_BUFFER_PACKETS; i++) {
            pool.push(new AudioPacket());
        }
    }

    private AudioPacket acquirePacket() {
        if (!pool.isEmpty()) return pool.pop();
        return new AudioPacket();
    }

    public synchronized void recyclePacket(AudioPacket packet) {
        if (packet != null && pool.size() < MAX_BUFFER_PACKETS) {
            pool.push(packet);
        }
    }

    public synchronized void add(int sequence, long timestamp, long wallClock, byte[] data, int length) {
        // Compute transit delay for Clock Drift Monitor
        long localArrival = System.currentTimeMillis();
        long delay = localArrival - wallClock;
        
        delayHistory[delayIndex] = delay;
        delayIndex = (delayIndex + 1) % delayHistory.length;
        
        long m = Long.MAX_VALUE;
        long sum = 0;
        int count = 0;
        for (long d : delayHistory) {
            if (d != 0) {
                if (d < m) m = d;
                sum += d;
                count++;
            }
        }
        if (m != Long.MAX_VALUE) currentMinDelay = m;
        
        // Calculate "Actual" transit time relative to the best case seen so far
        // We assume the best case (min delay) corresponds to roughly 5ms of real network time.
        // This compensates for clock drift/skew.
        lastMeasuredTransitDelay = (delay - currentMinDelay) + 5; 
        if (lastMeasuredTransitDelay < 0) lastMeasuredTransitDelay = 0;

        // Standard Deviation calculation for Adaptive Jitter Buffer
        if (count > 10) {
            double mean = (double) sum / count;
            double varianceSum = 0;
            for (long d : delayHistory) {
                if (d != 0) {
                    varianceSum += (d - mean) * (d - mean);
                }
            }
            currentJitterSD = Math.sqrt(varianceSum / count);
            
            // Base buffer 20ms + 3 Standard Deviations (99.7% coverage)
            if (isAdaptive) {
                int targetMs = 20 + (int) (currentJitterSD * 3);
                int packets = targetMs / PACKET_DURATION_MS;
                targetPackets = Math.max(MIN_TARGET_PACKETS, Math.min(MAX_TARGET_PACKETS, packets));
            } else {
                targetPackets = fixedTargetPackets;
            }
        }

        // Detect server reset or large sequence jumps
        if (nextSequence != -1) {
            int diff = (sequence - nextSequence + 65536) % 65536;
            if (diff > 32768) {
                // Packet looks like it's from the past.
                // If it's way in the past (e.g. more than 200 packets), 
                // it's likely a server restart/reset.
                if (diff < 65536 - 200) {
                    Log.w(TAG, "Probable server reset detected (seq=" + sequence + ", expected~" + nextSequence + ")");
                    clear(); // Reset everything
                } else {
                    latePackets++;
                    return; // Just a slightly late packet, ignore.
                }
            } else if (diff > 500) {
                // Large forward jump
                Log.w(TAG, "Large forward jump detected (" + diff + " packets), resetting...");
                clear();
            }
        }

        if (queue.size() >= MAX_BUFFER_PACKETS) {
            recyclePacket(queue.poll());
        }

        AudioPacket packet = acquirePacket();
        packet.set(sequence, timestamp, wallClock, data, length, false);
        queue.offer(packet);
        
        totalReceived++;
        
        // Windowed stats update
        long now = System.currentTimeMillis();
        if (lastWindowUpdate == 0) lastWindowUpdate = now;
        if (now - lastWindowUpdate >= 1000) {
            // Shift window
            windowIdx = (windowIdx + 1) % WINDOW_SIZE_SEC;
            lostPerSec[windowIdx] = 0;
            receivedPerSec[windowIdx] = 0;
            
            // Re-calculate loss rate
            long totalLostInWindow = 0;
            long totalRecvInWindow = 0;
            for (int i = 0; i < WINDOW_SIZE_SEC; i++) {
                totalLostInWindow += lostPerSec[i];
                totalRecvInWindow += receivedPerSec[i];
            }
            long expected = totalRecvInWindow + totalLostInWindow;
            currentLossRate = expected > 0 ? (totalLostInWindow * 100.0 / expected) : 0;
            
            lastWindowUpdate = now;
        }
        receivedPerSec[windowIdx]++;

        // Bitrate calculation
        if (lastReportTime == 0) lastReportTime = now;
        if (now - lastReportTime >= 1000) {
            long deltaPackets = totalReceived - lastReportedPackets;
            double bits = deltaPackets * length * 8; 
            currentBitrateKbps = bits / (now - lastReportTime);
            lastReportedPackets = totalReceived;
            lastReportTime = now;
        }

        notifyAll();
    }

    public synchronized AudioPacket take() {
        // Initial buffering to absorb jitter
        if (isBuffering) {
            if (queue.size() >= targetPackets) {
                isBuffering = false;
                if (nextSequence == -1) {
                    AudioPacket peek = queue.peek();
                    if (peek != null) nextSequence = peek.sequence;
                }
            } else {
                return null;
            }
        }

        AudioPacket packet = queue.peek();
        if (packet == null) {
            // Buffer empty but playing (starvation)
            isBuffering = true; // Lost sync, re-buffer
            return null;
        }

        if (packet.sequence == nextSequence) {
            queue.poll();
            nextSequence = (nextSequence + 1) & 0xFFFF;
            return packet;
        } else if (isEarlier(packet.sequence, nextSequence)) {
            // Late packet, already skipped, toss it
            recyclePacket(queue.poll());
            latePackets++;
            return take();
        } else {
            // Gap detected: current expected packet is missing.
            int gap = (packet.sequence - nextSequence + 65536) % 65535;
            if (gap > 50) {
                Log.w(TAG, "Gap too large (" + gap + "), jumping to " + packet.sequence);
                nextSequence = packet.sequence;
                isBuffering = true;
                return null;
            }

            Log.d(TAG, "Gap: expected " + nextSequence + " but got " + packet.sequence);
            
            // Return a mock packet indicating PLC should be used
            AudioPacket plcPacket = acquirePacket();
            plcPacket.set(nextSequence, packet.timestamp, 0, null, 0, true);
            nextSequence = (nextSequence + 1) & 0xFFFF;
            lostPackets++;
            lostPerSec[windowIdx]++;
            return plcPacket;
        }
    }

    public synchronized boolean isBuffering() {
        return isBuffering;
    }

    private boolean isEarlier(int seq1, int seq2) {
        int diff = (seq1 - seq2 + 65536) % 65536;
        return diff > 32768;
    }

    public synchronized Statistics getStatistics() {
        Statistics stats = new Statistics();
        stats.bufferLevel = queue.size();
        stats.delayMs = stats.bufferLevel * PACKET_DURATION_MS;
        stats.minDelay = currentMinDelay;
        stats.targetPackets = targetPackets;
        stats.jitterDev = currentJitterSD;
        stats.totalReceived = totalReceived;
        stats.lostPackets = lostPackets;
        stats.latePackets = latePackets;

        long now = System.currentTimeMillis();
        if (now - lastReportTime > 2000) {
            // Signal stagnation by zeroing bitrate
            stats.bitrateKbps = 0;
            stats.lossRate = 0;
        } else {
            stats.bitrateKbps = currentBitrateKbps;
            stats.lossRate = currentLossRate;
        }
        
        stats.lastTransitDelay = lastMeasuredTransitDelay;
        return stats;
    }

    public synchronized void resetStats() {
        totalReceived = 0;
        lostPackets = 0;
        latePackets = 0;
        currentBitrateKbps = 0;
        lastReportedPackets = 0;
        lastReportTime = 0;
        currentLossRate = 0;
        for(int i=0; i<WINDOW_SIZE_SEC; i++){
            lostPerSec[i] = 0;
            receivedPerSec[i] = 0;
        }
    }

    public synchronized void clear() {
        while (!queue.isEmpty()) {
            recyclePacket(queue.poll());
        }
        nextSequence = -1;
        isBuffering = true;
    }

    public synchronized void start() {
        isBuffering = true;
    }

    public synchronized void stop() {
        clear();
    }

    public synchronized int size() {
        return queue.size();
    }

    public synchronized void setBufferingMode(boolean adaptive, int fixedPackets) {
        this.isAdaptive = adaptive;
        this.fixedTargetPackets = fixedPackets;
        if (!adaptive) {
            this.targetPackets = fixedPackets;
        }
    }
}
