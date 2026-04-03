package com.example.audiooverlan.network;

import android.util.Log;
import java.io.IOException;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.SocketTimeoutException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;
import org.json.JSONObject;

public class UdpMicBroadcaster {
    private static final String TAG = "UdpMicBroadcaster";

    private volatile DatagramSocket socket;
    private final int listenPort;

    private final AtomicBoolean isRunning = new AtomicBoolean(false);
    private final AtomicBoolean isConnected = new AtomicBoolean(false);
    private Thread connectionMonitorThread;
    private Thread receiveThread;

    private final AtomicLong packetsSent = new AtomicLong(0);
    private final AtomicLong bytesSent = new AtomicLong(0);
    
    private int seqNum = 0;
    private long timestampSamples = 0;
    private final int framesPerPacket;

    private final byte[] audioSendBuffer = new byte[1500];
    private final ByteBuffer audioHeaderWriter = ByteBuffer.wrap(audioSendBuffer);
    private DatagramPacket audioPacket;

    private final ConcurrentHashMap<String, MicSession> sessions = new ConcurrentHashMap<>();

    public interface OnConnectionStateListener {
        void onConnected();
        void onDisconnected();
    }
    private OnConnectionStateListener connectionListener;

    public UdpMicBroadcaster(int listenPort, int sampleRate, int packetDurationMs) {
        this.listenPort = listenPort;
        this.framesPerPacket = sampleRate * packetDurationMs / 1000;
        this.audioHeaderWriter.order(ByteOrder.LITTLE_ENDIAN);
    }

    // Constructor matching old signature, uses serverIp to proactively initiate connection
    public UdpMicBroadcaster(String serverIp, int serverPort, int sampleRate, int packetDurationMs) {
        this(serverPort, sampleRate, packetDurationMs);
        if (serverIp != null && !serverIp.isEmpty()) {
            try {
                InetAddress target = InetAddress.getByName(serverIp);
                forceAddClient(target, serverPort);
                Log.i(TAG, "Proactively broadcasting to explicitly set PC: " + serverIp);
            } catch (Exception e) {
                Log.e(TAG, "Failed to resolve initial target IP", e);
            }
        }
    }

    public void setConnectionListener(OnConnectionStateListener listener) {
        this.connectionListener = listener;
    }

    private String getSessionKey(InetAddress address, int port) {
        return address.getHostAddress() + ":" + port;
    }

    private MicSession getOrCreateSession(InetAddress address, int port) {
        String key = getSessionKey(address, port);
        MicSession session = sessions.get(key);
        if (session == null) {
            session = new MicSession(address, port, this::sendUdpInternal, this::handleControlMessage);
            session.setStateChangeListener(newState -> refreshConnectedState());
            sessions.put(key, session);
        }
        return session;
    }

    private void refreshConnectedState() {
        boolean anyAuthenticated = false;
        for (MicSession s : sessions.values()) {
            if (s.isActive() && s.getState() == MicSession.State.Authenticated) {
                anyAuthenticated = true;
                break;
            }
        }
        
        boolean old = isConnected.getAndSet(anyAuthenticated);
        if (old != anyAuthenticated && connectionListener != null) {
            if (anyAuthenticated) {
                connectionListener.onConnected();
            } else {
                connectionListener.onDisconnected();
            }
        }
    }

    public void forceAddClient(InetAddress address, int port) {
        MicSession session = getOrCreateSession(address, port);
        session.setState(MicSession.State.Authenticated);
        session.updateLastSeen();
        Log.i(TAG, "Proactively authenticated target PC at: " + getSessionKey(address, port));
    }

    private void sendUdpInternal(byte[] data, InetAddress address, int port) {
        DatagramSocket currentSocket = socket;
        if (currentSocket != null && !currentSocket.isClosed()) {
            try {
                DatagramPacket packet = new DatagramPacket(data, data.length, address, port);
                currentSocket.send(packet);
            } catch (Exception e) {
                Log.e(TAG, "Failed sending packet to " + address, e);
            }
        }
    }

    private void handleControlMessage(String json, InetAddress address, int port) {
        try {
            JSONObject obj = new JSONObject(json);
            String cmd = obj.optString("command");
            Log.i(TAG, "Control from " + address + ": " + cmd);
        } catch (Exception e) {
            Log.e(TAG, "JSON parse error", e);
        }
    }

    private synchronized boolean reconnectSocket() {
        try {
            if (socket != null && !socket.isClosed()) {
                socket.close();
            }
            socket = new DatagramSocket(listenPort);
            socket.setSoTimeout(1000); // For receiveThread
            Log.i(TAG, "Socket bound to port " + listenPort);
            return true;
        } catch (Exception e) {
            Log.e(TAG, "Socket creation failed", e);
            return false;
        }
    }

