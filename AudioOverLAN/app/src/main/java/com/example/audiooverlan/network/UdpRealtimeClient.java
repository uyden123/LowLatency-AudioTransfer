package com.example.audiooverlan.network;

import android.os.Handler;
import android.os.Looper;
import android.util.Log;
import android.os.Build;

import com.example.audiooverlan.audio.JitterBuffer;

import java.io.IOException;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;
import android.os.ConditionVariable;
import org.json.JSONObject;

/**
 * UDP client for AudioOverLAN streaming.
 * Receives datagrams with [2B SeqNum] [1B Codec] [8B Timestamp] [N bytes Data].
 */
public class UdpRealtimeClient {
    private static final String TAG = "UdpClient";

    
    public interface OnConnectionStateListener {
        void onConnected();
        void onDisconnected();
        default void onDeviceNameReceived(String name) {}
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

    // Handshake Codecs
    private static final byte CODEC_SYN = (byte) 250;
    private static final byte CODEC_SYN_ACK = (byte) 251;
    private static final byte CODEC_ACK_HANDSHAKE = (byte) 252;
    private static final byte CODEC_ACK = (byte) 254;

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

    private final AtomicLong clockOffsetMs = new AtomicLong(0);

    public long getClockOffset() {
        return clockOffsetMs.get();
    }

    public void setControlMessageListener(OnControlMessageListener listener) {
        this.controlListener = listener;
    }

    public void setConnectionListener(OnConnectionStateListener listener) {
        this.connectionListener = listener;
    }

    private long startTime = 0;

    private synchronized boolean reconnectSocket() {
        if (!isRunning.get()) return false;
        try {
            if (socket != null && !socket.isClosed()) {
                socket.close();
            }
            socket = new DatagramSocket();
            socket.setSoTimeout(2000);
            // QoS: Set DSCP 46 (EF - Expedited Forwarding) for top priority
            try { socket.setTrafficClass(0x2E << 2); }
            catch (Exception e) { Log.w(TAG, "Failed to set TrafficClass: " + e.getMessage()); }
            InetAddress serverAddr = InetAddress.getByName(serverIp);
            socket.connect(serverAddr, serverPort);
            Log.i(TAG, "Socket connected to " + serverIp + ":" + serverPort);
            return true;
        } catch (Exception e) {
            Log.e(TAG, "Socket connect failed: " + e.getMessage());
            return false;
        }
    }

    public void start() {
        if (isRunning.get()) return;

        isRunning.set(true);
        startTime = System.currentTimeMillis();

        if (!reconnectSocket()) {
            isRunning.set(false);
            return;
        }

        // Keep-alive/Subscribe thread
        keepAliveThread = new Thread(() -> {
            try {
                byte[] subscribeMsg = "SUBSCRIBE".getBytes(StandardCharsets.UTF_8);
                byte[] activePing = new byte[] { (byte) 253 }; // Dummy ping to keep WiFi PSM awake
                long lastFullHeartbeat = 0;
                
                while (isRunning.get()) {
                    try {
                        InetAddress serverAddr = InetAddress.getByName(serverIp);
                        DatagramPacket subscribePacket = new DatagramPacket(subscribeMsg, subscribeMsg.length, serverAddr, serverPort);
                        DatagramPacket pingPacket = new DatagramPacket(activePing, 1, serverAddr, serverPort);
                        
                        DatagramSocket currentSocket = this.socket;
                        if (currentSocket == null || currentSocket.isClosed()) {
                            if (!reconnectSocket()) break;
                            Thread.sleep(1000);
                            continue;
                        }

                        if (isConnected.get()) {
                            long now = System.currentTimeMillis();
                            
                            // Send dummy ping to force WiFi radio to stay awake
                            currentSocket.send(pingPacket);
                            
                            if (now - lastFullHeartbeat >= 2000) {
                                String hbStr = "HEARTBEAT:" + now;
                                byte[] hbBytes = hbStr.getBytes(StandardCharsets.UTF_8);
                                DatagramPacket hbPacket = new DatagramPacket(hbBytes, hbBytes.length, serverAddr, serverPort);
                                currentSocket.send(hbPacket);
                                lastFullHeartbeat = now;
                                
                                // Faster UI disconnection detection (3s)
                                long lastRecv = lastPacketReceivedTime.get();
                                if (lastRecv > 0 && isConnected.get() && now - lastRecv > 3000) {
                                    Log.w(TAG, "No packets received for 3s, showing reconnecting UI");
                                    isConnected.set(false);
                                    if (connectionListener != null) {
                                        new Handler(Looper.getMainLooper()).post(connectionListener::onDisconnected);
                                    }
                                }
                            }
                            Thread.sleep(100); 
                        } else {
                            // Step 1: Send SYN
                            Log.i(TAG, "Handsake Step 1: Sending SYN to " + serverIp);
                            byte[] synPacket = new byte[3];
                            synPacket[2] = CODEC_SYN;
                            currentSocket.send(new DatagramPacket(synPacket, 3, serverAddr, serverPort));
                            Thread.sleep(1000); // Retry SYN every 1s
                        }
                    } catch (Exception e) {
                        if (isRunning.get()) {
                            Log.e(TAG, "Keep-alive/Network error: " + e.getMessage());
                            isConnected.set(false);
                            if (connectionListener != null) {
                                new Handler(Looper.getMainLooper()).post(connectionListener::onDisconnected);
                            }
                            reconnectSocket();
                            try { Thread.sleep(1000); } catch (Exception ignored) {}
                        }
                    }
                }
            } finally {
                cleanupInternal();
            }
        }, "UdpKeepAlive");
        keepAliveThread.start();

        receiveThread = new Thread(this::runReceiveLoop, "UdpReceiver");
        receiveThread.start();
    }

