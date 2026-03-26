package com.example.audiooverlan.network;

import android.util.Log;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * TCP client for reliable Control Messages (Mixer, Settings, Icons).
 * Protocol: [Int32 Length][JSON Body]
 */
public class TcpControlClient {
    private static final String TAG = "TcpControlClient";
    
    public interface OnMessageListener {
        void onMessage(String json);
    }
    
    public interface OnStateListener {
        void onConnected();
        void onDisconnected();
    }

    private final String serverIp;
    private final int serverPort;
    private OnMessageListener messageListener;
    private OnStateListener stateListener;
    
    private final AtomicBoolean isRunning = new AtomicBoolean(false);
    private Thread workerThread;
    private Socket socket;
    private final java.util.concurrent.ExecutorService sendExecutor = java.util.concurrent.Executors.newSingleThreadExecutor();

    public TcpControlClient(String serverIp, int serverPort) {
        this.serverIp = serverIp;
        this.serverPort = serverPort;
    }

    public void setListeners(OnMessageListener ml, OnStateListener sl) {
        this.messageListener = ml;
        this.stateListener = sl;
    }

    public void start() {
        if (isRunning.get()) return;
        isRunning.set(true);
        workerThread = new Thread(this::runLoop, "TcpControlWorker");
        workerThread.start();
    }

    private void runLoop() {
        while (isRunning.get()) {
                boolean wasConnected = false;
                try {
                    Log.i(TAG, "Connecting to TCP control at " + serverIp + ":" + serverPort);
                    socket = new Socket();
                    socket.connect(new InetSocketAddress(serverIp, serverPort), 5000);
                    wasConnected = true;
                    
                    if (stateListener != null) stateListener.onConnected();
                    
                    InputStream is = socket.getInputStream();
                    byte[] lenBuf = new byte[4];
                    
                    while (isRunning.get() && !socket.isClosed()) {
                        // Read 4-byte length (Little Endian)
                        int read = 0;
                        while (read < 4) {
                            int r = is.read(lenBuf, read, 4 - read);
                            if (r <= 0) throw new Exception("End of stream");
                            read += r;
                        }
                        
                        int len = ByteBuffer.wrap(lenBuf).order(ByteOrder.LITTLE_ENDIAN).getInt();
                        if (len <= 0 || len > 1024 * 1024) continue;
                        
                        byte[] jsonBuf = new byte[len];
                        int totalRead = 0;
                        while (totalRead < len) {
                            int r = is.read(jsonBuf, totalRead, len - totalRead);
                            if (r <= 0) throw new Exception("End of stream");
                            totalRead += r;
                        }
                        
                        String json = new String(jsonBuf, StandardCharsets.UTF_8);
                        if (messageListener != null) messageListener.onMessage(json);
                    }
                } catch (Exception e) {
                    if (isRunning.get()) {
                        Log.w(TAG, "TCP disconnected: " + e.getMessage());
                        if (wasConnected && stateListener != null) stateListener.onDisconnected();
                        try { Thread.sleep(2000); } catch (InterruptedException ex) { break; }
                    }
                } finally {
                    closeSocket();
                }
        }
    }

    public void send(String json) {
        sendExecutor.execute(() -> {
            try {
                Socket s = this.socket;
                if (s == null || !s.isConnected() || s.isClosed()) return;
                
                byte[] jsonBytes = json.getBytes(StandardCharsets.UTF_8);
                byte[] lenBytes = ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(jsonBytes.length).array();
                
                OutputStream os = s.getOutputStream();
                synchronized (os) {
                    os.write(lenBytes);
                    os.write(jsonBytes);
                    os.flush();
                }
            } catch (Exception e) {
                Log.e(TAG, "Failed to send TCP message: " + e.getMessage());
            }
        });
    }

    private void closeSocket() {
        try {
            if (socket != null) {
                socket.close();
                socket = null;
            }
        } catch (Exception ignored) {}
    }

    public void stop() {
        isRunning.set(false);
        closeSocket();
        if (workerThread != null) workerThread.interrupt();
        sendExecutor.shutdownNow();
    }
    
    public boolean isConnected() {
        return socket != null && socket.isConnected() && !socket.isClosed();
    }
}
