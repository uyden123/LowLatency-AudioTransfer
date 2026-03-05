package com.example.audiooverlan.network;

import android.util.Log;
import org.json.JSONObject;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.charset.StandardCharsets;

/**
 * TCP Client to send control JSON commands to the server.
 */
public class TcpControlClient {
    private static final String TAG = "TcpControlClient";
    private static final int TIMEOUT_MS = 3000;

    public static void sendCommand(String ip, int port, JSONObject command) {
        new Thread(() -> {
            try (Socket socket = new Socket()) {
                socket.connect(new InetSocketAddress(ip, port), TIMEOUT_MS);
                OutputStream out = socket.getOutputStream();
                String jsonStr = command.toString() + "\n";
                out.write(jsonStr.getBytes(StandardCharsets.UTF_8));
                out.flush();
                Log.d(TAG, "Sent TCP command: " + jsonStr + " to " + ip + ":" + port);
            } catch (Exception e) {
                Log.e(TAG, "Failed to send TCP command to " + ip + ":" + port, e);
            }
        }).start();
    }
}
