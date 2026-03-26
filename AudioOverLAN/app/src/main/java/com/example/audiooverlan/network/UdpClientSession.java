package com.example.audiooverlan.network;

import java.net.InetAddress;

public class UdpClientSession {
    public enum HandshakeState {
        Disconnected,
        SynReceived,
        Authenticated
    }

    private final InetAddress address;
    private final int port;
    private HandshakeState state;
    private long lastSeenMillis;

    public UdpClientSession(InetAddress address, int port) {
        this.address = address;
        this.port = port;
        this.state = HandshakeState.Disconnected;
        this.lastSeenMillis = System.currentTimeMillis();
    }

    public InetAddress getAddress() {
        return address;
    }

    public int getPort() {
        return port;
    }

    public HandshakeState getState() {
        return state;
    }

    public void setState(HandshakeState state) {
        this.state = state;
    }

    public void updateLastSeen() {
        this.lastSeenMillis = System.currentTimeMillis();
    }

    public boolean isActive() {
        return (System.currentTimeMillis() - lastSeenMillis) < 5000;
    }
}
