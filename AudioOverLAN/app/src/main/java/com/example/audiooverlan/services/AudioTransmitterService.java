package com.example.audiooverlan.services;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.ServiceInfo;
import android.media.AudioDeviceCallback;
import android.media.AudioDeviceInfo;
import android.media.AudioManager;
import android.media.AudioFormat;
import android.media.MediaRecorder;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.PowerManager;
import android.util.Log;

import androidx.annotation.Nullable;
import androidx.core.app.NotificationCompat;

import com.example.audiooverlan.audio.AAudioSourceStrategy;
import com.example.audiooverlan.audio.AudioCapturePipeline;
import com.example.audiooverlan.audio.AudioConfig;
import com.example.audiooverlan.audio.AudioRecordSourceStrategy;
import com.example.audiooverlan.audio.AudioSourceStrategy;
import com.example.audiooverlan.audio.DeepFilterStrategy;
import com.example.audiooverlan.audio.NoOpStrategy;
import com.example.audiooverlan.audio.NoiseSuppressionStrategy;
import com.example.audiooverlan.audio.RNNoiseStrategy;
import com.example.audiooverlan.network.TransmitterConnectionManager;
import com.example.audiooverlan.utils.SettingsRepository;
import com.example.audiooverlan.viewmodels.TransmitterState;
import com.example.audiooverlan.viewmodels.TransmitterStateRepository;
import com.example.audiooverlan.utils.SettingsRepository;

public class AudioTransmitterService extends Service {
    private static final String TAG = "TransmitterService";
    private static final String CHANNEL_ID = "TransmitterChannel";
    private static final int NOTIFICATION_ID = 2;

    private static final int SAMPLE_RATE = 48000;
    private static final int CHANNELS = 1;

    private AudioCapturePipeline pipeline;
    private TransmitterConnectionManager connectionManager;
    private PowerManager.WakeLock wakeLock;
    private android.net.wifi.WifiManager.WifiLock wifiLock;
    private NotificationManager notificationManager;

    public static volatile boolean isServiceRunning = false;
    private static AudioTransmitterService instance;
    private String targetIp;
    private String sourceMode = "Microphone";
    private Intent projectionData;
    private long startTimeMillis;
    private boolean isInstanceRunning = true;
    private final Object restartLock = new Object();
    private volatile boolean isRestarting = false;

    public static String getSourceMode() {
        if (instance != null) return instance.sourceMode;
        return "Microphone";
    }

    // Static fields for TransmittingFragment (Backward Compatibility)
    public static boolean useAAudio = true;
    public static boolean isExclusiveMode = true;
    public static int micSource = MediaRecorder.AudioSource.VOICE_COMMUNICATION;
    public static int appNsMode = 0;
    public static float appNoiseSuppressionLevel = 50.0f;
    public static boolean appVolumeBoostEnabled = false;
    public static float appVolumeBoostLevel = 2.0f;
    public static float volumeGain = 1.0f;
    private int preferredDeviceId = 0; // 0 = default
    private boolean isWaitingForSco = false;
    private AudioDeviceCallback audioDeviceCallback;

    private final BroadcastReceiver scoReceiver = new BroadcastReceiver() {
        @Override
        public void onReceive(Context context, Intent intent) {
            int state = intent.getIntExtra(AudioManager.EXTRA_SCO_AUDIO_STATE, -1);
            Log.d(TAG, "Bluetooth SCO state changed: " + state);
            if (state == AudioManager.SCO_AUDIO_STATE_CONNECTED && isWaitingForSco) {
                Log.i(TAG, "Bluetooth SCO connected, restarting pipeline now.");
                isWaitingForSco = false;
                requestAudioSourceRestart();
            } else if (state == AudioManager.SCO_AUDIO_STATE_DISCONNECTED && isWaitingForSco) {
                 Log.w(TAG, "Bluetooth SCO failed to connect.");
                 isWaitingForSco = false;
            }
        }
    };

    public static AudioTransmitterService getInstance() { return instance; }

    public boolean isConnected() {
        return connectionManager != null && connectionManager.isConnected();
    }

