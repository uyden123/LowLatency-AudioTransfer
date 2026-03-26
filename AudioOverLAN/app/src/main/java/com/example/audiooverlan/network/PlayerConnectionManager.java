package com.example.audiooverlan.network;

import android.util.Log;

import com.example.audiooverlan.audio.JitterBuffer;

import org.json.JSONObject;

import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public class PlayerConnectionManager {
    private static final String TAG = "ConnectionManager";

    private final String serverIp;
    private final int port;
    private final JitterBuffer jitterBuffer;
    private UdpRealtimeClient udpClient;
    private TcpControlClient tcpControlClient;
    private ConnectionListener listener;
    
    private final ExecutorService executor = Executors.newSingleThreadExecutor();

    public interface ConnectionListener {
        void onControlMessage(String message);
        void onDeviceNameReceived(String name);
        void onConnected();
        void onDisconnected();
    }

    public PlayerConnectionManager(String ip, int port, JitterBuffer jitterBuffer, ConnectionListener listener) {
        this.serverIp = ip;
        this.port = port;
        this.jitterBuffer = jitterBuffer;
        this.listener = listener;
    }

    public void start() {
        executor.execute(() -> {
            try {
                // Initialize UDP Client
                udpClient = new UdpRealtimeClient(serverIp, port, jitterBuffer);
                udpClient.setConnectionListener(new UdpRealtimeClient.OnConnectionStateListener() {
                    @Override
                    public void onConnected() { if (listener != null) listener.onConnected(); }
                    @Override
                    public void onDisconnected() { if (listener != null) listener.onDisconnected(); }
                    @Override
                    public void onDeviceNameReceived(String name) { if (listener != null) listener.onDeviceNameReceived(name); }
                });
                udpClient.setControlMessageListener(msg -> { if (listener != null) listener.onControlMessage(msg); });
                udpClient.start();

                // Initialize TCP Control Client
                tcpControlClient = new TcpControlClient(serverIp, port + 1);
                tcpControlClient.setListeners(
                    msg -> { if (listener != null) listener.onControlMessage(msg); },
                    new TcpControlClient.OnStateListener() {
                        @Override
                        public void onConnected() { }
                        @Override
                        public void onDisconnected() {
                            if (listener != null) listener.onDisconnected();
                        }
                    }
                );
                tcpControlClient.start();

            } catch (Exception e) {
                Log.e(TAG, "Failed to start connection manager", e);
            }
        });
    }

    public void stop() {
        try {
            JSONObject stopCmd = new JSONObject();
            stopCmd.put("command", "stop");
            sendCommand(stopCmd);
        } catch (Exception ignored) {}

        if (udpClient != null) {
            udpClient.close();
            udpClient = null;
        }
        if (tcpControlClient != null) {
            tcpControlClient.stop();
            tcpControlClient = null;
        }
    }

    public void sendCommand(JSONObject cmd) {
        if (tcpControlClient != null) {
            tcpControlClient.send(cmd.toString());
        }
    }

    public long getClockOffset() {
        return udpClient != null ? udpClient.getClockOffset() : 0;
    }

    public boolean isConnected() {
        return udpClient != null && udpClient.isConnected();
    }

    public boolean hasResponse() {
        return udpClient != null && udpClient.hasReceivedAnyPacket();
    }
}
