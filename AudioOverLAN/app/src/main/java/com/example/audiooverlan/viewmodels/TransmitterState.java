package com.example.audiooverlan.viewmodels;

import androidx.annotation.NonNull;

public abstract class TransmitterState {
    private TransmitterState() {}

    public static final class Idle extends TransmitterState {
        public static final Idle INSTANCE = new Idle();
        private Idle() {}
        @NonNull @Override public String toString() { return "Idle"; }
    }

    public static final class Starting extends TransmitterState {
        public static final Starting INSTANCE = new Starting();
        private Starting() {}
        @NonNull @Override public String toString() { return "Starting"; }
    }

    public static final class ClientInfo {
        public final String ip;
        public final String deviceName;
        public ClientInfo(String ip, String deviceName) {
            this.ip = ip;
            this.deviceName = deviceName;
        }
    }

    public static final class Transmitting extends TransmitterState {
        public final String targetIp;
        public final long packetsSent;
        public final double bitrateKbps;
        public final long uptimeMillis;
        public final boolean isConnected;
        public final boolean isBluetoothAvailable;
        public final String activeSource;
        public final java.util.List<ClientInfo> clients;

        public Transmitting(String targetIp, long packetsSent, double bitrateKbps, long uptimeMillis, boolean isConnected, boolean isBluetoothAvailable, String activeSource, java.util.List<ClientInfo> clients) {
            this.targetIp = targetIp;
            this.packetsSent = packetsSent;
            this.bitrateKbps = bitrateKbps;
            this.uptimeMillis = uptimeMillis;
            this.isConnected = isConnected;
            this.isBluetoothAvailable = isBluetoothAvailable;
            this.activeSource = activeSource;
            this.clients = clients;
        }

        @NonNull @Override public String toString() { 
            return String.format("Transmitting(to=%s, packets=%d, bitrate=%.1f)", targetIp, packetsSent, bitrateKbps); 
        }
    }

    public static final class Error extends TransmitterState {
        public final String message;
        public Error(String message) { this.message = message; }
        @NonNull @Override public String toString() { return "Error: " + message; }
    }
}
