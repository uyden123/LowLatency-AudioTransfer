package com.example.audiooverlan.audio;

import android.util.Log;
import java.util.ArrayDeque;
import java.util.PriorityQueue;

/**
 * Simple sequence-number based jitter buffer.
 * Buffers a fixed amount of audio before starting playback.
 * Optimized with Object Pooling to eliminate GC pressure.
 */
public class JitterBuffer {
    private static final String TAG = "JitterBuffer";

    // Configuration
    private static final double PACKET_DURATION_MS = (double) AudioConstants.OPUS_FRAME_SIZE_MS;
    
    public enum BufferMode {
        LOW,
        MEDIUM,
        HIGH,
        CUSTOM
    }

    private BufferMode currentMode = BufferMode.MEDIUM;
    private int minTargetMs = 20;
    private int maxTargetMs = 60;
    private static final int MAX_BUFFER_PACKETS = AudioConstants.MAX_BUFFER_PACKETS;

    // Buffer state
    private final PriorityQueue<AudioPacket> queue;
    private final ArrayDeque<AudioPacket> pool = new ArrayDeque<>(MAX_BUFFER_PACKETS);
    private final Object poolLock = new Object();
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
    private int fixedTargetMs = 40; // Default 40ms, now auto-scales with frame size
    private int targetPackets = AudioConstants.MIN_TARGET_PACKETS; // Final current target (adaptive or fixed)

    private int overshootCount = 0;

    public static class AudioPacket {
        public int sequence;
        public long timestamp;
        public byte[] data;
        public int length;
        public boolean isPLC;
        public int codec;
        public long wallClock;

        public AudioPacket() {
            this.data = new byte[2048]; // Max Opus packet size
        }

