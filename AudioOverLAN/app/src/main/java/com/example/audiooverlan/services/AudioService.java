package com.example.audiooverlan.services;

import android.app.Service;
import android.content.Intent;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.util.Log;

import androidx.annotation.Nullable;
import android.support.v4.media.session.MediaSessionCompat;
import android.support.v4.media.session.PlaybackStateCompat;

import com.example.audiooverlan.audio.AAudioPlayer;
import com.example.audiooverlan.audio.AudioConfig;
import com.example.audiooverlan.audio.JitterBuffer;
import com.example.audiooverlan.audio.PlayerAudioPipeline;
import com.example.audiooverlan.network.PlayerConnectionManager;
import com.example.audiooverlan.utils.SettingsRepository;
import com.example.audiooverlan.viewmodels.PlayerState;
import com.example.audiooverlan.viewmodels.PlayerStateRepository;

import org.json.JSONObject;

public class AudioService extends Service {
    private static final String TAG = "AudioService";

    private static final int SAMPLE_RATE = 48000;
    private static final int AUDIO_FORMAT = android.media.AudioFormat.ENCODING_PCM_16BIT;

    public static final String ACTION_STOP = "com.example.audiooverlan.ACTION_STOP";

    private static AudioService instance;
    public static volatile boolean isServiceRunning = false;
    public static volatile String connectedIp = null;
    public static volatile String connectedDeviceName = null;

    private PlayerAudioPipeline pipeline;
    private PlayerConnectionManager connectionManager;
    private MediaSessionCompat mediaSession;

    // SOLID Managers
    private AudioNotificationManager notificationManager;
    private AudioFocusHandler focusHandler;
    private AudioDeviceMonitor deviceMonitor;
    private WakeLockManager wakeLockManager;

    private boolean useAAudio = false;
    private boolean isExclusiveMode = false;
    private String currentIpAddress = "";

    public interface OnMixerListener {
        void onMixerSync(String sessionsJson);
        void onMixerUpdate(String sessionJson);
        void onMixerIconRes(long pid, String icon);
        default void onMixerConnected() {}
    }
    private static volatile OnMixerListener mixerListener;
    public static void setMixerListener(OnMixerListener listener) { mixerListener = listener; }

    @Override
    public void onCreate() {
        super.onCreate();
        instance = this;

        // Initialize Managers
        notificationManager = new AudioNotificationManager(this);
        focusHandler = new AudioFocusHandler(this);
        deviceMonitor = new AudioDeviceMonitor(this);
        wakeLockManager = new WakeLockManager(this);

        wakeLockManager.acquireLocks();
        setupMediaSession();
        deviceMonitor.register();
    }

    private void setupMediaSession() {
        mediaSession = new MediaSessionCompat(this, "AudioServiceSession");
        PlaybackStateCompat.Builder stateBuilder = new PlaybackStateCompat.Builder()
                .setActions(PlaybackStateCompat.ACTION_STOP)
                .setState(PlaybackStateCompat.STATE_PLAYING, PlaybackStateCompat.PLAYBACK_POSITION_UNKNOWN, 1.0f);
        mediaSession.setPlaybackState(stateBuilder.build());

        mediaSession.setCallback(new MediaSessionCompat.Callback() {
            @Override
            public void onPause() { super.onPause(); stopSelf(); }
            @Override
            public void onStop() { 
                super.onStop();
                PlayerStateRepository.getInstance().updateState(PlayerState.Disconnected.INSTANCE);
                stopSelf(); 
            }
        });
        mediaSession.setActive(true);
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent != null && ACTION_STOP.equals(intent.getAction())) {
            PlayerStateRepository.getInstance().updateState(PlayerState.Disconnected.INSTANCE);
            stopSelf();
            return START_NOT_STICKY;
        }

