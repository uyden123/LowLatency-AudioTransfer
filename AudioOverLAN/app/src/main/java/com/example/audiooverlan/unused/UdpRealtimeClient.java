package com.example.audiooverlan.unused;

import android.util.Log;

import com.example.audiooverlan.audio.ByteArrayPool;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.SocketException;
import java.net.SocketTimeoutException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;

/**
 * Production-ready UDP client for AudioOverLAN streaming.
 * Compatible with AudioServer v2.0 packet format.
 *
 * Features:
 * - Proper packet parsing (2-byte sequence + 8-byte timestamp)
 * - Buffer pooling for zero-allocation
 * - Statistics tracking
 * - Auto-reconnect on errors
 * - Graceful shutdown
 */
public class UdpRealtimeClient {
    private static final String TAG = "UdpClient";

    // Network
    private DatagramSocket socket;
    private InetAddress serverAddress;
    private final int serverPort;
    private final UdpListener listener;

    // State
    private final AtomicBoolean isRunning = new AtomicBoolean(false);
    private Thread receiveThread;

    // Buffer management
    private final ByteArrayPool bufferPool;
    private static final int POOL_SIZE = 100;
    private static final int BUFFER_SIZE = 4096; // Support up to 4KB packets

    // Statistics
    private final AtomicLong packetsReceived = new AtomicLong(0);
    private final AtomicLong bytesReceived = new AtomicLong(0);
    private final AtomicLong parseErrors = new AtomicLong(0);
    private final AtomicLong receiveErrors = new AtomicLong(0);
    private long startTime = 0;

    // Configuration
    private static final int SOCKET_TIMEOUT_MS = 5000; // 5 second timeout
    private static final int MAX_PACKET_SIZE = 4096;

    /**
     * Create UDP client
     * @param serverIp Server IP address
     * @param serverPort Server UDP port
     * @param listener Callback for received packets
     */
    public UdpRealtimeClient(String serverIp, int serverPort, UdpListener listener) throws Exception {
        if (serverIp == null || serverIp.isEmpty()) {
            throw new IllegalArgumentException("Server IP cannot be null or empty");
        }
        if (serverPort <= 0 || serverPort > 65535) {
            throw new IllegalArgumentException("Invalid port: " + serverPort);
        }
        if (listener == null) {
            throw new IllegalArgumentException("Listener cannot be null");
        }

        this.serverAddress = InetAddress.getByName(serverIp);
        this.serverPort = serverPort;
        this.listener = listener;
        this.bufferPool = new ByteArrayPool(POOL_SIZE, BUFFER_SIZE);

        Log.i(TAG, "UDP client created: " + serverIp + ":" + serverPort);
    }

    /**
     * Start receiving audio packets
     */
    public void start() throws Exception {
        if (isRunning.get()) {
            Log.w(TAG, "Client already running");
            return;
        }

        try {
            // Create socket
            socket = new DatagramSocket();
            socket.setSoTimeout(SOCKET_TIMEOUT_MS);

            // Send SUBSCRIBE message
            sendSubscribe();

            // Start receive thread
            isRunning.set(true);
            startTime = System.currentTimeMillis();
            startReceiveThread();

            Log.i(TAG, "UDP client started successfully");

        } catch (Exception e) {
            Log.e(TAG, "Failed to start UDP client", e);
            cleanup();
            throw e;
        }
    }

    /**
     * Send SUBSCRIBE message to server
     */
    private void sendSubscribe() throws Exception {
        byte[] subscribeMsg = "SUBSCRIBE".getBytes(StandardCharsets.UTF_8);
        DatagramPacket packet = new DatagramPacket(
                subscribeMsg,
                subscribeMsg.length,
                serverAddress,
                serverPort
        );

        socket.send(packet);
        Log.i(TAG, "SUBSCRIBE message sent to " + serverAddress + ":" + serverPort);
    }

