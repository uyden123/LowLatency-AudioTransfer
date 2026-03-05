package com.example.audiooverlan.network;

import android.util.Log;

import com.example.audiooverlan.audio.ByteArrayPool;

import java.io.DataInputStream;
import java.io.IOException;
import java.io.OutputStream;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;

/**
 * TCP client for AudioOverLAN streaming.
 * Uses length-prefixed framing for TCP stream.
 */
public class TcpRealtimeClient {
    private static final String TAG = "TcpClient";

    private Socket socket;
    private final String serverIp;
    private final int serverPort;
    private final AudioStreamListener listener;

    private final AtomicBoolean isRunning = new AtomicBoolean(false);
    private Thread receiveThread;

    private final ByteArrayPool bufferPool;
    private static final int POOL_SIZE = 100;
    private static final int BUFFER_SIZE = 4096;

    private final AtomicLong packetsReceived = new AtomicLong(0);
    private final AtomicLong bytesReceived = new AtomicLong(0);
    private final AtomicLong receiveErrors = new AtomicLong(0);
    private long startTime = 0;

    public TcpRealtimeClient(String serverIp, int serverPort, AudioStreamListener listener) {
        this.serverIp = serverIp;
        this.serverPort = serverPort;
        this.listener = listener;
        this.bufferPool = new ByteArrayPool(POOL_SIZE, BUFFER_SIZE);
    }

    public void start() {
        if (isRunning.get()) return;

        isRunning.set(true);
        startTime = System.currentTimeMillis();

        receiveThread = new Thread(this::runReceiveLoop, "TcpReceiver");
        receiveThread.start();
    }

    private void runReceiveLoop() {
        android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_AUDIO);

        while (isRunning.get()) {
            Socket s = new Socket();
            try {
                Log.d(TAG, "Attempting to connect to " + serverIp + ":" + serverPort);
                s.connect(new java.net.InetSocketAddress(serverIp, serverPort), 5000);
                this.socket = s;
                s.setTcpNoDelay(true); // Low latency
                s.setSoTimeout(10000);

                Log.i(TAG, "Connected to TCP server " + serverIp + ":" + serverPort);

                // Send SUBSCRIBE message
                OutputStream out = s.getOutputStream();
                out.write("SUBSCRIBE".getBytes(StandardCharsets.UTF_8));
                out.flush();
                Log.d(TAG, "SUBSCRIBE message sent");

                DataInputStream in = new DataInputStream(s.getInputStream());

                ByteBuffer tsBuffer = ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN);

                while (isRunning.get()) {
                    // Simplified Packet format: [2 bytes Length] [1 byte Codec] [8 bytes Timestamp] [N bytes Data]
                    int totalLength;
                    try {
                        totalLength = in.readUnsignedShort();
                    } catch (IOException e) {
                        Log.i(TAG, "Server closed the connection");
                        break;
                    }

                    if (totalLength < 50) { // Control messages are short strings, audio is much larger
                        byte[] msgBuf = new byte[totalLength];
                        in.readFully(msgBuf);
                        String msg = new String(msgBuf, StandardCharsets.UTF_8);
                        Log.i(TAG, "Server control message received: " + msg);
                        if (listener != null) listener.onMessage(msg, serverIp, serverPort);
                        continue;
                    }

                    // Read Codec Type (1 byte)
                    int codec = in.readUnsignedByte();

                    // Read Timestamp (8 bytes, little-endian)
                    byte[] tsBytes = new byte[8];
                    in.readFully(tsBytes);
                    tsBuffer.clear();
                    tsBuffer.put(tsBytes);
                    tsBuffer.flip();
                    long timestamp = tsBuffer.getLong();

                    // Read Audio Data directly
                    int audioLength = totalLength - 9;
                    byte[] audioData = bufferPool.acquire();
                    if (audioData.length < audioLength) {
                        audioData = new byte[audioLength];
                    }
                    in.readFully(audioData, 0, audioLength);

                    packetsReceived.incrementAndGet();
                    bytesReceived.addAndGet(audioLength);

                    if (listener != null) {
                        listener.onAudioPacket(codec, timestamp, audioData, audioLength);
                    }
                }
            } catch (IOException e) {
                if (isRunning.get()) {
                    Log.e(TAG, "TCP connection error, retrying...", e);
                    receiveErrors.incrementAndGet();
                    if (listener != null) listener.onError(e);
                    try { Thread.sleep(2000); } catch (InterruptedException ignored) {}
                }
            }
        }
    }

    public void releaseBuffer(byte[] buffer) {
        if (buffer != null) bufferPool.release(buffer);
    }

    public void close() {
        if (isRunning.get()) {
            isRunning.set(false);
            new Thread(() -> {
                try {
                    if (socket != null && !socket.isClosed()) {
                        OutputStream out = socket.getOutputStream();
                        out.write("UNSUBSCRIBE".getBytes(StandardCharsets.UTF_8));
                        out.flush();
                        socket.close();
                    }
                } catch (IOException ignored) {}
            }).start();
        }
        if (receiveThread != null) {
            receiveThread.interrupt();
        }
    }

    public long getPacketsReceived() { return packetsReceived.get(); }
}
