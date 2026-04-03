package com.example.audiooverlan.network;

import android.content.Context;

public class TransmitterConnectionManager {
    private static final String TAG = "TransmitterConn";

    private final String targetIp;
    private final int targetPort;
    private final int sampleRate;
    private final int packetDuration;
    
    private UdpMicBroadcaster udpSender;
    private NsdMicAdvertiser nsdAdvertiser;
    private ConnectionListener listener;

    public interface ConnectionListener {
        void onServerConnected();
        void onServerDisconnected();
    }

    public TransmitterConnectionManager(Context context, String ip, int port, int sampleRate, int packetDuration, ConnectionListener listener) {
        this.targetIp = ip;
        this.targetPort = port;
        this.sampleRate = sampleRate;
        this.packetDuration = packetDuration;
        this.listener = listener;
        this.nsdAdvertiser = new NsdMicAdvertiser(context);
    }

    public void start() {
        udpSender = new UdpMicBroadcaster(targetIp, targetPort, sampleRate, packetDuration);
        udpSender.setConnectionListener(new UdpMicBroadcaster.OnConnectionStateListener() {
            @Override
            public void onConnected() { if (listener != null) listener.onServerConnected(); }
            @Override
            public void onDisconnected() { if (listener != null) listener.onServerDisconnected(); }
        });
        try {
            udpSender.start();
        } catch (Exception ignored) {}
        nsdAdvertiser.start();
    }

    public void stop() {
        if (udpSender != null) {
            udpSender.stop();
            udpSender = null;
        }
        if (nsdAdvertiser != null) {
            nsdAdvertiser.stop();
            nsdAdvertiser = null;
        }
    }

    public void sendPacket(byte[] data, int length) {
        if (udpSender != null) {
            udpSender.sendAudioPacket(data, length);
        }
    }

    public boolean isConnected() {
        return udpSender != null && udpSender.isConnected();
    }

    public long getPacketsSent() {
        return udpSender != null ? udpSender.getPacketsSent() : 0;
    }

    public long getBytesSent() {
        return udpSender != null ? udpSender.getBytesSent() : 0;
    }

    public java.util.List<com.example.audiooverlan.viewmodels.TransmitterState.ClientInfo> getActiveClients() {
        return udpSender != null ? udpSender.getActiveClients() : new java.util.ArrayList<>();
    }
}