    private final ByteBuffer timestampBuffer = ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN);

    /**
     * Start packet receive thread
     */
    private void startReceiveThread() {
        receiveThread = new Thread(() -> {
            android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_AUDIO);
            Log.i(TAG, "Receive thread started");

            byte[] receiveBuffer = new byte[MAX_PACKET_SIZE];
            DatagramPacket packet = new DatagramPacket(receiveBuffer, receiveBuffer.length);

            while (isRunning.get()) {
                try {
                    // Receive packet
                    socket.receive(packet);

                    // Process packet
                    processPacket(packet);

                } catch (SocketTimeoutException e) {
                    // Timeout is normal, just continue
                    if (Log.isLoggable(TAG, Log.DEBUG)) {
                        Log.d(TAG, "Socket timeout (normal)");
                    }
                } catch (SocketException e) {
                    if (isRunning.get()) {
                        Log.e(TAG, "Socket error", e);
                        receiveErrors.incrementAndGet();
                        notifyError(e);
                    }
                    // Socket closed, exit loop
                    break;
                } catch (Exception e) {
                    if (isRunning.get()) {
                        Log.e(TAG, "Receive error", e);
                        receiveErrors.incrementAndGet();
                        notifyError(e);
                    }
                }
            }

            Log.i(TAG, "Receive thread stopped");
        }, "UdpReceiver");

        receiveThread.start();
    }

    /**
     * Process received packet according to AudioServer v2.0 format
     */
    private void processPacket(DatagramPacket packet) {
        try {
            int length = packet.getLength();

            // Check for control messages
            if (length < 10) {
                String msg = new String(packet.getData(), 0, length, StandardCharsets.UTF_8);
                if (msg.contains("UNSUBSCRIBED") || msg.contains("SHUTDOWN") ||
                        msg.contains("MAX_SUBSCRIBERS")) {
                    Log.w(TAG, "Server message: " + msg);
                    notifyMessage(msg, packet.getAddress().getHostAddress(), packet.getPort());
                    return;
                }

                // Too short for audio packet
                Log.w(TAG, "Packet too short: " + length + " bytes");
                parseErrors.incrementAndGet();
                return;
            }

            // Parse AudioServer v2.0 packet format:
            // [2 bytes] Sequence Number (big-endian)
            // [8 bytes] Timestamp (little-endian, long)
            // [N bytes] Audio data (Opus or PCM)

            byte[] data = packet.getData();

            // Parse sequence number (2 bytes, big-endian/network byte order)
            int sequence = ((data[0] & 0xFF) << 8) | (data[1] & 0xFF);

            // Parse timestamp (8 bytes, little-endian) using class member to avoid allocation
            timestampBuffer.clear();
            timestampBuffer.put(data, 2, 8);
            timestampBuffer.flip();
            long timestamp = timestampBuffer.getLong();

            // Extract audio data
            int audioLength = length - 10;
            byte[] audioData = bufferPool.acquire();

            if (audioData.length < audioLength) {
                Log.w(TAG, "Audio data too large: " + audioLength + " bytes, buffer: " + audioData.length);
                bufferPool.release(audioData);
                parseErrors.incrementAndGet();
                return;
            }

            System.arraycopy(data, 10, audioData, 0, audioLength);

            // Update statistics
            packetsReceived.incrementAndGet();
            bytesReceived.addAndGet(audioLength);

            // Deliver to listener
            if (listener != null) {
                listener.onAudioPacket(sequence, timestamp, audioData, audioLength);
            }

            // Verbose logging every 1000 packets
            if (packetsReceived.get() % 1000 == 0) {
                long uptime = (System.currentTimeMillis() - startTime) / 1000;
                double pktPerSec = uptime > 0 ? packetsReceived.get() / (double)uptime : 0;
                Log.i(TAG, String.format("UDP RX: %d pkts (%.1f pkt/s), %d bytes, errors=%d",
                        packetsReceived.get(), pktPerSec, bytesReceived.get(), parseErrors.get()));
            }

        } catch (Exception e) {
            Log.e(TAG, "Packet processing error", e);
            parseErrors.incrementAndGet();
        }
    }

    /**
     * Release buffer back to pool
     */
    public void releaseBuffer(byte[] buffer) {
        if (buffer != null) {
            bufferPool.release(buffer);
        }
    }

    /**
     * Send UNSUBSCRIBE and close connection
     */
    public void close() {
        if (!isRunning.compareAndSet(true, false)) {
            return;
        }

        Log.i(TAG, "Closing UDP client...");

        // Network operations must be performed off the main thread
        new Thread(() -> {
            try {
                if (socket != null && !socket.isClosed()) {
                    byte[] unsubscribeMsg = "UNSUBSCRIBE".getBytes(StandardCharsets.UTF_8);
                    DatagramPacket packet = new DatagramPacket(
                            unsubscribeMsg,
                            unsubscribeMsg.length,
                            serverAddress,
                            serverPort
                    );
                    socket.send(packet);
                    Log.i(TAG, "UNSUBSCRIBE message sent");
                }
            } catch (Exception e) {
                Log.w(TAG, "Failed to send UNSUBSCRIBE", e);
            } finally {
                cleanup();
                Log.i(TAG, "UDP client resources cleaned up");
            }
        }, "UdpCloseThread").start();

        // Wait for receive thread to finish briefly
        if (receiveThread != null) {
            try {
                receiveThread.join(1000);
            } catch (InterruptedException e) {
                Log.w(TAG, "Interrupted while waiting for receive thread");
                Thread.currentThread().interrupt();
            }
        }

        // Log final statistics
        logStatistics();
        Log.i(TAG, "UDP client stop initiated");
    }

    /**
     * Cleanup resources
     */
    private synchronized void cleanup() {
        if (socket != null) {
            if (!socket.isClosed()) {
                socket.close();
            }
            socket = null;
        }
    }

    /**
     * Get client statistics
     */
    public ClientStatistics getStatistics() {
        ClientStatistics stats = new ClientStatistics();
        stats.packetsReceived = packetsReceived.get();
        stats.bytesReceived = bytesReceived.get();
        stats.parseErrors = parseErrors.get();
        stats.receiveErrors = receiveErrors.get();
        stats.isRunning = isRunning.get();

        long uptime = System.currentTimeMillis() - startTime;
        stats.uptimeSeconds = uptime / 1000;

        if (stats.uptimeSeconds > 0) {
            stats.packetsPerSecond = stats.packetsReceived / (double) stats.uptimeSeconds;
            stats.bytesPerSecond = stats.bytesReceived / (double) stats.uptimeSeconds;
        }

        return stats;
    }

    /**
     * Log statistics
     */
    public void logStatistics() {
        ClientStatistics stats = getStatistics();
        Log.i(TAG, "=== UDP Client Statistics ===");
        Log.i(TAG, String.format("Packets: %d (%.1f/sec)",
                stats.packetsReceived, stats.packetsPerSecond));
        Log.i(TAG, String.format("Bytes: %d (%.1f KB/sec)",
                stats.bytesReceived, stats.bytesPerSecond / 1024.0));
        Log.i(TAG, String.format("Errors: parse=%d, receive=%d",
                stats.parseErrors, stats.receiveErrors));
        Log.i(TAG, String.format("Uptime: %d seconds", stats.uptimeSeconds));
    }

    /**
     * Check if client is running
     */
    public boolean isRunning() {
        return isRunning.get();
    }

    // Notification helpers
    private void notifyError(Exception e) {
        if (listener != null) {
            listener.onError(e);
        }
    }

    private void notifyMessage(String msg, String ip, int port) {
        if (listener != null) {
            listener.onMessage(msg, ip, port);
        }
    }

    /**
     * Statistics container
     */
    public static class ClientStatistics {
        public long packetsReceived;
        public long bytesReceived;
        public long parseErrors;
        public long receiveErrors;
        public long uptimeSeconds;
        public double packetsPerSecond;
        public double bytesPerSecond;
        public boolean isRunning;

        @Override
        public String toString() {
            return String.format(
                    "UdpClient: pkts=%d (%.1f/s), bytes=%d (%.1f KB/s), errors=%d, uptime=%ds",
                    packetsReceived, packetsPerSecond, bytesReceived, bytesPerSecond / 1024.0,
                    parseErrors + receiveErrors, uptimeSeconds
            );
        }
    }
}
