package com.example.audiooverlan.unused;

import android.util.Log;

import java.io.DataOutputStream;
import java.io.IOException;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.concurrent.atomic.AtomicBoolean;

public class TcpTransmitter {
    private static final String TAG = "TcpTransmitter";
    private final String ip;
    private final int port;
    private Socket socket;
    private DataOutputStream out;
    private final AtomicBoolean isRunning = new AtomicBoolean(false);
    private final ByteBuffer tsBuffer = ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN);

    public TcpTransmitter(String ip, int port) {
        this.ip = ip;
        this.port = port;
    }

    public void connect() throws IOException {
        socket = new Socket();
        socket.connect(new java.net.InetSocketAddress(ip, port), 5000);
        socket.setTcpNoDelay(true);
        out = new DataOutputStream(socket.getOutputStream());
        isRunning.set(true);
        Log.i(TAG, "Connected to " + ip + ":" + port);
    }

    public synchronized void sendAudio(int codec, long timestamp, byte[] data, int offset, int length) throws IOException {
        if (!isRunning.get() || out == null) return;

        // Packet format: [2 bytes Length] [1 byte Codec] [8 bytes Timestamp] [N bytes Data]
        // Length field = 1 + 8 + length
        int totalLength = 1 + 8 + length;

        out.writeShort(totalLength);
        out.writeByte(codec);

        tsBuffer.clear();
        tsBuffer.putLong(timestamp);
        out.write(tsBuffer.array());

        out.write(data, offset, length);
        out.flush();
    }

    public void close() {
        isRunning.set(false);
        try {
            if (out != null) out.close();
            if (socket != null) socket.close();
        } catch (IOException e) {
            Log.e(TAG, "Error closing transmitter", e);
        }
    }

    public boolean isConnected() {
        return isRunning.get() && socket != null && socket.isConnected();
    }
}