        if (intent != null && intent.hasExtra("IP_ADDRESS")) {
            String newIp = intent.getStringExtra("IP_ADDRESS");
            int port = intent.getIntExtra("PORT", 5000);
            Log.d(TAG, "onStartCommand: Connecting to " + newIp + ":" + port);

            cleanupExistingModules();

            SettingsRepository repo = SettingsRepository.getInstance(this);
            currentIpAddress = newIp;
            connectedIp = currentIpAddress;

            PlayerStateRepository.getInstance().updateState(new PlayerState.Connecting(currentIpAddress));

            isExclusiveMode = repo.isExclusiveAudio();
            useAAudio = repo.isAaudioEnabled();
            
            focusHandler.setExclusiveMode(isExclusiveMode);

            startModules(currentIpAddress, port);
            
            startForeground(AudioNotificationManager.NOTIFICATION_ID, 
                notificationManager.createNotification("Connecting to " + currentIpAddress + "...", mediaSession));
            isServiceRunning = true;
        }
        return START_NOT_STICKY;
    }

    private void cleanupExistingModules() {
        if (pipeline != null) { 
            Log.w(TAG, "Cleaning up existing pipeline before restart");
            pipeline.stop(); 
            pipeline = null; 
        }
        if (connectionManager != null) { 
            Log.w(TAG, "Cleaning up existing connectionManager before restart");
            connectionManager.stop(); 
            connectionManager = null; 
        }
    }

    private void startModules(String ip, int port) {
        SettingsRepository repo = SettingsRepository.getInstance(this);
        
        int modeIdx = repo.getBufferMode();
        JitterBuffer.BufferMode mode = JitterBuffer.BufferMode.values()[Math.min(Math.max(modeIdx, 0), JitterBuffer.BufferMode.values().length - 1)];
        int minMs = repo.getBufferCustomMinMs();
        int maxMs = repo.getBufferCustomMaxMs();

        AudioConfig config = new AudioConfig.Builder()
                .sampleRate(SAMPLE_RATE)
                .channels(2)
                .audioFormat(AUDIO_FORMAT)
                .exclusiveMode(isExclusiveMode)
                .useAAudio(useAAudio)
                .bufferConfig(mode, minMs, maxMs)
                .build();

        pipeline = new PlayerAudioPipeline(config, ip);
        focusHandler.setPipeline(pipeline);
        deviceMonitor.setPipeline(pipeline);

        connectionManager = new PlayerConnectionManager(ip, port, pipeline.getJitterBuffer(), new PlayerConnectionManager.ConnectionListener() {
            @Override
            public void onControlMessage(String message) {
                handleControlMessage(message);
            }

            @Override
            public void onDeviceNameReceived(String name) {
                connectedDeviceName = name;
                new Handler(Looper.getMainLooper()).post(() -> {
                    if (isServiceRunning) {
                        String content = connectedDeviceName != null ? "Connected to " + connectedDeviceName : "Connected to " + currentIpAddress;
                        notificationManager.updateNotification(content, mediaSession);
                    }
                });
            }

            @Override
            public void onConnected() {
                PlayerStateRepository.getInstance().updateState(new PlayerState.Playing(ip, 0,0, 0.0f,0, 0, 0, 0, 0, 0, 0, "Opus", 0, 0, 0));
                if (mixerListener != null) mixerListener.onMixerConnected();
            }

            @Override
            public void onDisconnected() {
                if (isServiceRunning) {
                    PlayerStateRepository.getInstance().updateState(PlayerState.Reconnecting.INSTANCE);
                }
            }
        });
        
        pipeline.setClockOffsetProvider(() -> connectionManager != null ? connectionManager.getClockOffset() : 0L);
        pipeline.start();
        connectionManager.start();
    }

    private void handleControlMessage(String message) {
        try {
            JSONObject json = new JSONObject(message);
            String cmd = json.optString("command");
            if ("mixer_sync".equals(cmd)) {
                if (mixerListener != null) mixerListener.onMixerSync(json.optString("sessions", "[]"));
            } else if ("mixer_update".equals(cmd)) {
                if (mixerListener != null) mixerListener.onMixerUpdate(json.optString("session", "{}"));
            } else if ("mixer_icon_res".equals(cmd)) {
                if (mixerListener != null) mixerListener.onMixerIconRes(json.optLong("pid"), json.optString("icon"));
            } else if ("stop".equals(cmd)) {
                Log.i(TAG, "Server requested stop (Broadcasting shutdown). Stopping service...");
                stopSelf();
            }
        } catch (Exception e) {}
    }

    public static void sendServerCommand(JSONObject cmd) {
        if (instance != null && instance.connectionManager != null) {
            instance.connectionManager.sendCommand(cmd);
        }
    }

    @Override
    public void onDestroy() {
        Log.i(TAG, "AudioService.onDestroy() called");
        isServiceRunning = false;
        instance = null;
        
        if (pipeline != null) pipeline.stop();

        final PlayerConnectionManager connToStop = connectionManager;
        connectionManager = null;

        new Thread(() -> {
            if (connToStop != null) connToStop.stop();
            if (wakeLockManager != null) wakeLockManager.releaseLocks();
            Log.i(TAG, "AudioService background cleanup finished");
        }).start();

        if (mediaSession != null) {
            mediaSession.setActive(false);
            mediaSession.release();
        }

        deviceMonitor.unregister();
        focusHandler.abandonFocus();
        
        super.onDestroy();
        PlayerStateRepository.getInstance().updateState(PlayerState.Disconnected.INSTANCE);
    }

    @Nullable
    @Override
    public IBinder onBind(Intent intent) { return null; }

    // Facade Methods for legacy UI support
    public static long getAvgLatency() { return instance != null && instance.pipeline != null ? (long)instance.pipeline.getAvgLatency() : 0; }
    public static long getMaxLatency() { return instance != null && instance.pipeline != null ? instance.pipeline.getMaxLatency() : 0; }
    public static short[] getLatestSamples() { return instance != null && instance.pipeline != null ? instance.pipeline.getLatestSamples() : null; }
    public static String getAudioEngineName() { return instance != null && instance.pipeline != null ? "Opus" : "None"; }
    public static void fillJitterBufferStats(JitterBuffer.Statistics out) { if (instance != null && instance.pipeline != null) instance.pipeline.fillStatistics(out); }
    public static void setBufferingConfig(JitterBuffer.BufferMode mode, int minMs, int maxMs) {
        if (instance != null && instance.pipeline != null) instance.pipeline.setBufferingConfig(mode, minMs, maxMs);
    }
    public static void setBufferMode(boolean adaptive, int fixedMs) {
        if (instance != null && instance.pipeline != null) {
            instance.pipeline.setBufferingConfig(adaptive ? JitterBuffer.BufferMode.MEDIUM : JitterBuffer.BufferMode.CUSTOM, fixedMs, fixedMs + 40);
        }
    }
    public static void setAAudioMode(boolean e) { /* Placeholder */ }
    
    public static void setExclusiveAudioMode(boolean e) { 
        if (instance != null) {
            instance.isExclusiveMode = e;
            instance.focusHandler.setExclusiveMode(e);
            if (instance.pipeline != null) {
                instance.pipeline.setExclusiveMode(e);
            }
        }
    }
    public static boolean isConnectedToProcessor() { return instance != null && instance.connectionManager != null && instance.connectionManager.isConnected(); }
    public static boolean hasServerResponded() { return instance != null && instance.connectionManager != null && instance.connectionManager.hasResponse(); }
    public static boolean isAAudioAvailable() { return AAudioPlayer.isAvailable(); }
}
