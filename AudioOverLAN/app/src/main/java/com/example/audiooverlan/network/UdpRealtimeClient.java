package com.example.audiooverlan.network;

import android.util.Log;

import com.example.audiooverlan.audio.JitterBuffer;

import java.io.IOException;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;

/**
 * UDP client for AudioOverLAN streaming.
 * Receives datagrams with [2B SeqNum] [1B Codec] [8B Timestamp] [N bytes Data].
 */
public class UdpRealtimeClient {
    private static final String TAG = "UdpClient";

    
    public interface OnConnectionStateListener {
        void onConnected();
        void onDisconnected();
    }

    public interface OnControlMessageListener {
        void onControlMessage(String message);
    }

    private OnControlMessageListener controlListener;
    private OnConnectionStateListener connectionListener;
    private final AtomicBoolean isConnected = new AtomicBoolean(false);
    private DatagramSocket socket;
    private final String serverIp;
    private final int serverPort;
    private final JitterBuffer jitterBuffer;

    private final AtomicBoolean isRunning = new AtomicBoolean(false);
    private Thread receiveThread;
    private Thread keepAliveThread;

    private final AtomicLong packetsReceived = new AtomicLong(0);
    private final AtomicLong bytesReceived = new AtomicLong(0);
    private final AtomicLong lastPacketReceivedTime = new AtomicLong(0);

    public UdpRealtimeClient(String serverIp, int serverPort, JitterBuffer jitterBuffer) {
        this.serverIp = serverIp;
        this.serverPort = serverPort;
        this.jitterBuffer = jitterBuffer;
    }

    public void setControlMessageListener(OnControlMessageListener listener) {
        this.controlListener = listener;
    }

    public void setConnectionListener(OnConnectionStateListener listener) {
        this.connectionListener = listener;
    }

    private long startTime = 0;

    public void start() {
        if (isRunning.get()) return;

        isRunning.set(true);
        startTime = System.currentTimeMillis();

        try {
            socket = new DatagramSocket();
            socket.setSoTimeout(2000); // 2s timeout for read
            InetAddress serverAddr = InetAddress.getByName(serverIp);
            socket.connect(serverAddr, serverPort);

            Log.i(TAG, "Attempting to connect via UDP to " + serverIp + ":" + serverPort);
        } catch (Exception e) {
            Log.e(TAG, "Failed to start UDP socket", e);
            isRunning.set(false);
            return;
        }

        byte[] msg = "SUBSCRIBE".getBytes(StandardCharsets.UTF_8);
        DatagramPacket packet = new DatagramPacket(msg, msg.length);

        try {
            socket.send(packet);
        } catch (Exception e) {
            Log.e(TAG, "Failed to send SUBSCRIBE ping", e);
        }

        // Keep-alive/Subscribe thread
        keepAliveThread = new Thread(() -> {
            byte[] subscribeMsg = "SUBSCRIBE".getBytes(StandardCharsets.UTF_8);
            byte[] heartbeatMsg = "HEARTBEAT".getBytes(StandardCharsets.UTF_8);
            DatagramPacket subscribePacket = new DatagramPacket(subscribeMsg, subscribeMsg.length);
            DatagramPacket heartbeatPacket = new DatagramPacket(heartbeatMsg, heartbeatMsg.length);

            while (isRunning.get()) {
                try {
                    if (isConnected.get()) {
                        socket.send(heartbeatPacket);
                    } else {
                        socket.send(subscribePacket);
                    }
                    Thread.sleep(2000); // 2 seconds keep-alive
                } catch (Exception e) {
                    if (isRunning.get()) Log.e(TAG, "Keep-alive error: " + e.getMessage());
                }
            }
        }, "UdpKeepAlive");
        keepAliveThread.start();

        receiveThread = new Thread(this::runReceiveLoop, "UdpReceiver");
        receiveThread.start();
    }

    private void runReceiveLoop() {
        android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_AUDIO);