    private static long readInt64LittleEndian(byte[] data, int offset) {
        return ((long) data[offset + 7] << 56)
             | (((long) data[offset + 6] & 0xFF) << 48)
             | (((long) data[offset + 5] & 0xFF) << 40)
             | (((long) data[offset + 4] & 0xFF) << 32)
             | (((long) data[offset + 3] & 0xFF) << 24)
             | (((long) data[offset + 2] & 0xFF) << 16)
             | (((long) data[offset + 1] & 0xFF) << 8)
             | ((long) data[offset] & 0xFF);
    }

    private void runReceiveLoop() {
        android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_AUDIO);

        byte[] receiveData = new byte[8192];
        DatagramPacket receivePacket = new DatagramPacket(receiveData, receiveData.length);

        try {
            while (isRunning.get()) {
                DatagramSocket currentSocket = this.socket;
                if (currentSocket == null || currentSocket.isClosed()) {
                    try { Thread.sleep(100); } catch (Exception ignored) {}
                    continue;
                }
                try {
                    receivePacket.setLength(receiveData.length);
                    currentSocket.receive(receivePacket);
                    int length = receivePacket.getLength();
                    if (length < 3) continue;

                    // Any packet from server = connection is alive
                    lastPacketReceivedTime.set(System.currentTimeMillis());
                    
                    byte[] data = receivePacket.getData();
                    int possibleCodec = data[2] & 0xFF;

                    // 1, 250-252, 254, 255 are our binary packet codecs 
                    if (possibleCodec == 1 || (possibleCodec >= 250 && possibleCodec <= 252) || possibleCodec == 254 || possibleCodec == 255) {
                        if (length < 3) continue; // Minimum header for control is 3 bytes (Seq 2B + Codec 1B)

                        int seqNum = ((data[0] & 0xFF) << 8) | (data[1] & 0xFF);
                        int codec = possibleCodec;

                        // 1. Handle SYN_ACK (Handshake Step 2)
                        if (codec == (CODEC_SYN_ACK & 0xFF)) {
                            Log.i(TAG, "Handshake Step 2 RECEIVED: SYN_ACK. Sending ACK x3 + Immediate Heartbeat.");
                            
                            byte[] nameBytes = Build.MODEL.getBytes(StandardCharsets.UTF_8);
                            byte[] ackHandshake = new byte[3 + nameBytes.length];
                            ackHandshake[2] = CODEC_ACK_HANDSHAKE;
                            System.arraycopy(nameBytes, 0, ackHandshake, 3, nameBytes.length);
                            DatagramPacket ackPacket = new DatagramPacket(ackHandshake, ackHandshake.length, receivePacket.getAddress(), receivePacket.getPort());
                            
                            // 3x burst for reliability
                            for (int i = 0; i < 3; i++) {
                                currentSocket.send(ackPacket);
                            }

                            // Immediate heartbeat to promote state on server ASAP
                            String hbStr = "HEARTBEAT:" + System.currentTimeMillis();
                            byte[] hbBytes = hbStr.getBytes(StandardCharsets.UTF_8);
                            currentSocket.send(new DatagramPacket(hbBytes, hbBytes.length, receivePacket.getAddress(), receivePacket.getPort()));
                            
                            // Move to CONNECTED state
                            if (!isConnected.get()) {
                                isConnected.set(true);
                                if (connectionListener != null) {
                                    new Handler(Looper.getMainLooper()).post(connectionListener::onConnected);
                                }
                            }
                            continue;
                        }

                        // Handle Reliable Control Messages (Codec 255 / 0xFF)
                        if (codec == 255) {
                            if (length >= 23) {
                                int msgId = (data[19] & 0xFF) | ((data[20] & 0xFF) << 8) | ((data[21] & 0xFF) << 16) | ((data[22] & 0xFF) << 24);

                                String json = new String(data, 23, length - 23, StandardCharsets.UTF_8).trim();
                                Log.i(TAG, "Reliable Control received [" + msgId + "]: " + json);

                                // 1. Send ACK (Codec 254)
                                sendAck(msgId);

                                // 2. Notify listener
                                if (controlListener != null) {
                                    controlListener.onControlMessage(json);
                                }
                            }
                            continue;
                        }

                        // Handle binary ACKs (Codec 254)
                        if (codec == 254) {
                            if (length >= 23) {
                                int msgId = (data[19] & 0xFF) | ((data[20] & 0xFF) << 8) | ((data[21] & 0xFF) << 16) | ((data[22] & 0xFF) << 24);
                                ConditionVariable cv = pendingAcks.get(msgId);
                                if (cv != null) {
                                    cv.open();
                                }
                            }
                            continue;
                        }

                        // Receiving a valid audio packet header also confirms connection
                        if (!isConnected.get()) {
                            Log.i(TAG, "First packet received! Connection established with " + serverIp);
                            isConnected.set(true);
                            if (connectionListener != null) {
                                new Handler(Looper.getMainLooper()).post(connectionListener::onConnected);
                            }
                        }

                        long timestamp = readInt64LittleEndian(data, 3);
                        long wallClock = readInt64LittleEndian(data, 11);

                        // Interaction with JitterBuffer (Zero-allocation)
                        int audioLength = length - 19;
                        if (audioLength > 0 && jitterBuffer != null) {
                            jitterBuffer.add(seqNum, codec, timestamp, wallClock, data, 19, audioLength);
                        }

                        packetsReceived.incrementAndGet();
                        bytesReceived.addAndGet(Math.max(0, audioLength));
                    } else {
                        // It's likely a string message like SUBSCRIBE_ACK
                        if (length < 50) {
                            String possibleMsg = new String(data, 0, length, StandardCharsets.UTF_8).trim();
                            if (possibleMsg.startsWith("HEARTBEAT_ACK:")) {
                                String[] parts = possibleMsg.split(":");
                                if (parts.length >= 3) {
                                    try {
                                        long androidSentTime = Long.parseLong(parts[1]);
                                        long pcTime = Long.parseLong(parts[2]);
                                        long now = System.currentTimeMillis();
                                        long rtt = now - androidSentTime;
                                        long pcArrivalEst = pcTime + (rtt / 2);
                                        long offset = now - pcArrivalEst;
                                        long curOffset = clockOffsetMs.get();
                                        if (curOffset == 0) clockOffsetMs.set(offset);
                                        else clockOffsetMs.set((curOffset * 3 + offset) / 4);
                                    } catch (Exception e) {}
                                }
                                if (!isConnected.get()) {
                                    isConnected.set(true);
                                    if (connectionListener != null) {
                                        new Handler(Looper.getMainLooper()).post(connectionListener::onConnected);
                                    }
                                }
                            } else if (possibleMsg.equalsIgnoreCase("SUBSCRIBE_ACK") || possibleMsg.equalsIgnoreCase("HEARTBEAT_ACK")) {
                                if (!isConnected.get()) {
                                    isConnected.set(true);
                                    Log.i(TAG, "Connected to PC (" + possibleMsg + ")");
                                    if (connectionListener != null) {
                                        new Handler(Looper.getMainLooper()).post(connectionListener::onConnected);
                                    }
                                }
                            } else if (possibleMsg.startsWith("DEVICE_NAME:")) {
                                String name = possibleMsg.substring(12);
                                if (connectionListener != null) {
                                    new Handler(Looper.getMainLooper()).post(() -> connectionListener.onDeviceNameReceived(name));
                                }
                            }
                        }
                    }
                } catch (java.net.SocketTimeoutException e) {
                    // Regular poll timeout, just loop again
                } catch (IOException e) {
                    if (isRunning.get()) {
                        String msg = e.getMessage();
                        if (msg != null && (msg.contains("EBADF") || msg.contains("Socket closed"))) {
                            // Expected when socket is closed during receive
                            continue;
                        }
                        Log.e(TAG, "UDP receive error: " + e.getLocalizedMessage());
                    }
                }
            }
        } finally {
            cleanupInternal();
        }
    }