    @Override
    public void onCreate() {
        super.onCreate();
        instance = this;
        notificationManager = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        createNotificationChannel();
        setupAudioDeviceCallback();
        registerReceiver(scoReceiver, new IntentFilter(AudioManager.ACTION_SCO_AUDIO_STATE_UPDATED));
    }

    private void setupAudioDeviceCallback() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            AudioManager audioManager = (AudioManager) getSystemService(AUDIO_SERVICE);
            audioDeviceCallback = new AudioDeviceCallback() {
                @Override
                public void onAudioDevicesAdded(AudioDeviceInfo[] addedDevices) {
                    handleDeviceChange();
                }

                @Override
                public void onAudioDevicesRemoved(AudioDeviceInfo[] removedDevices) {
                    handleDeviceChange();
                }

                private void handleDeviceChange() {
                    if (pipeline != null) {
                        Log.i(TAG, "Audio device change detected, notifying UI...");
                        // requestAudioSourceRestart(); // REVERT: No more auto-switch
                        refreshState();
                    }
                }
            };
            audioManager.registerAudioDeviceCallback(audioDeviceCallback, null);
        }
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null) return START_NOT_STICKY;

        targetIp = intent.getStringExtra("IP_ADDRESS");
        sourceMode = intent.getStringExtra("SOURCE");
        if (sourceMode == null) sourceMode = "Microphone";
        projectionData = intent.getParcelableExtra("PROJECTION_DATA");
        int port = intent.getIntExtra("PORT", 5003);

        loadLegacyStaticFields();
        startForegroundService();
        startModules(targetIp, port);

        isServiceRunning = true;
        startTimeMillis = System.currentTimeMillis();
        return START_NOT_STICKY;
    }

    private void loadLegacyStaticFields() {
        SettingsRepository repo = SettingsRepository.getInstance(this);
        useAAudio = repo.isTransAaudioEnabled();
        isExclusiveMode = repo.isTransExclusiveMode();
        micSource = repo.getTransMicSource();
        appNsMode = repo.getTransNsMode();
        appNoiseSuppressionLevel = repo.getTransNsLevel();
        appVolumeBoostEnabled = repo.isTransVolumeBoost();
        appVolumeBoostLevel = repo.getTransVolumeBoostLevel();
        volumeGain = appVolumeBoostEnabled ? appVolumeBoostLevel : 1.0f;
    }

    private void startForegroundService() {
        String text = (targetIp != null && !targetIp.isEmpty()) ? "Sending to " + targetIp : "Waiting for PC...";
        Notification notification = createNotification(text);

        int type = 0;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            if ("Apps".equals(sourceMode)) {
                type = ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION;
            } else {
                type = ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE;
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
                    type |= ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE;
                }
            }
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(NOTIFICATION_ID, notification, type);
        } else {
            startForeground(NOTIFICATION_ID, notification);
        }

        PowerManager pm = (PowerManager) getSystemService(POWER_SERVICE);
        wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "AudioOverLAN:TransmitterWakeLock");
        wakeLock.acquire(2 * 60 * 60 * 1000L);

        android.net.wifi.WifiManager wm = (android.net.wifi.WifiManager) getApplicationContext().getSystemService(WIFI_SERVICE);
        wifiLock = wm.createWifiLock(android.net.wifi.WifiManager.WIFI_MODE_FULL_HIGH_PERF, "AudioOverLAN:TransmitterWifiLock");
        wifiLock.acquire();
    }

    private void startModules(String ip, int port) {
        AudioConfig config = new AudioConfig.Builder()
                .sampleRate(SAMPLE_RATE)
                .channels(CHANNELS)
                .audioFormat(AudioFormat.ENCODING_PCM_16BIT)
                .exclusiveMode(isExclusiveMode)
                .useAAudio(useAAudio)
                .deviceId(preferredDeviceId)
                .build();

        pipeline = new AudioCapturePipeline(config, new AudioCapturePipeline.CaptureListener() {
            @Override
            public void onFrameCaptured(byte[] opusData, int length) {
                if (connectionManager != null) connectionManager.sendPacket(opusData, length);
            }

            @Override
            public void onError(String message) {
                Log.e(TAG, "Pipeline error: " + message);
                stopSelf();
            }
        });

        updateStrategies(config);
        pipeline.start();

        connectionManager = new TransmitterConnectionManager(this, ip, port, SAMPLE_RATE, 20, 
            new TransmitterConnectionManager.ConnectionListener() {
                private final TransmitterConnectionManager selfConn = connectionManager;
                
                @Override
                public void onServerConnected() { 
                    if (connectionManager != selfConn) return; // Ignore old callbacks
                    updateNotification();
                    refreshState();
                }
                @Override
                public void onServerDisconnected() { 
                    if (connectionManager != selfConn) return; // Ignore old callbacks
                    updateNotification();
                    refreshState();
                }
            });
        connectionManager.start();
        
        TransmitterStateRepository.getInstance().updateState(TransmitterState.Starting.INSTANCE);
        startStatsThread();
    }

    private void startStatsThread() {
        new Thread(() -> {
            while (isInstanceRunning) {
                try {
                    Thread.sleep(1000);
                    refreshState();
                } catch (InterruptedException e) { break; }
            }
        }, "TransmitterStatsThread").start();
    }

    private void refreshState() {
        if (connectionManager == null) return;
        
        long packets = connectionManager.getPacketsSent();
        long bytes = connectionManager.getBytesSent();
        double bitrate = (bytes * 8.0) / ((System.currentTimeMillis() - startTimeMillis) / 1000.0 + 1.0) / 1000.0;
        
        boolean btAvailable = false;
        String sourceName = sourceMode.equals("Apps") ? "Apps Sound" : "Built-in Mic";
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            AudioManager am = (AudioManager) getSystemService(AUDIO_SERVICE);
            AudioDeviceInfo[] devices = am.getDevices(AudioManager.GET_DEVICES_INPUTS);
            for (AudioDeviceInfo d : devices) {
                if (d.getType() == AudioDeviceInfo.TYPE_BLUETOOTH_SCO || 
                    d.getType() == AudioDeviceInfo.TYPE_BLUETOOTH_A2DP) {
                    btAvailable = true;
                    if (pipeline != null && pipeline.isStarted() && d.isSource()) {
                        // This is a rough check, but helps UI
                    }
                }
            }
        }

        TransmitterStateRepository.getInstance().updateState(new TransmitterState.Transmitting(
            targetIp != null ? targetIp : "PC",
            packets,
            bitrate,
            System.currentTimeMillis() - startTimeMillis,
            connectionManager.isConnected(),
            btAvailable,
            sourceName,
            connectionManager.getActiveClients()
        ));
    }

    private void updateStrategies(AudioConfig config) {
        AudioSourceStrategy source;
        if (sourceMode.equals("Apps") && projectionData != null) {
            source = new com.example.audiooverlan.audio.AudioPlaybackCaptureSourceStrategy(this, config, projectionData);
        } else if (useAAudio && com.example.audiooverlan.audio.AAudioRecorder.isAvailable()) {
            source = new AAudioSourceStrategy(config);
        } else {
            source = new AudioRecordSourceStrategy(this, config, micSource, SAMPLE_RATE * 20 / 1000);
        }
        source.start();

        NoiseSuppressionStrategy ns;
        if (appNsMode == 1) ns = new RNNoiseStrategy();
        else if (appNsMode == 2) ns = new DeepFilterStrategy(this, appNoiseSuppressionLevel);
        else ns = new NoOpStrategy();

        pipeline.setStrategies(source, ns);
        pipeline.setVolumeGain(volumeGain);
    }

    public void requestAudioSourceRestart() {
        if (pipeline != null) {
            new Thread(() -> {
                synchronized (restartLock) {
                    if (isRestarting) return;
                    isRestarting = true;
                }
                try {
                    pipeline.stop();
                    // Small delay to ensure hardware is released
                    try { Thread.sleep(50); } catch (InterruptedException ignored) {}
                    
                    AudioConfig config = new AudioConfig.Builder()
                            .sampleRate(SAMPLE_RATE)
                            .channels(CHANNELS)
                            .audioFormat(AudioFormat.ENCODING_PCM_16BIT)
                            .exclusiveMode(isExclusiveMode)
                            .useAAudio(useAAudio)
                            .deviceId(preferredDeviceId)
                            .build();
                    updateStrategies(config);
                    pipeline.start();
                } finally {
                    synchronized (restartLock) {
                        isRestarting = false;
                    }
                }
            }, "PipelineRestartThread").start();
        }
    }

    public void updateNoiseSuppressionLevel(float level) {
        appNoiseSuppressionLevel = level;
        if (pipeline != null) {
            pipeline.updateNoiseSuppressionLevel(level);
        }
    }

    public void updateVolumeGain(float gain) {
        volumeGain = gain;
        if (pipeline != null) {
            pipeline.setVolumeGain(gain);
        }
    }

    public void setPreferredDeviceId(int id) {
        this.preferredDeviceId = id;
        
        AudioManager am = (AudioManager) getSystemService(AUDIO_SERVICE);
        if (id != 0) {
            // Check if selected is Bluetooth
            boolean isBt = false;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                AudioDeviceInfo[] devices = am.getDevices(AudioManager.GET_DEVICES_INPUTS);
                for (AudioDeviceInfo d : devices) {
                    if (d.getId() == id && d.getType() == AudioDeviceInfo.TYPE_BLUETOOTH_SCO) {
                        isBt = true;
                        break;
                    }
                }
            }

            if (isBt) {
                Log.i(TAG, "Activating Bluetooth SCO for mic...");
                am.setMode(AudioManager.MODE_IN_COMMUNICATION);
                am.startBluetoothSco();
                am.setBluetoothScoOn(true);
                isWaitingForSco = true;
                // Pipeline will restart when SCO connects (via scoReceiver)
                return; 
            }
        } else {
            // Switch to built-in mic
            if (am.isBluetoothScoOn()) {
                Log.i(TAG, "Deactivating Bluetooth SCO...");
                am.setBluetoothScoOn(false);
                am.stopBluetoothSco();
                am.setMode(AudioManager.MODE_NORMAL);
            }
        }
        
        requestAudioSourceRestart();
    }

    private void updateNotification() {
        boolean connected = connectionManager != null && connectionManager.isConnected();
        String content = connected ? "Mic Streaming - Connected" : "Waiting for PC...";
        notificationManager.notify(NOTIFICATION_ID, createNotification(content));
    }

    private Notification createNotification(String content) {
        return new NotificationCompat.Builder(this, CHANNEL_ID)
                .setContentTitle("Audio Server Active")
                .setContentText(content)
                .setSmallIcon(android.R.drawable.ic_btn_speak_now)
                .setPriority(NotificationCompat.PRIORITY_HIGH)
                .setOngoing(true)
                .build();
    }

    private void createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationChannel channel = new NotificationChannel(CHANNEL_ID, "Transmitter Service", NotificationManager.IMPORTANCE_LOW);
            notificationManager.createNotificationChannel(channel);
        }
    }

    @Override
    public void onDestroy() {
        Log.i(TAG, "TransmitterService.onDestroy() called");
        isInstanceRunning = false;
        isServiceRunning = false;
        instance = null;
        startTimeMillis = 0;

        if (pipeline != null) {
            pipeline.stop();
        }

        final TransmitterConnectionManager connToStop = connectionManager;
        connectionManager = null;

        // Run network stop in a thread to avoid NetworkOnMainThreadException
        new Thread(() -> {
            if (connToStop != null) {
                connToStop.stop();
            }
            if (wakeLock != null && wakeLock.isHeld()) wakeLock.release();
            if (wifiLock != null && wifiLock.isHeld()) wifiLock.release();
            Log.i(TAG, "TransmitterService background cleanup finished");
        }).start();

        super.onDestroy();
        try {
            unregisterReceiver(scoReceiver);
        } catch (Exception ignored) {}
        
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M && audioDeviceCallback != null) {
            AudioManager am = (AudioManager) getSystemService(AUDIO_SERVICE);
            if (am != null) am.unregisterAudioDeviceCallback(audioDeviceCallback);
        }
        
        TransmitterStateRepository.getInstance().updateState(TransmitterState.Idle.INSTANCE);
    }

    @Nullable
    @Override
    public IBinder onBind(Intent intent) { return null; }

    public static void updateDeepFilterLevel(float level) { /* Placeholder */ }
}