        byte[] receiveData = new byte[8192];
        DatagramPacket receivePacket = new DatagramPacket(receiveData, receiveData.length);
        ByteBuffer tsBuffer = ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN);

        while (isRunning.get()) {
            try {
                socket.receive(receivePacket);

                int length = receivePacket.getLength();
                
                // Any packet from server = connection is alive
                lastPacketReceivedTime.set(System.currentTimeMillis());
                
                byte[] data = receivePacket.getData();

                // Check for control string messages (SUBSCRIBE_ACK, HEARTBEAT_ACK)
                if (length > 0 && length < 50) {
                    String possibleMsg = new String(data, 0, length, StandardCharsets.UTF_8).trim();
                    if (possibleMsg.equalsIgnoreCase("SUBSCRIBE_ACK") || possibleMsg.equalsIgnoreCase("HEARTBEAT_ACK")) {
                        if (!isConnected.get()) {
                            isConnected.set(true);
                            Log.i(TAG, "Connected to PC (" + possibleMsg + ")");
                            if (connectionListener != null) connectionListener.onConnected();
                        }
                        continue;
                    }
                }

                if (length < 19) continue; // Too small for full audio header (19 bytes)

                // 1. SeqNum (2 bytes Big Endian)
                int seqNum = ((data[0] & 0xFF) << 8) | (data[1] & 0xFF);

                // 2. Codec (1 byte)
                int codec = data[2] & 0xFF;

                // 3. Timestamp (8 bytes Little Endian)
                tsBuffer.clear();
                tsBuffer.put(data, 3, 8);
                tsBuffer.flip();
                long timestamp = tsBuffer.getLong();

                // 4. WallClock (8 bytes Little Endian)
                tsBuffer.clear();
                tsBuffer.put(data, 11, 8);
                tsBuffer.flip();
                long wallClock = tsBuffer.getLong();

                // 5. Audio Data
                int audioLength = length - 19;
                if (audioLength < 0) continue;
                byte[] audioData = new byte[audioLength];
                System.arraycopy(data, 19, audioData, 0, audioLength);

                packetsReceived.incrementAndGet();
                bytesReceived.addAndGet(audioLength);

                if (jitterBuffer != null) {
                    jitterBuffer.add(seqNum, timestamp, wallClock, audioData, audioLength);
                }

                // Handle Control Messages (Codec 255 / 0xFF)
                if (codec == 255) {
                    String msg = new String(audioData, StandardCharsets.UTF_8).trim();
                    Log.w(TAG, "Control message received: " + msg);
                    if (controlListener != null) {
                        controlListener.onControlMessage(msg);
                    }
                }
            } catch (java.net.SocketTimeoutException e) {
                // Check for connection timeout
                if (isConnected.get() && lastPacketReceivedTime.get() > 0) {
                    long elapsed = System.currentTimeMillis() - lastPacketReceivedTime.get();
                    if (elapsed > 5000) { // 5s timeout
                        isConnected.set(false);
                        Log.w(TAG, "PC Connection lost (timeout)");
                        if (connectionListener != null) connectionListener.onDisconnected();
                    }
                }
            } catch (IOException e) {
                if (isRunning.get()) {
                    Log.e(TAG, "UDP receive error", e);
                }
            }
        }
    }

    public void close() {
        if (isRunning.get()) {
            isRunning.set(false);
            
            // Try to send goodbye
            new Thread(() -> {
                try {
                    byte[] msg = "UNSUBSCRIBE".getBytes(StandardCharsets.UTF_8);
                    DatagramPacket packet = new DatagramPacket(msg, msg.length);
                    if (socket != null && !socket.isClosed()) {
                        socket.send(packet);
                    }
                } catch (Exception ignored) {}
                
                if (socket != null && !socket.isClosed()) {
                    socket.close();
                }
                isConnected.set(false);
                if (connectionListener != null) connectionListener.onDisconnected();
            }).start();
        }

        if (receiveThread != null) {
            receiveThread.interrupt();
        }
        if (keepAliveThread != null) {
            keepAliveThread.interrupt();
        }
    }

    public long getPacketsReceived() { return packetsReceived.get(); }

    /**
     * @return true if we have received at least one packet from the server.
     */
    public boolean hasReceivedAnyPacket() {
        return lastPacketReceivedTime.get() > 0;
    }

    /**
     * @return true if we received a packet from the server recently (active connection).
     */
    public boolean isConnected() {
        return isConnected.get();
    }
}