    private synchronized void cleanupInternal() {
        if (!isRunning.get()) {
            if (socket != null) {
                try { socket.close(); } catch (Exception ignored) {}
                socket = null;
            }
        }
    }

    private void sendAck(int msgId) {
        DatagramSocket currentSocket = this.socket;
        if (currentSocket == null || currentSocket.isClosed() || !isRunning.get()) return;
        try {
            byte[] data = new byte[23];
            data[2] = (byte) 254; // CODEC_ACK
            ByteBuffer.wrap(data, 19, 4).order(ByteOrder.LITTLE_ENDIAN).putInt(msgId);
            DatagramPacket pkt = new DatagramPacket(data, data.length);
            currentSocket.send(pkt);
        } catch (Exception e) {
            Log.e(TAG, "Failed to send ACK", e);
        }
    }

    private synchronized int getNextControlId() {
        return ++controlMessageId;
    }

    private final ConcurrentHashMap<Integer, ConditionVariable> pendingAcks = new ConcurrentHashMap<>();
    private int controlMessageId = 0;

    public boolean sendReliableControl(String json) {
        DatagramSocket currentSocket = this.socket;
        if (currentSocket == null || currentSocket.isClosed() || !isRunning.get()) return false;
        
        int msgId = getNextControlId();
        byte[] jsonBytes = json.getBytes(StandardCharsets.UTF_8);
        byte[] data = new byte[23 + jsonBytes.length];
        data[2] = (byte) 255;
        ByteBuffer.wrap(data, 19, 4).order(ByteOrder.LITTLE_ENDIAN).putInt(msgId);
        System.arraycopy(jsonBytes, 0, data, 23, jsonBytes.length);
        
        try {
            InetAddress address = InetAddress.getByName(serverIp);
            DatagramPacket pkt = new DatagramPacket(data, data.length, address, serverPort);
            ConditionVariable cv = new ConditionVariable();
            pendingAcks.put(msgId, cv);
            
            for (int i = 0; i < 5; i++) {
                currentSocket.send(pkt);
                if (cv.block(250)) return true;
                Log.w(TAG, "Retrying control message " + msgId + " (attempt " + (i+2) + ")");
            }
        } catch (Exception e) {
            Log.e(TAG, "Reliable send error", e);
        } finally {
            pendingAcks.remove(msgId);
        }
        return false;
    }

    public void close() {
        if (!isRunning.compareAndSet(true, false)) return;

        // BUG 2 FIX: Close the socket directly on the calling thread so that
        // receiveThread's blocking receive() is unblocked immediately.
        // Previously, socket.close() was done in a new background thread, causing
        // a race where receiveThread could still be reading a socket that was
        // concurrently being closed, leading to unhandled IOExceptions.
        DatagramSocket s = socket;
        if (s != null && !s.isClosed()) {
            // Best-effort UNSUBSCRIBE before closing
            try {
                byte[] msg = "UNSUBSCRIBE".getBytes(StandardCharsets.UTF_8);
                DatagramPacket packet = new DatagramPacket(msg, msg.length);
                s.send(packet);
            } catch (Exception ignored) {}
            s.close();
        }

        isConnected.set(false);

        // BUG 2 FIX: Notify listener on main thread to prevent UI crashes when
        // the listener updates UI components.
        OnConnectionStateListener listener = connectionListener;
        if (listener != null) {
            new Handler(Looper.getMainLooper()).post(listener::onDisconnected);
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

