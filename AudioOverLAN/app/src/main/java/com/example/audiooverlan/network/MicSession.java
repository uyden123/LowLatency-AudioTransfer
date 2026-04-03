package com.example.audiooverlan.network;

import android.os.ConditionVariable;
import android.util.Log;

import java.net.InetAddress;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.concurrent.ConcurrentHashMap;

public class MicSession {
    private static final String TAG = "MicSession";

    public enum State {
        Disconnected,
        Handshaking,
        Authenticated
    }

    private final InetAddress address;
    private final int port;
    private State state = State.Disconnected;
    private long lastSeenMillis;

    private String deviceName;
    private final UdpSender udpSender;
    private final OnControlMessageListener controlListener;

    public interface UdpSender {
        void sendPacket(byte[] data, InetAddress address, int port);
    }

    public interface OnControlMessageListener {
        void onControlMessage(String json, InetAddress address, int port);
    }

    // Protocol Constants
    public static final int CODEC_SYN = 250;
    public static final int CODEC_SYN_ACK = 251;
    public static final int CODEC_ACK_HANDSHAKE = 252;
    public static final int CODEC_DISCONNECT = 253;
    public static final int CODEC_ACK = 254;
    public static final int CODEC_HEARTBEAT = 140;
    public static final int CODEC_HEARTBEAT_ACK = 141;
    public static final int CODEC_CONTROL = 255;

    private final ConcurrentHashMap<Integer, ConditionVariable> pendingAcks = new ConcurrentHashMap<>();

    public interface OnStateChangeListener {
        void onStateChanged(State newState);
    }
    private OnStateChangeListener stateChangeListener;

    public void setStateChangeListener(OnStateChangeListener listener) {
        this.stateChangeListener = listener;
    }

    public MicSession(InetAddress address, int port, UdpSender udpSender, OnControlMessageListener controlListener) {
        this.address = address;
        this.port = port;
        this.udpSender = udpSender;
        this.controlListener = controlListener;
        this.lastSeenMillis = System.currentTimeMillis();
    }

    public InetAddress getAddress() { return address; }
    public int getPort() { return port; }
    public State getState() { return state; }
    public void setState(State state) { 
        this.state = state; 
        if (stateChangeListener != null) stateChangeListener.onStateChanged(state);
    }
    
    public String getDeviceName() { return deviceName; }

    public void updateLastSeen() {
        this.lastSeenMillis = System.currentTimeMillis();
    }

    public boolean isActive() {
        return (System.currentTimeMillis() - lastSeenMillis) < 5000;
    }

    public void processPacket(byte[] data, int length) {
        if (length < 1) return;
        updateLastSeen();

        if (length >= 12 && data[0] == 'D' && data[1] == 'E' && data[2] == 'V') {
            String msg = new String(data, 0, length, java.nio.charset.StandardCharsets.UTF_8).trim();
            if (msg.startsWith("DEVICE_NAME:")) {
                this.deviceName = msg.substring(12);
                return;
            }
        }

        if (length < 3) return;

        int codec = data[2] & 0xFF;
        switch (codec) {
            case CODEC_SYN:
                handleSyn();
                break;
            case CODEC_ACK_HANDSHAKE:
                handleAckHandshake();
                break;
            case CODEC_DISCONNECT:
                handleDisconnect();
                break;
            case CODEC_HEARTBEAT:
                handleHeartbeat();
                break;
            case CODEC_ACK:
                handleBinaryAck(data, length);
                break;
            case CODEC_CONTROL:
                handleControl(data, length);
                break;
            default:
                break;
        }
    }

    private void handleSyn() {
        state = State.Handshaking;
        Log.i(TAG, "SYN from " + address + ":" + port + ". Sending SYN_ACK.");

        byte[] synAck = new byte[3];
        synAck[2] = (byte) CODEC_SYN_ACK;
        for (int i = 0; i < 3; i++) {
            udpSender.sendPacket(synAck, address, port);
        }
    }

    private void handleAckHandshake() {
        Log.i(TAG, "ACK_HANDSHAKE from " + address + ":" + port);
        if (state == State.Handshaking) {
            state = State.Authenticated;
            Log.i(TAG, "Handshake COMPLETE: " + address + ":" + port + " is AUTHENTICATED.");
        }
    }

    private void handleHeartbeat() {
        if (state != State.Authenticated) return;

        Log.d(TAG, "Heartbeat from " + address + ":" + port);
        byte[] heartbeatAck = new byte[3];
        heartbeatAck[2] = (byte) CODEC_HEARTBEAT_ACK;
        for (int i = 0; i < 3; i++) {
            udpSender.sendPacket(heartbeatAck, address, port);
        }
    }

    private void handleDisconnect() {
        Log.i(TAG, "DISCONNECT received from " + address + ":" + port);
        state = State.Disconnected;
        lastSeenMillis = 0; // Force immediate timeout/removal
        if (stateChangeListener != null) {
            stateChangeListener.onStateChanged(state);
        }
    }

    private void handleBinaryAck(byte[] data, int length) {
        if (length < 23) return;
        int msgId = ByteBuffer.wrap(data, 19, 4).order(ByteOrder.LITTLE_ENDIAN).getInt();
        ConditionVariable cv = pendingAcks.remove(msgId);
        if (cv != null) cv.open();
    }

    private void handleControl(byte[] data, int length) {
        if (length < 23) return;
        int msgId = ByteBuffer.wrap(data, 19, 4).order(ByteOrder.LITTLE_ENDIAN).getInt();
        String json = new String(data, 23, length - 23, java.nio.charset.StandardCharsets.UTF_8).trim();
        
        // Send ACK
        sendBinaryAck(msgId);
        
        if (controlListener != null) {
            controlListener.onControlMessage(json, address, port);
        }
    }

    private void sendBinaryAck(int msgId) {
        byte[] data = new byte[23];
        data[2] = (byte) CODEC_ACK;
        ByteBuffer.wrap(data, 19, 4).order(ByteOrder.LITTLE_ENDIAN).putInt(msgId);
        udpSender.sendPacket(data, address, port);
    }
}
