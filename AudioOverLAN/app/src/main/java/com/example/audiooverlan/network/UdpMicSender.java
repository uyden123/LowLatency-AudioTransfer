package com.example.audiooverlan.network;

import android.util.Log;

import java.io.IOException;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.SocketTimeoutException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;

/**
 * UDP sender for mic audio from Android to PC.
 * Sends Opus-encoded audio packets with the same format as PC→Android:
 *   [2B SeqNum BE] [1B Codec] [8B Timestamp LE] [8B WallClock LE] [N bytes Opus data]
 *   Total header: 19 bytes
 *
 * Also handles SUBSCRIBE/HEARTBEAT handshake with the PC receiver.
 */
public class UdpMicSender {
    private static final String TAG = "UdpMicSender";

    private DatagramSocket socket;
    private InetAddress serverAddr;
    private String serverIp;
    private int serverPort; // Dynamic target port (learned from client)
    private final int listenPort;

    private final AtomicBoolean isRunning = new AtomicBoolean(false);
    private final AtomicBoolean isConnected = new AtomicBoolean(false);
    private Thread heartbeatThread;
    private Thread receiveThread;

    private final AtomicLong packetsSent = new AtomicLong(0);
    private final AtomicLong bytesSent = new AtomicLong(0);
    private final AtomicLong lastAckTime = new AtomicLong(0);

    private int seqNum = 0;
    private long timestampSamples = 0;
    private final int framesPerPacket; // samples per channel per 20ms packet
    private final byte[] sendBuffer = new byte[1500]; // MTU size
    private final ByteBuffer headerWriters = ByteBuffer.wrap(sendBuffer);
    private DatagramPacket audioPacket;

    public interface OnConnectionStateListener {
        void onConnected();
        void onDisconnected();
    }

    private OnConnectionStateListener connectionListener;

    /**
     * @param serverIp   PC IP address (can be null for discovery mode)
     * @param serverPort PC UDP port (e.g. 5003)
     * @param sampleRate Audio sample rate (48000)
     * @param packetDurationMs Packet duration (20)
     */
    public UdpMicSender(String serverIp, int serverPort, int sampleRate, int packetDurationMs) {
        this.serverIp = serverIp;
        this.serverPort = serverPort;
        this.listenPort = serverPort; // Listen on the same port we send to (logical)
        this.framesPerPacket = sampleRate * packetDurationMs / 1000;
        this.headerWriters.order(ByteOrder.LITTLE_ENDIAN);
    }

    public void setConnectionListener(OnConnectionStateListener listener) {
        this.connectionListener = listener;
    }

    public void start() throws IOException {
        if (isRunning.get()) return;

        socket = new DatagramSocket(listenPort);
        socket.setSoTimeout(2000);
        isRunning.set(true);
        seqNum = 0;
        timestampSamples = 0;

        if (serverIp != null && !serverIp.isEmpty()) {
            serverAddr = InetAddress.getByName(serverIp);
            Log.i(TAG, "Starting UDP mic sender to " + serverIp + ":" + serverPort);
            
            // Initialize the pre-allocated audio packet once we have the address
            audioPacket = new DatagramPacket(sendBuffer, sendBuffer.length, serverAddr, serverPort);
            
            // Send initial SUBSCRIBE
            sendControlText("SUBSCRIBE");
        } else {
            Log.i(TAG, "Starting UDP mic sender in passive mode, waiting for discovery...");
        }

        // Heartbeat / Reconnection thread
        final boolean isPassiveMode = (serverIp == null || serverIp.isEmpty());
        heartbeatThread = new Thread(() -> {
            while (isRunning.get()) {
                try {
                    Thread.sleep(2000);
                    if (serverAddr != null && isConnected.get()) {
                        // Only send heartbeats if we are connected.
                        // If we are in passive mode, we never initiate SUBSCRIBE.
                        sendControlText("HEARTBEAT");
                    } else if (serverAddr != null && !isPassiveMode) {
                        // Only proactive subscribe if we weren't started in passive mode
                        Log.i(TAG, "Attempting to re-subscribe to PC...");
                        sendControlText("SUBSCRIBE");
                    }
                } catch (InterruptedException e) {
                    break;
                } catch (Exception e) {
                    if (isRunning.get()) Log.e(TAG, "Heartbeat error", e);
                }
            }
        }, "UdpMicHeartbeat");
        heartbeatThread.start();

        // Receive thread for ACKs and discovery
        receiveThread = new Thread(() -> {
            byte[] buf = new byte[1024];
            DatagramPacket pkt = new DatagramPacket(buf, buf.length);
            while (isRunning.get()) {
                try {
                    socket.receive(pkt);
                    String msg = new String(buf, 0, pkt.getLength(), StandardCharsets.UTF_8).trim();
                    lastAckTime.set(System.currentTimeMillis());

                    if (msg.equalsIgnoreCase("SUBSCRIBE")) {
                        // We are acting as Server - Respond with SUBSCRIBE_ACK
                        Log.i(TAG, "Received SUBSCRIBE from: " + pkt.getAddress());
                        sendControlTextTo("SUBSCRIBE_ACK", pkt.getAddress(), pkt.getPort());
                        
                        // Learning behavior: Always learn/update PC address AND PORT if we receive a subscribe
                        if (serverAddr == null || !serverAddr.equals(pkt.getAddress()) || serverPort != pkt.getPort()) {
                            serverAddr = pkt.getAddress();
                            serverIp = serverAddr.getHostAddress();
                            serverPort = pkt.getPort();
                            Log.i(TAG, "Learned/Updated target to: " + serverIp + ":" + serverPort);
                            audioPacket = new DatagramPacket(sendBuffer, sendBuffer.length, serverAddr, serverPort);
                        }

                        if (!isConnected.get()) {
                            isConnected.set(true);
                            if (connectionListener != null) connectionListener.onConnected();
                        }
                    } else if (msg.equalsIgnoreCase("SUBSCRIBE_ACK")) {
                        // We are acting as Client - Server acknowledged our subscription
                        if (!isConnected.get()) {
                            isConnected.set(true);
                            Log.i(TAG, "Connected to PC (SUBSCRIBE_ACK received)");
                            if (connectionListener != null) connectionListener.onConnected();
                        }
                    } else if (msg.equalsIgnoreCase("HEARTBEAT")) {
                        // Respond with HEARTBEAT_ACK
                        Log.i(TAG, "Received HEARTBEAT from: " + pkt.getAddress());
                        sendControlTextTo("HEARTBEAT_ACK", pkt.getAddress(), pkt.getPort());
                        
                        // Also learn/update target if we don't have one or if it changed
                        if (serverAddr == null || !serverAddr.equals(pkt.getAddress()) || serverPort != pkt.getPort()) {
                            serverAddr = pkt.getAddress();
                            serverIp = serverAddr.getHostAddress();
                            serverPort = pkt.getPort();
                            Log.i(TAG, "Learned/Updated target from HEARTBEAT: " + serverIp + ":" + serverPort);
                            audioPacket = new DatagramPacket(sendBuffer, sendBuffer.length, serverAddr, serverPort);
                        }

                        if (!isConnected.get()) {
                            isConnected.set(true);
                            if (connectionListener != null) connectionListener.onConnected();
                        }
                    } else if (msg.equalsIgnoreCase("HEARTBEAT_ACK")) {
                        // Server acknowledged our heartbeat
                        if (!isConnected.get()) {
                            isConnected.set(true);
                            if (connectionListener != null) connectionListener.onConnected();
                        }
                    }
                } catch (SocketTimeoutException e) {
                    // Check if we lost connection based on last received packet (ACK or anything)
                    if (isConnected.get() && lastAckTime.get() > 0) {
                        long elapsed = System.currentTimeMillis() - lastAckTime.get();
                        if (elapsed > 5000) { // 5s timeout
                            isConnected.set(false);
                            Log.w(TAG, "PC connection timeout (no HEARTBEAT_ACK/data for 5s)");
                            if (connectionListener != null) connectionListener.onDisconnected();
                        }
                    }
                } catch (Exception e) {
                    if (isRunning.get()) Log.e(TAG, "Receive error", e);
                }
            }
        }, "UdpMicReceiveAndDiscover");
        receiveThread.start();
    }