    public void start() throws IOException {
        if (!isRunning.compareAndSet(false, true)) return;

        if (!reconnectSocket()) {
            isRunning.set(false);
            return;
        }
        
        seqNum = 0;
        timestampSamples = 0;
        audioPacket = new DatagramPacket(audioSendBuffer, audioSendBuffer.length);
        refreshConnectedState(); // Initial check

        connectionMonitorThread = new Thread(() -> {
            while (isRunning.get()) {
                try {
                    Thread.sleep(2000);
                    
                    boolean anyAuthenticated = false;
                    List<String> toRemove = new ArrayList<>();

                    for (java.util.Map.Entry<String, MicSession> entry : sessions.entrySet()) {
                        MicSession session = entry.getValue();
                        if (!session.isActive()) {
                            toRemove.add(entry.getKey());
                            Log.i(TAG, "Session " + entry.getKey() + " timeout.");
                            continue;
                        }
                        if (session.getState() == MicSession.State.Authenticated) {
                            anyAuthenticated = true;
                        }
                    }

                    for (String key : toRemove) sessions.remove(key);
                    refreshConnectedState();
                } catch (InterruptedException e) {
                    break;
                } catch (Exception e) {
                    if (isRunning.get()) Log.e(TAG, "Monitor error", e);
                }
            }
        }, "UdpMicMonitor");
        connectionMonitorThread.start();

        receiveThread = new Thread(() -> {
            byte[] buf = new byte[1024];
            DatagramPacket pkt = new DatagramPacket(buf, buf.length);
            while (isRunning.get()) {
                DatagramSocket currentSocket = this.socket;
                if (currentSocket == null || currentSocket.isClosed()) {
                    try { Thread.sleep(100); } catch (Exception ignored) {}
                    continue;
                }
                try {
                    pkt.setLength(buf.length);
                    currentSocket.receive(pkt);
                    
                    MicSession session = getOrCreateSession(pkt.getAddress(), pkt.getPort());
                    session.processPacket(buf, pkt.getLength());
                } catch (SocketTimeoutException ignored) {
                } catch (Exception e) {
                    if (isRunning.get()) {
                        Log.e(TAG, "Receive error", e);
                        try { Thread.sleep(500); } catch (Exception ignored) {}
                    }
                }
            }
        }, "UdpMicReceive");
        receiveThread.start();
    }

    public void sendAudioPacket(byte[] opusData, int length) {
        DatagramSocket currentSocket = socket;
        if (!isRunning.get() || currentSocket == null || currentSocket.isClosed() || audioPacket == null) return;
        
        try {
            audioSendBuffer[0] = (byte) ((seqNum >> 8) & 0xFF);
            audioSendBuffer[1] = (byte) (seqNum & 0xFF);
            audioSendBuffer[2] = 1; // CODEC_AUDIO
            audioHeaderWriter.putLong(3, timestampSamples);
            audioHeaderWriter.putLong(11, System.currentTimeMillis());
            System.arraycopy(opusData, 0, audioSendBuffer, 19, length);
            audioPacket.setLength(19 + length);
            
            for (MicSession session : sessions.values()) {
                if (session.getState() == MicSession.State.Authenticated) {
                    try {
                        audioPacket.setAddress(session.getAddress());
                        audioPacket.setPort(session.getPort());
                        currentSocket.send(audioPacket);
                    } catch (Exception ignored) {}
                }
            }
            
            seqNum = (seqNum + 1) & 0xFFFF;
            timestampSamples += framesPerPacket;
            packetsSent.incrementAndGet();
            bytesSent.addAndGet(length);
        } catch (Exception e) {
            if (isRunning.get()) Log.e(TAG, "Send error", e);
        }
    }

    public void stop() {
        if (!isRunning.getAndSet(false)) return;

        if (socket != null && !socket.isClosed()) {
            try {
                byte[] disconnectPacket = new byte[3];
                disconnectPacket[2] = (byte) MicSession.CODEC_DISCONNECT;
                for (MicSession session : sessions.values()) {
                    if (session.getState() == MicSession.State.Authenticated) {
                        DatagramPacket p = new DatagramPacket(disconnectPacket, disconnectPacket.length, session.getAddress(), session.getPort());
                        for (int i = 0; i < 2; i++) socket.send(p); // Send twice for reliability
                    }
                }
            } catch (Exception ignored) {}
        }

        if (receiveThread != null) receiveThread.interrupt();
        if (connectionMonitorThread != null) connectionMonitorThread.interrupt();
        
        if (socket != null && !socket.isClosed()) socket.close();
        
        try { if (receiveThread != null) receiveThread.join(1000); } catch (InterruptedException ignored) {}
        try { if (connectionMonitorThread != null) connectionMonitorThread.join(1000); } catch (InterruptedException ignored) {}
        isConnected.set(false);
        sessions.clear();
    }

    public boolean isConnected() { return isConnected.get(); }
    public boolean isRunning() { return isRunning.get(); }
    public long getPacketsSent() { return packetsSent.get(); }
    public long getBytesSent() { return bytesSent.get(); }

    public List<com.example.audiooverlan.viewmodels.TransmitterState.ClientInfo> getActiveClients() {
        List<com.example.audiooverlan.viewmodels.TransmitterState.ClientInfo> activeClients = new ArrayList<>();
        for (MicSession session : sessions.values()) {
            if (session.isActive() && session.getState() == MicSession.State.Authenticated) {
                String ip = session.getAddress().getHostAddress();
                String name = session.getDeviceName();
                if (name == null || name.isEmpty()) {
                    name = ip;
                }
                activeClients.add(new com.example.audiooverlan.viewmodels.TransmitterState.ClientInfo(ip, name));
            }
        }
        return activeClients;
    }
}