        public void set(int sequence, int codec, long timestamp, long wallClock, byte[] data, int offset, int length, boolean isPLC) {
            this.sequence = sequence;
            this.codec = codec;
            this.timestamp = timestamp;
            this.wallClock = wallClock;
            if (this.data.length < length) this.data = new byte[length];
            if (data != null) {
                System.arraycopy(data, offset, this.data, 0, length);
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
        public int avgTransitDelay;  // New: Average transit delay in recent window
        public double lossRate; // New: % loss in recent window

        @Override
        public String toString() {
            return String.format("Buffer: %d/%d pkts Drift: %d Jitter: %.1fms Loss: %d (%.1f%%) Late: %d Bitrate: %.1f kbps Net: %dms",
                    bufferLevel, targetPackets, minDelay, jitterDev, lostPackets, lossRate, latePackets, bitrateKbps, lastTransitDelay);
        }
    }

    // MinDelay Clock Drift Monitor
    // JB-3 FIX: Use Long.MIN_VALUE as sentinel instead of 0, so that a real delay
    // of 0ms (perfect clock sync) is not silently ignored.
    private static final long DELAY_SENTINEL = Long.MIN_VALUE;
    private long[] delayHistory = new long[100]; // 2 seconds window at 20ms/packet
    private int delayIndex = 0;
    private int delayFilled = 0; // How many slots have been written at least once
    private long currentMinDelay = 0;
    private double currentJitterSD = 0;
    private long lastMeasuredTransitDelay = 0; // Relative to minDelay

    // JB-1/JB-2 FIX: Incremental running statistics to avoid O(n) scan per packet.
    // We maintain a running sum and use Welford's online algorithm for variance.
    private long delayRunningSum = 0;       // sum of all valid delay values in window
    private double welfordM2 = 0.0;         // Welford's M2 accumulator for variance
    private double welfordMean = 0.0;       // Welford's running mean
    private long welfordCount = 0;          // Number of samples processed by Welford
    // Running min: we still need to rescan when the current min is evicted.
    // To avoid full rescan on every eviction, we track a "min valid until" approach:
    // if the evicted value equals currentMinDelay, we do a one-time rescan.
    private boolean minNeedsRescan = false;

    // Drain phase: after initial buffering, actively pull buffer to minimum
    private boolean drainPhase = false;

    public JitterBuffer() {
        // JB-4 FIX: Comparator must return 0 for equal sequences (contract requirement).
        // Old code returned 1 for equal sequences, violating Comparator.compare(a,a)==0,
        // which can cause IllegalArgumentException or infinite loops in some JVM versions.
        this.queue = new PriorityQueue<>((p1, p2) -> {
            if (p1.sequence == p2.sequence) return 0; // duplicate — treat as equal
            int diff = (p1.sequence - p2.sequence + 65536) % 65536;
            return diff > 32768 ? -1 : 1;
        });

        // JB-3 FIX: Initialize delayHistory with DELAY_SENTINEL (Long.MIN_VALUE)
        // so that a real delay of 0ms is not confused with "no data yet".
        java.util.Arrays.fill(delayHistory, DELAY_SENTINEL);

        // Pre-fill pool to avoid allocation during initial bursts
        for (int i = 0; i < MAX_BUFFER_PACKETS; i++) {
            pool.push(new AudioPacket());
        }
    }

    // BUG 10 FIX: acquirePacket() is only called from synchronized methods (add, take),
    // so no additional synchronization is needed here. ArrayDeque.pollLast() is used
    // instead of Stack.pop() to avoid the synchronized overhead of java.util.Stack.
    private AudioPacket acquirePacket() {
        synchronized (poolLock) {
            AudioPacket p = pool.pollLast();
            return p != null ? p : new AudioPacket();
        }
    }

    public void recyclePacket(AudioPacket packet) {
        if (packet != null) {
            synchronized (poolLock) {
                if (pool.size() < MAX_BUFFER_PACKETS) {
                    pool.push(packet);
                }
            }
        }
    }

    public synchronized void add(int sequence, int codec, long timestamp, long wallClock, byte[] data, int offset, int length) {
        // Log every 100 packets
        if (totalReceived % 100 == 0) {
            Log.d(TAG, "Buffer status: size=" + queue.size() + "/" + targetPackets + " (Mode: " + currentMode + ")");
        }
        // Compute transit delay for Clock Drift Monitor
        long localArrival = System.currentTimeMillis();
        long delay = localArrival - wallClock;

        // JB-1/JB-2/JB-3 FIX: Incremental O(1) statistics using Welford's online algorithm.
        // Old code did two O(n) scans per packet (min+sum scan, then variance scan).
        // New code maintains running sum, Welford M2/mean, and only rescans for min
        // when the evicted slot held the current minimum (rare event).

        // --- Evict old slot ---
        long evicted = delayHistory[delayIndex];
        boolean evictedWasValid = (evicted != DELAY_SENTINEL);

        if (evictedWasValid) {
            // Remove evicted value from running stats
            delayRunningSum -= evicted;
            // Welford online removal (approximate — exact removal requires two-pass,
            // but for a sliding window of 200 samples this is accurate enough)
            welfordCount--;
            if (welfordCount > 0) {
                double oldMean = welfordMean;
                welfordMean = (welfordMean * (welfordCount + 1) - evicted) / welfordCount;
                welfordM2 -= (evicted - oldMean) * (evicted - welfordMean);
                if (welfordM2 < 0) welfordM2 = 0; // numerical guard
            } else {
                welfordMean = 0;
                welfordM2 = 0;
            }
            // If evicted value was the current min, we need to rescan
            if (evicted == currentMinDelay) {
                minNeedsRescan = true;
            }
        } else {
            delayFilled++;
        }

        // --- Write new slot ---
        // JB-3 FIX: Store actual delay (can be 0) — sentinel is Long.MIN_VALUE, not 0
        delayHistory[delayIndex] = delay;
        delayIndex = (delayIndex + 1) % delayHistory.length;

        // Add new value to running stats
        delayRunningSum += delay;
        welfordCount++;
        double delta = delay - welfordMean;
        welfordMean += delta / welfordCount;
        double delta2 = delay - welfordMean;
        welfordM2 += delta * delta2;

        // Update min
        if (delay < currentMinDelay || minNeedsRescan) {
            if (delay <= currentMinDelay) {
                currentMinDelay = delay;
                minNeedsRescan = false;
            } else {
                // Rescan needed: evicted slot held the old min
                long newMin = Long.MAX_VALUE;
                for (long d : delayHistory) {
                    if (d != DELAY_SENTINEL && d < newMin) newMin = d;
                }
                currentMinDelay = (newMin != Long.MAX_VALUE) ? newMin : delay;
                minNeedsRescan = false;
            }
        }

        // Calculate "Actual" transit time relative to the best case seen so far
        // We assume the best case (min delay) corresponds to roughly 5ms of real network time.
        lastMeasuredTransitDelay = (delay - currentMinDelay) + 5;
        if (lastMeasuredTransitDelay < 0) lastMeasuredTransitDelay = 0;

            // Standard Deviation from Welford's algorithm — O(1)
            if (welfordCount > 10) {
                currentJitterSD = Math.sqrt(welfordM2 / welfordCount);

                int minTargetPackets = (int) Math.max(1, Math.ceil(minTargetMs / PACKET_DURATION_MS));
                int maxTargetPackets = (int) Math.max(minTargetPackets, Math.ceil(maxTargetMs / PACKET_DURATION_MS));

                if (currentMode != BufferMode.CUSTOM) {
                    // Adaptive logic for Low, Medium, High modes
                    // Base 5ms + 3 Standard Deviations
                    int targetMs = 5 + (int) (currentJitterSD * 3);
                    int packets = (int) Math.ceil(targetMs / PACKET_DURATION_MS);
                    targetPackets = Math.max(minTargetPackets, Math.min(maxTargetPackets, packets));
                } else {
                    // Custom mode using fixed ms targets (non-adaptive or specific adaptive window)
                    // In Custom, minTargetMs acts as the fixed target if adaptive is not used, 
                    // but according to your request, we can use it as a range.
                    int packets = (int) Math.ceil(minTargetMs / PACKET_DURATION_MS);
                    targetPackets = Math.max(1, Math.min(maxTargetPackets, packets));
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
        packet.set(sequence, codec, timestamp, wallClock, data, offset, length, false);
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

    public synchronized AudioPacket peek() {
        return queue.peek();
    }

    public synchronized AudioPacket take() {
        if (isBuffering) {
            if (queue.size() >= targetPackets) {
                isBuffering = false;
                drainPhase = true; // Start drain to pull buffer to minimum
                if (nextSequence == -1) {
                    AudioPacket peek = queue.peek();
                    if (peek != null) nextSequence = peek.sequence;
                }
            } else {
                return null;
            }
        }

        // EXTREME DRAIN: Only for very severe buildup (e.g. 400ms+)
        int extremeThreshold = Math.max(targetPackets * 3, 20);
        if (queue.size() > extremeThreshold) {
            int toDrop = queue.size() - targetPackets - 2; // Leave a small safety margin
            for (int i = 0; i < toDrop && !queue.isEmpty(); i++) {
                recyclePacket(queue.poll());
            }
            AudioPacket newHead = queue.peek();
            if (newHead != null) {
                nextSequence = newHead.sequence;
            } else {
                // JB-5 FIX: If drain emptied the queue entirely, reset nextSequence so
                // that when the buffer refills, new packets are not treated as "late"
                // (which would cause them all to be silently dropped).
                nextSequence = -1;
                isBuffering = true;
            }
            Log.w(TAG, "Safety drain: Dropped " + toDrop + " packets due to severe bloat (" + queue.size() + " left)");
        }
        // Smooth drop is now DISABLED because we will use resampling speed instead.
        // We just reset the overshoot counter.
        overshootCount = 0;

        // BUG 3 FIX: Replace unbounded recursion with an iterative loop to prevent
        // StackOverflowError when many consecutive late packets arrive (e.g. after
        // server reset when old packets flood in).
        // BUG 4 FIX: Use modulo 65536 (not 65535) for 16-bit sequence number wrap.
        while (true) {
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
                // Late packet, already skipped, toss it (iterate instead of recurse)
                recyclePacket(queue.poll());
                latePackets++;
                // continue loop — BUG 3 FIX: was return take() (unbounded recursion)
            } else {
                // Gap detected: current expected packet is missing.
                int gap = (packet.sequence - nextSequence + 65536) % 65536;
                if (gap > 50) {
                    Log.w(TAG, "Gap too large (" + gap + "), jumping to " + packet.sequence);
                    nextSequence = packet.sequence;
                    isBuffering = true;
                    return null;
                }

                Log.d(TAG, "Gap: expected " + nextSequence + " but got " + packet.sequence + " (diff=" + gap + ")");

                // Return a mock packet indicating PLC should be used
                AudioPacket plcPacket = acquirePacket();
                plcPacket.set(nextSequence, 1, packet.timestamp, 0, null, 0, 0, true);
                nextSequence = (nextSequence + 1) & 0xFFFF;
                lostPackets++;
                lostPerSec[windowIdx]++;
                return plcPacket;
            }
        }
    }

    public synchronized boolean isBuffering() {
        return isBuffering;
    }

    private boolean isEarlier(int seq1, int seq2) {
        int diff = (seq1 - seq2 + 65536) % 65536;
        return diff > 32768;
    }

    public synchronized void fillStatistics(Statistics stats) {
        if (stats == null) return;
        stats.bufferLevel = queue.size();
        stats.delayMs = (int) (stats.bufferLevel * PACKET_DURATION_MS);
        stats.minDelay = currentMinDelay;
        stats.targetPackets = targetPackets;
        stats.jitterDev = currentJitterSD;
        stats.totalReceived = totalReceived;
        stats.lostPackets = lostPackets;
        stats.latePackets = latePackets;

        long now = System.currentTimeMillis();
        if (now - lastReportTime > 2000) {
            stats.bitrateKbps = 0;
            stats.lossRate = 0;
        } else {
            stats.bitrateKbps = currentBitrateKbps;
            stats.lossRate = currentLossRate;
        }
        stats.lastTransitDelay = lastMeasuredTransitDelay;
        
        // JB-3 FIX: Use running sum and welfordCount instead of O(n) scan.
        // avgTransitDelay = mean(delay) - currentMinDelay + 5
        stats.avgTransitDelay = welfordCount > 0
                ? (int)((welfordMean - currentMinDelay) + 5)
                : 0;
        if (stats.avgTransitDelay < 0) stats.avgTransitDelay = 0;
    }

    public synchronized Statistics getStatistics() {
        Statistics stats = new Statistics();
        stats.bufferLevel = queue.size();
        stats.delayMs = (int) (stats.bufferLevel * PACKET_DURATION_MS);
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
        // JB-3 FIX: Use running mean instead of O(n) scan
        stats.avgTransitDelay = welfordCount > 0
                ? (int)((welfordMean - currentMinDelay) + 5)
                : 0;
        if (stats.avgTransitDelay < 0) stats.avgTransitDelay = 0;
        
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

    public synchronized void waitForData(long timeoutMs) throws InterruptedException {
        if (queue.isEmpty() || (isBuffering && queue.size() < targetPackets)) {
            wait(timeoutMs);
        }
    }

    public synchronized void start() {
        isBuffering = true;
    }

    public synchronized void stop() {
        clear();
    }

    /**
     * Full reset including clock drift state. Used when reconnecting
     * to prevent stale data from causing buffer accumulation.
     */
    public synchronized void forceReset() {
        clear();
        resetStats();
        // JB-3 FIX: Fill with DELAY_SENTINEL instead of 0
        java.util.Arrays.fill(delayHistory, DELAY_SENTINEL);
        delayIndex = 0;
        delayFilled = 0;
        currentMinDelay = 0;
        currentJitterSD = 0;
        lastMeasuredTransitDelay = 0;
        lastWindowUpdate = 0;
        windowIdx = 0;
        overshootCount = 0;
        // JB-1/JB-2 FIX: Reset Welford running stats
        delayRunningSum = 0;
        welfordM2 = 0.0;
        welfordMean = 0.0;
        welfordCount = 0;
        minNeedsRescan = false;
        Log.i(TAG, "JitterBuffer force-reset complete (reconnection)");
    }

    public synchronized int size() {
        return queue.size();
    }

    public synchronized double getJitterSD() {
        return currentJitterSD;
    }

    public synchronized int getTargetPackets() {
        return targetPackets;
    }

    public synchronized boolean isDrainPhase() {
        return drainPhase;
    }

    public synchronized void clearDrainPhase() {
        drainPhase = false;
    }

    public synchronized void setBufferingConfig(BufferMode mode, int minMs, int maxMs) {
        this.currentMode = mode;
        switch (mode) {
            case LOW:
                this.minTargetMs = 10;
                this.maxTargetMs = 40;
                break;
            case MEDIUM:
                this.minTargetMs = 30;
                this.maxTargetMs = 80;
                break;
            case HIGH:
                this.minTargetMs = 100;
                this.maxTargetMs = 300;
                break;
            case CUSTOM:
                this.minTargetMs = minMs;
                this.maxTargetMs = maxMs;
                break;
        }
        
        // Recalculate immediate targetPackets
        this.targetPackets = (int) Math.max(1, Math.ceil(minTargetMs / PACKET_DURATION_MS));
        Log.i(TAG, "Buffer config updated: mode=" + mode + ", min=" + minTargetMs + "ms, max=" + maxTargetMs + "ms");
    }


}