    /**
     * Send an Opus-encoded audio packet.
     * Thread-safe - can be called from the recording thread.
     *
     * @param opusData Opus encoded data
     * @param length   Length of valid data
     */
    public void sendAudioPacket(byte[] opusData, int length) {
        if (!isRunning.get() || socket == null || serverAddr == null || audioPacket == null) return;

        try {
            // Header: [2B SeqNum BE] [1B Codec] [8B Timestamp LE] [8B WallClock LE]
            
            // SeqNum (2 bytes BE)
            sendBuffer[0] = (byte) ((seqNum >> 8) & 0xFF);
            sendBuffer[1] = (byte) (seqNum & 0xFF);

            // Codec (1 byte) = 1 for Opus
            sendBuffer[2] = 1;

            // Timestamp (8 bytes LE) - using ByteBuffer wrap for speed
            headerWriters.putLong(3, timestampSamples);
            
            // WallClock (8 bytes LE)
            headerWriters.putLong(11, System.currentTimeMillis());

            // Audio data - Copy Opus data into the pre-allocated sendBuffer
            System.arraycopy(opusData, 0, sendBuffer, 19, length);

            // Update packet length and send
            audioPacket.setLength(19 + length);
            socket.send(audioPacket);

            seqNum = (short) ((seqNum + 1) & 0xFFFF);
            timestampSamples += framesPerPacket;

            packetsSent.incrementAndGet();
            bytesSent.addAndGet(length);

        } catch (Exception e) {
            if (isRunning.get()) Log.e(TAG, "Send error", e);
        }
    }

    /**
     * Send a control text message (SUBSCRIBE, HEARTBEAT, etc.) to the configured server.
     */
    private void sendControlText(String message) {
        if (serverAddr == null) return;
        sendControlTextTo(message, serverAddr, serverPort);
    }

    /**
     * Send a control text message to a specific address/port.
     */
    private void sendControlTextTo(String message, InetAddress address, int port) {
        if (socket == null || socket.isClosed()) return;
        try {
            byte[] msg = message.getBytes(StandardCharsets.UTF_8);
            DatagramPacket packet = new DatagramPacket(msg, msg.length, address, port);
            socket.send(packet);
        } catch (Exception e) {
            Log.e(TAG, "Failed to send " + message + " to " + address.getHostAddress(), e);
        }
    }

    public void stop() {
        if (!isRunning.getAndSet(false)) return;

        Log.i(TAG, "Stopping UDP mic sender...");

        // Send disconnect
        try {
            sendControlText("UNSUBSCRIBE");
        } catch (Exception ignored) {}

        if (receiveThread != null) receiveThread.interrupt();
        if (heartbeatThread != null) heartbeatThread.interrupt();

        if (socket != null && !socket.isClosed()) {
            socket.close();
        }

        isConnected.set(false);
        Log.i(TAG, "UDP mic sender stopped.");
    }

    public boolean isConnected() {
        return isConnected.get();
    }

    public boolean isRunning() {
        return isRunning.get();
    }

    public long getPacketsSent() {
        return packetsSent.get();
    }

    public long getBytesSent() {
        return bytesSent.get();
    }
}
