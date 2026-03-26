package com.example.audiooverlan.viewmodels;

public abstract class PlayerState {
    private PlayerState() {}

    public static final class Idle extends PlayerState {
        public static final Idle INSTANCE = new Idle();
    }

    public static final class Connecting extends PlayerState {
        public final String ip;
        public Connecting(String ip) { this.ip = ip; }
    }

    public static final class Playing extends PlayerState {
        public final String ip;
        public final long latency;
        public final long maxLatency;
        public final double avgLatency;
        public final int bufferLevel;
        public final int targetPackets;
        public final long transitDelay;
        public final int avgTransitDelay;
        public final double bitrateKbps;
        public final double lossRate;
        public final long latePackets;
        public final String codecName;
        public final float bufferMs;
        public final float playbackLatency;
        public final float totalLatency;

        public Playing(String ip, long latency, long maxLatency, double avgLatency,
                       int bufferLevel, int targetPackets, long transitDelay, int avgTransitDelay,
                       double bitrateKbps, double lossRate, long latePackets, String codecName,
                       float bufferMs, float playbackLatency, float totalLatency) {
            this.ip = ip;
            this.latency = latency;
            this.maxLatency = maxLatency;
            this.avgLatency = avgLatency;
            this.bufferLevel = bufferLevel;
            this.targetPackets = targetPackets;
            this.transitDelay = transitDelay;
            this.avgTransitDelay = avgTransitDelay;
            this.bitrateKbps = bitrateKbps;
            this.lossRate = lossRate;
            this.latePackets = latePackets;
            this.codecName = codecName;
            this.bufferMs = bufferMs;
            this.playbackLatency = playbackLatency;
            this.totalLatency = totalLatency;
        }
    }

    public static final class Reconnecting extends PlayerState {
        public static final Reconnecting INSTANCE = new Reconnecting();
    }

    public static final class Disconnected extends PlayerState {
        public static final Disconnected INSTANCE = new Disconnected();
    }
}
