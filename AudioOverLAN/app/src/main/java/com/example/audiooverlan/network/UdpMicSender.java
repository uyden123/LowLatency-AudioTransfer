package com.example.audiooverlan.network;

import android.util.Log;
import android.os.ConditionVariable;

import java.io.IOException;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.SocketTimeoutException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.List;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;
import org.json.JSONObject;

public class UdpMicSender {
    private static final String TAG = "UdpMicBroadcaster";

    private volatile DatagramSocket socket;
    private final int listenPort;

    private final AtomicBoolean isRunning = new AtomicBoolean(false);
    private final AtomicBoolean isConnected = new AtomicBoolean(false);
    private Thread heartbeatThread;
    private Thread receiveThread;

    private final AtomicLong packetsSent = new AtomicLong(0);
    private final AtomicLong bytesSent = new AtomicLong(0);
    
    // Support legacy reliable control messages if needed
    private static final byte CODEC_CONTROL = (byte) 255;
    private static final byte CODEC_ACK = (byte) 254;
    private final ConcurrentHashMap<Integer, ConditionVariable> pendingAcks = new ConcurrentHashMap<>();
    private int controlMessageId = 0;

    private int seqNum = 0;
    private long timestampSamples = 0;
    private final int framesPerPacket;

    private final byte[] audioSendBuffer = new byte[1500];
    private final ByteBuffer audioHeaderWriter = ByteBuffer.wrap(audioSendBuffer);
    private DatagramPacket audioPacket;

    private final MicClientConnectionManager connectionManager;

    public interface OnConnectionStateListener {
        void onConnected();
        void onDisconnected();
    }
    private OnConnectionStateListener connectionListener;

    public UdpMicSender(int listenPort, int sampleRate, int packetDurationMs) {
        this.listenPort = listenPort;
        this.framesPerPacket = sampleRate * packetDurationMs / 1000;
        this.audioHeaderWriter.order(ByteOrder.LITTLE_ENDIAN);
        
        this.connectionManager = new MicClientConnectionManager((data, address, port) -> {
            DatagramSocket currentSocket = socket;
            if (currentSocket != null && !currentSocket.isClosed()) {
                try {
                    DatagramPacket packet = new DatagramPacket(data, data.length, address, port);
                    currentSocket.send(packet);
                } catch (Exception e) {
                    Log.e(TAG, "Failed sending packet", e);
                }
            }
        });
    }

    // Constructor matching old signature, uses serverIp to proactively initiate connection
    public UdpMicSender(String serverIp, int serverPort, int sampleRate, int packetDurationMs) {
        this(serverPort, sampleRate, packetDurationMs);
        if (serverIp != null && !serverIp.isEmpty()) {
            try {
                InetAddress target = InetAddress.getByName(serverIp);
                this.connectionManager.forceAddClient(target, serverPort);
                Log.i(TAG, "Proactively broadcasting to explicitly set PC: " + serverIp);
            } catch (Exception e) {
                Log.e(TAG, "Failed to resolve initial target IP", e);
            }
        }
    }

