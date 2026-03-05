package com.example.audiooverlan.network;

import android.util.Log;

import java.io.DataOutputStream;
import java.io.IOException;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.Collections;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * TCP Server that listens for incoming client connections and broadcasts audio packets.
 * Default port: 5003
 */
public class TcpAudioServer {
    private static final String TAG = "TcpAudioServer";
    private final int port;
    private ServerSocket serverSocket;
    private final AtomicBoolean isRunning = new AtomicBoolean(false);

    // Set of connected clients
    private final Set<ClientHandler> clients = Collections.newSetFromMap(new ConcurrentHashMap<>());
    private Thread acceptThread;

    public TcpAudioServer(int port) {
        this.port = port;
    }

    public void start() throws IOException {
        if (isRunning.get()) return;

        serverSocket = new ServerSocket(port);
        isRunning.set(true);
        Log.i(TAG, "Server started on port " + port);

        acceptThread = new Thread(() -> {
            while (isRunning.get()) {
                try {
                    Socket clientSocket = serverSocket.accept();
                    Log.i(TAG, "New client connected: " + clientSocket.getInetAddress());
                    ClientHandler handler = new ClientHandler(clientSocket);
                    // Don't add to clients here - handler will add itself after SUBSCRIBE
                    new Thread(handler).start();
                } catch (IOException e) {
                    if (isRunning.get()) {
                        Log.e(TAG, "Accept error", e);
                    }
                }
            }
        }, "TcpServerAccept");
        acceptThread.start();
    }

    /**
     * Broadcasts an audio packet to all connected clients.
     */
    public void broadcastAudio(int codec, long timestamp, byte[] data, int offset, int length) {
        if (!isRunning.get()) return;

        for (ClientHandler client : clients) {
            client.send(codec, timestamp, data, offset, length);
        }
    }

    public void broadcastControlMessage(String msg) {
        if (!isRunning.get()) return;
        byte[] msgBytes = msg.getBytes(java.nio.charset.StandardCharsets.UTF_8);
        for (ClientHandler client : clients) {
            client.send(255, 0, msgBytes, 0, msgBytes.length);
        }
    }

    public void stop() {
        isRunning.set(false);
        try {
            if (serverSocket != null) serverSocket.close();
        } catch (IOException ignored) {}

        for (ClientHandler client : clients) {
            client.close();
        }
        clients.clear();
    }

    private class ClientHandler implements Runnable {
        private final Socket socket;
        private DataOutputStream out;
        private final ByteBuffer tsBuffer = ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN);

        ClientHandler(Socket socket) {
            this.socket = socket;
        }

        @Override
        public void run() {
            try {
                socket.setTcpNoDelay(true);
                // Initialize output stream FIRST
                out = new DataOutputStream(socket.getOutputStream());

                // Read and validate SUBSCRIBE message from client
                byte[] initBuf = new byte[1024];
                int read = socket.getInputStream().read(initBuf);
                if (read > 0) {
                    String msg = new String(initBuf, 0, read).trim();
                    Log.d(TAG, "Client message: " + msg);

                    if ("SUBSCRIBE".equalsIgnoreCase(msg)) {
                        // Only add to broadcast list AFTER out is initialized and SUBSCRIBE validated
                        clients.add(this);
                        Log.i(TAG, "Client subscribed: " + socket.getInetAddress() + " (total: " + clients.size() + ")");
                    } else {
                        Log.w(TAG, "Invalid message from client, expected SUBSCRIBE: " + msg);
                        close();
                        return;
                    }
                } else {
                    Log.w(TAG, "Client disconnected before sending SUBSCRIBE");
                    close();
                    return;
                }

                // Keep connection alive, detect disconnect
                while (isRunning.get() && !socket.isClosed()) {
                    Thread.sleep(1000);
                }
            } catch (Exception e) {
                Log.d(TAG, "Client disconnected or error: " + e.getMessage());
            } finally {
                close();
            }
        }

        synchronized void send(int codec, long timestamp, byte[] data, int offset, int length) {
            if (out == null || socket.isClosed()) return;
            try {
                // Packet format: [2 bytes Length] [1 byte Codec] [8 bytes Timestamp] [N bytes Data]
                int totalLength = 1 + 8 + length;
                out.writeShort(totalLength);
                out.writeByte(codec);
                tsBuffer.clear();
                tsBuffer.putLong(timestamp);
                out.write(tsBuffer.array());
                out.write(data, offset, length);
                out.flush();
            } catch (IOException e) {
                Log.d(TAG, "Error sending to client, removing...");
                close();
            }
        }

        void close() {
            clients.remove(this);
            try {
                if (out != null) out.close();
                if (socket != null) socket.close();
            } catch (IOException ignored) {}
        }
    }
}
