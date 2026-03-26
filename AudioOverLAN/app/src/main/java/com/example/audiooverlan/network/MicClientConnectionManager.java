package com.example.audiooverlan.network;

import android.util.Log;
import java.net.InetAddress;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.ConcurrentHashMap;

public class MicClientConnectionManager {
    private static final String TAG = "MicClientManager";

    private final ConcurrentHashMap<String, UdpClientSession> clients = new ConcurrentHashMap<>();
    private final UdpSender udpSender;

    public static final int CODEC_SYN = 250;
    public static final int CODEC_SYN_ACK = 251;
    public static final int CODEC_ACK_HANDSHAKE = 252;

    public interface UdpSender {
        void sendPacket(byte[] data, InetAddress address, int port);
    }

    public MicClientConnectionManager(UdpSender udpSender) {
        this.udpSender = udpSender;
    }

    public boolean processUdpPacket(byte[] data, int length, InetAddress senderAddr, int senderPort) {
        if (length < 3) return false;
        
        int codec = data[2] & 0xFF;

        switch (codec) {
            case CODEC_SYN:
                handleSyn(senderAddr, senderPort);
                return true;

            case CODEC_ACK_HANDSHAKE:
                handleAckHandshake(senderAddr, senderPort);
                return true;

            default:
                // Any other packet from an authenticated client acts as keep-alive
                updateActive(senderAddr, senderPort, false);
                return false;
        }
    }

    private String getClientKey(InetAddress addr, int port) {
        return addr.getHostAddress() + ":" + port;
    }

    private void handleSyn(InetAddress senderAddr, int senderPort) {
        String key = getClientKey(senderAddr, senderPort);
        UdpClientSession session = clients.get(key);
        if (session == null) {
            session = new UdpClientSession(senderAddr, senderPort);
            clients.put(key, session);
        }
        session.setState(UdpClientSession.HandshakeState.SynReceived);
        session.updateLastSeen();

        Log.i(TAG, "SYN from " + key + ". Sending SYN_ACK.");

        byte[] synAck = new byte[3];
        synAck[2] = (byte) CODEC_SYN_ACK;

        // Multi-send for reliability over UDP
        for (int i = 0; i < 3; i++) {
            udpSender.sendPacket(synAck, senderAddr, senderPort);
        }
    }

    private void handleAckHandshake(InetAddress senderAddr, int senderPort) {
        String key = getClientKey(senderAddr, senderPort);
        UdpClientSession session = clients.get(key);
        if (session != null && session.getState() == UdpClientSession.HandshakeState.SynReceived) {
            session.setState(UdpClientSession.HandshakeState.Authenticated);
            session.updateLastSeen();
            Log.i(TAG, "Handshake COMPLETE: " + key + " is now AUTHENTICATED.");
        }
    }

    public void forceAddClient(InetAddress addr, int port) {
        updateActive(addr, port, true);
        Log.i(TAG, "Proactively authenticated target PC at: " + getClientKey(addr, port));
    }

    private void updateActive(InetAddress senderAddr, int senderPort, boolean forceAuthenticated) {
        String key = getClientKey(senderAddr, senderPort);
        UdpClientSession session = clients.get(key);
        if (session == null) {
            if (forceAuthenticated) {
                session = new UdpClientSession(senderAddr, senderPort);
                session.setState(UdpClientSession.HandshakeState.Authenticated);
                clients.put(key, session);
            }
            return; // Ignore raw packets from totally unknown clients
        }
        session.updateLastSeen();

        if (session.getState() == UdpClientSession.HandshakeState.SynReceived || forceAuthenticated) {
            session.setState(UdpClientSession.HandshakeState.Authenticated);
        }
    }

    public List<UdpClientSession> getAuthenticatedClients() {
        List<UdpClientSession> active = new ArrayList<>();
        List<String> toRemove = new ArrayList<>();
        
        for (java.util.Map.Entry<String, UdpClientSession> entry : clients.entrySet()) {
            UdpClientSession session = entry.getValue();
            if (session.isActive()) {
                if (session.getState() == UdpClientSession.HandshakeState.Authenticated) {
                    active.add(session);
                }
            } else {
                toRemove.add(entry.getKey());
            }
        }
        
        for (String key : toRemove) {
            clients.remove(key);
            Log.i(TAG, "Client " + key + " removed due to timeout.");
        }
        
        return active;
    }
    
    public void cleanup() {
        getAuthenticatedClients(); // triggers timeout removal internally
    }
}