    public void setConnectionListener(OnConnectionStateListener listener) {
        this.connectionListener = listener;
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

        heartbeatThread = new Thread(() -> {
            while (isRunning.get()) {
                try {
                    Thread.sleep(2000);
                    connectionManager.cleanup();
                    List<UdpClientSession> clients = connectionManager.getAuthenticatedClients();
                    
                    boolean hasClients = !clients.isEmpty();
                    if (hasClients && !isConnected.get()) {
                        isConnected.set(true);
                        if (connectionListener != null) connectionListener.onConnected();
                    } else if (!hasClients && isConnected.get()) {
                        isConnected.set(false);
                        if (connectionListener != null) connectionListener.onDisconnected();
                    }
                } catch (InterruptedException e) {
                    break;
                } catch (Exception e) {
                    if (isRunning.get()) Log.e(TAG, "Heartbeat error", e);
                }
            }
        }, "UdpMicHeartbeat");
        heartbeatThread.start();

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
                    int length = pkt.getLength();

                    boolean handled = connectionManager.processUdpPacket(buf, length, pkt.getAddress(), pkt.getPort());
                    
                    if (!handled && length >= 19) {
                        int codec = buf[2] & 0xFF;
                        if (codec == 254) { // ACK
                            if (length >= 23) {
                                int msgId = ByteBuffer.wrap(buf, 19, 4).order(ByteOrder.LITTLE_ENDIAN).getInt();
                                ConditionVariable cv = pendingAcks.remove(msgId);
                                if (cv != null) cv.open();
                            }
                        } else if (codec == 255) { // CONTROL
                            if (length >= 23) {
                                int msgId = ByteBuffer.wrap(buf, 19, 4).order(ByteOrder.LITTLE_ENDIAN).getInt();
                                String json = new String(buf, 23, length - 23, StandardCharsets.UTF_8).trim();
                                sendBinaryAck(msgId, pkt.getAddress(), pkt.getPort());
                                processIncomingControl(json);
                            }
                        }
                    }
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
        
        List<UdpClientSession> activeClients = connectionManager.getAuthenticatedClients();
        if (activeClients.isEmpty()) return;

        try {
            audioSendBuffer[0] = (byte) ((seqNum >> 8) & 0xFF);
            audioSendBuffer[1] = (byte) (seqNum & 0xFF);
            audioSendBuffer[2] = 1; // CODEC_AUDIO
            audioHeaderWriter.putLong(3, timestampSamples);
            audioHeaderWriter.putLong(11, System.currentTimeMillis());
            System.arraycopy(opusData, 0, audioSendBuffer, 19, length);
            audioPacket.setLength(19 + length);
            
            for (UdpClientSession client : activeClients) {
                try {
                    audioPacket.setAddress(client.getAddress());
                    audioPacket.setPort(client.getPort());
                    currentSocket.send(audioPacket);
                } catch (Exception ignored) {}
            }
            
            seqNum = (seqNum + 1) & 0xFFFF;
            timestampSamples += framesPerPacket;
            packetsSent.incrementAndGet();
            bytesSent.addAndGet(length);
        } catch (Exception e) {
            if (isRunning.get()) Log.e(TAG, "Send error", e);
        }
    }

    private void processIncomingControl(String json) {
        try {
            JSONObject obj = new JSONObject(json);
            String cmd = obj.optString("command");
            Log.i(TAG, "Incoming command: " + cmd);
        } catch (Exception e) {
            Log.e(TAG, "JSON parse error", e);
        }
    }

    private void sendBinaryAck(int msgId, InetAddress address, int port) {
        DatagramSocket currentSocket = this.socket;
        if (currentSocket == null || currentSocket.isClosed()) return;
        try {
            byte[] data = new byte[23];
            data[2] = CODEC_ACK;
            ByteBuffer.wrap(data, 19, 4).order(ByteOrder.LITTLE_ENDIAN).putInt(msgId);
            DatagramPacket pkt = new DatagramPacket(data, data.length, address, port);
            currentSocket.send(pkt);
        } catch (Exception e) {
            Log.e(TAG, "Send ACK error", e);
        }
    }

    public void stop() {
        if (!isRunning.getAndSet(false)) return;

        if (receiveThread != null) receiveThread.interrupt();
        if (heartbeatThread != null) heartbeatThread.interrupt();
        
        if (socket != null && !socket.isClosed()) socket.close();
        
        try { if (receiveThread != null) receiveThread.join(1000); } catch (InterruptedException ignored) {}
        try { if (heartbeatThread != null) heartbeatThread.join(1000); } catch (InterruptedException ignored) {}
        isConnected.set(false);
    }

    public boolean isConnected() { return isConnected.get(); }
    public boolean isRunning() { return isRunning.get(); }
    public long getPacketsSent() { return packetsSent.get(); }
    public long getBytesSent() { return bytesSent.get(); }
}
