package com.example.audiooverlan.services;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioManager;
import android.media.AudioTrack;
import android.os.Build;
import android.os.IBinder;
import android.os.PowerManager;
import android.util.Log;

import androidx.annotation.Nullable;
import androidx.core.app.NotificationCompat;

import com.example.audiooverlan.UI.MainActivity;
import com.example.audiooverlan.audio.AAudioPlayer;
import com.example.audiooverlan.audio.OpusCodec;
import com.example.audiooverlan.network.UdpRealtimeClient;
import com.example.audiooverlan.audio.JitterBuffer;

import com.example.audiooverlan.network.TcpControlClient;
import org.json.JSONObject;

import java.util.Locale;

/**
 * Simple Audio streaming service.
 * Only handles jitter buffering and playback.
 */
public class AudioService extends Service {

    private static final String TAG = "AudioService";
    private static final String CHANNEL_ID = "AudioServiceChannel";
    private static final int NOTIFICATION_ID = 1;

    // Audio configuration
    private static final int SAMPLE_RATE = 48000;
    private static final int CHANNEL_CONFIG = AudioFormat.CHANNEL_OUT_STEREO;
    private static final int AUDIO_FORMAT = AudioFormat.ENCODING_PCM_16BIT;
    private static final int PACKET_DURATION_MS = 20;
    public static final String KEY_DRC_ENABLED = "drc_enabled";
    public static final String KEY_AAUDIO_ENABLED = "aaudio_enabled";

    // Components
    private static AudioService instance;
    private UdpRealtimeClient udpClient;
    private AudioTrack audioTrack;
    private AAudioPlayer aaudioPlayer;
    private boolean useAAudio = false;
    private OpusCodec opusCodec;
    private JitterBuffer jitterBuffer;

    // State
    public static volatile boolean isServiceRunning = false;
    public static volatile String connectedIp = null;
    private volatile boolean isPlaying = false;
    private boolean isExclusiveMode = false;
    private AudioManager audioManager;
    private AudioManager.OnAudioFocusChangeListener focusChangeListener;
    private android.os.PowerManager.WakeLock wakeLock;
    private android.net.wifi.WifiManager.WifiLock wifiLock;
    
    // Stats for UI
    private static volatile long currentLatencyVal = 0;
    private static volatile long maxLatencyVal = 0;
    private static volatile double avgLatencyVal = 0;
    private static volatile long packetCount = 0;
    private static volatile long totalLatencySum = 0;

    private long initialClockOffset = Long.MAX_VALUE; // Difference between Server and Client clock
    private Thread playbackThread;
    private Thread statsThread;
    private NotificationManager notificationManager;
    private String currentIpAddress = "";
    
    // Visualization
    private volatile short[] latestSamples;

    // Clock drift
    private long baseMinDelay = -1;
    private double currentRatio = 1.0;

    public static long getCurrentLatency() { return currentLatencyVal; }
    public static long getMaxLatency() { return maxLatencyVal; }
    public static double getAvgLatency() { return avgLatencyVal; }

    public static JitterBuffer.Statistics getJitterBufferStats() {
        if (instance != null && instance.jitterBuffer != null) {
            return instance.jitterBuffer.getStatistics();
        }
        return null;
    }

    public boolean isServerConnected() {
        return udpClient != null && udpClient.isConnected();
    }

    public static boolean isConnectedToProcessor() {
        return instance != null && instance.isServerConnected();
    }

    public static boolean hasServerResponded() {
        return instance != null && instance.udpClient != null && instance.udpClient.hasReceivedAnyPacket();
    }



    public static short[] getLatestSamples() {
        if (instance != null) {
            return instance.latestSamples;
        }
        return null;
    }

    public static String getAudioEngineName() {
        if (instance != null) {
            if (instance.useAAudio && instance.aaudioPlayer != null && instance.aaudioPlayer.isStarted()) {
                return "AAudio (Oboe)";
            }
            return "AudioTrack";
        }
        return "None";
    }

    public static void setBufferMode(boolean adaptive, int fixedPackets) {
        if (instance != null && instance.jitterBuffer != null) {
            instance.jitterBuffer.setBufferingMode(adaptive, fixedPackets);
        }
    }

    public static void setAAudioMode(boolean enabled) {
        if (instance != null) {
            instance.switchAudioEngine(enabled);
        }
    }

    private synchronized void switchAudioEngine(boolean enabled) {
        // Removed early return to allow forced restart for setting changes
        
        Log.i(TAG, "Switching audio engine to: " + (enabled ? "AAudio" : "AudioTrack"));
        
        // Stop current
        if (aaudioPlayer != null) {
            aaudioPlayer.stop();
            aaudioPlayer = null;
        }
        if (audioTrack != null) {
            try {
                audioTrack.stop();
                audioTrack.release();
            } catch (Exception ignored) {}
            audioTrack = null;
        }

        useAAudio = enabled;
        
        // Re-init
        int channels = (CHANNEL_CONFIG == AudioFormat.CHANNEL_OUT_STEREO) ? 2 : 1;
        if (useAAudio && AAudioPlayer.isAvailable()) {
            if (aaudioPlayer == null) aaudioPlayer = new AAudioPlayer();
            boolean started = aaudioPlayer.start(SAMPLE_RATE, channels, 0, isExclusiveMode);
            if (!started) {
                Log.w(TAG, "AAudio switch failed, falling back to AudioTrack");
                aaudioPlayer = null;
                useAAudio = false;
                initAudioTrack();
                if (isPlaying) audioTrack.play();
            }
        } else {
            useAAudio = false;
            initAudioTrack();
            if (isPlaying) {
                try {
                    audioTrack.play();
                } catch (Exception e) {
                    Log.e(TAG, "Failed to resume AudioTrack after switch", e);
                }
            }
        }
    }

    public static boolean isAAudioAvailable() {
        return AAudioPlayer.isAvailable();
    }

    public static void setExclusiveAudioMode(boolean exclusive) {
        if (instance != null) {
            boolean changed = instance.isExclusiveMode != exclusive;
            instance.isExclusiveMode = exclusive;
            
            if (exclusive && instance.isPlaying) {
                instance.requestAudioFocus();
            } else if (!exclusive) {
                instance.releaseAudioFocus();
            }

            // If using AAudio, we need to restart the engine to apply SharingMode changes
            if (changed && instance.useAAudio && instance.isPlaying) {
                instance.switchAudioEngine(true);
            }
        }
    }

    private void requestAudioFocus() {
        if (audioManager != null) {
            audioManager.requestAudioFocus(focusChangeListener, AudioManager.STREAM_MUSIC, AudioManager.AUDIOFOCUS_GAIN);
        }
    }

    private void releaseAudioFocus() {
        if (audioManager != null) {
            audioManager.abandonAudioFocus(focusChangeListener);
        }
    }

    public static void sendServerCommand(JSONObject command) {
        if (instance != null && instance.currentIpAddress != null && !instance.currentIpAddress.isEmpty()) {
            TcpControlClient.sendCommand(instance.currentIpAddress, 5000, command);
        }
    }

    @Override
    public void onCreate() {
        super.onCreate();
        instance = this;
        isServiceRunning = true;
        notificationManager = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        audioManager = (AudioManager) getSystemService(AUDIO_SERVICE);
        initAudioFocus();
        createNotificationChannel();
        resetStats();
    }

    private void initAudioFocus() {
        focusChangeListener = focusChange -> {
            if (isExclusiveMode) {
                switch (focusChange) {
                    case AudioManager.AUDIOFOCUS_LOSS:
                    case AudioManager.AUDIOFOCUS_LOSS_TRANSIENT:
                        pausePlayback();
                        break;
                    case AudioManager.AUDIOFOCUS_GAIN:
                        resumePlayback();
                        break;
                }
            }
        };
    }

    private void pausePlayback() {
        if (useAAudio) {
            // AAudio doesn't support pause, just log
            Log.d(TAG, "Playback pause requested (AAudio mode - no-op)");
        } else if (audioTrack != null && audioTrack.getPlayState() == AudioTrack.PLAYSTATE_PLAYING) {
            audioTrack.pause();
            Log.d(TAG, "Playback paused due to focus loss");
        }
    }

    private void resumePlayback() {
        if (useAAudio) {
            Log.d(TAG, "Playback resume requested (AAudio mode - no-op)");
        } else if (audioTrack != null && audioTrack.getPlayState() == AudioTrack.PLAYSTATE_PAUSED && isPlaying) {
            audioTrack.play();
            Log.d(TAG, "Playback resumed due to focus gain");
        }
    }

    private void resetStats() {
        currentLatencyVal = 0;
        maxLatencyVal = 0;
        avgLatencyVal = 0;
        packetCount = 0;
        totalLatencySum = 0;
        if (jitterBuffer != null) {
            jitterBuffer.resetStats();
        }
    }

    private void createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationChannel serviceChannel = new NotificationChannel(
                    CHANNEL_ID, "Audio Streaming Service", NotificationManager.IMPORTANCE_HIGH); // Changed to HIGH
            serviceChannel.setLockscreenVisibility(Notification.VISIBILITY_PUBLIC);
            notificationManager.createNotificationChannel(serviceChannel);
        }
    }

    private Notification createNotification(String content) {
        Intent notificationIntent = new Intent(this, MainActivity.class);
        android.app.PendingIntent pendingIntent = android.app.PendingIntent.getActivity(this,
                0, notificationIntent, android.app.PendingIntent.FLAG_IMMUTABLE);

        return new NotificationCompat.Builder(this, CHANNEL_ID)
                .setContentTitle("AudioOverLAN - " + currentIpAddress)
                .setContentText(content)
                .setSmallIcon(android.R.drawable.ic_media_play)
                .setContentIntent(pendingIntent)
                .setPriority(NotificationCompat.PRIORITY_HIGH)
                .setCategory(NotificationCompat.CATEGORY_SERVICE)
                .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
                .setOngoing(true)
                .build();
    }

    private void initAudio() {
        int channels = (CHANNEL_CONFIG == AudioFormat.CHANNEL_OUT_STEREO) ? 2 : 1;

        if (useAAudio && AAudioPlayer.isAvailable()) {
            // Use native AAudio path for lowest latency
            aaudioPlayer = new AAudioPlayer();
            boolean started = aaudioPlayer.start(SAMPLE_RATE, channels, 0, isExclusiveMode);
            if (!started) {
                Log.w(TAG, "AAudio failed to start, falling back to AudioTrack");
                aaudioPlayer = null;
                useAAudio = false;
                initAudioTrack();
            } else {
                Log.i(TAG, "Using AAudio backend: " + aaudioPlayer.getStreamInfo());
            }
        } else {
            if (useAAudio) {
                Log.w(TAG, "AAudio not available, falling back to AudioTrack");
                useAAudio = false;
            }
            initAudioTrack();
        }

        int samplesPerPacket = (SAMPLE_RATE * PACKET_DURATION_MS / 1000);
        silenceBuffer = new byte[samplesPerPacket * channels * 2];
        pcmBufferWorkspace = new short[samplesPerPacket * channels];
        
        jitterBuffer = new JitterBuffer();
    }

    private void initAudioTrack() {
        int minBufferSize = AudioTrack.getMinBufferSize(SAMPLE_RATE, CHANNEL_CONFIG, AUDIO_FORMAT);
        int bufferSize = Math.max(minBufferSize * 4, 8192);

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            audioTrack = new AudioTrack.Builder()
                    .setAudioAttributes(new AudioAttributes.Builder()
                            .setUsage(AudioAttributes.USAGE_MEDIA)
                            .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                            .build())
                    .setAudioFormat(new AudioFormat.Builder()
                            .setEncoding(AUDIO_FORMAT)
                            .setSampleRate(SAMPLE_RATE)
                            .setChannelMask(CHANNEL_CONFIG)
                            .build())
                    .setBufferSizeInBytes(bufferSize)
                    .setTransferMode(AudioTrack.MODE_STREAM)
                    .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
                    .build();
        } else {
            audioTrack = new AudioTrack(AudioManager.STREAM_MUSIC,
                    SAMPLE_RATE, CHANNEL_CONFIG, AUDIO_FORMAT,
                    bufferSize, AudioTrack.MODE_STREAM);
        }
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
            if (intent != null) {
            currentIpAddress = intent.getStringExtra("IP_ADDRESS");
            connectedIp = currentIpAddress;
            int port = intent.getIntExtra("PORT", 5000);
            isExclusiveMode = intent.getBooleanExtra("EXCLUSIVE_MODE", false);
            useAAudio = intent.getBooleanExtra("USE_AAUDIO", false);

            initAudio();
            int channels = (CHANNEL_CONFIG == AudioFormat.CHANNEL_OUT_STEREO) ? 2 : 1;
            opusCodec = new OpusCodec(SAMPLE_RATE, channels, PACKET_DURATION_MS);
            if (!opusCodec.initDecoder()) {
                Log.e(TAG, "Failed to init Opus decoder");
                stopSelf();
                return START_NOT_STICKY;
            }

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                startForeground(NOTIFICATION_ID, createNotification("Connecting..."),
                        ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK);
            } else {
                startForeground(NOTIFICATION_ID, createNotification("Connecting..."));
            }

            isPlaying = true;

            // Acquire locks
            PowerManager pm = (PowerManager) getSystemService(POWER_SERVICE);
            if (pm != null) {
                wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "AudioOverLAN:PlaybackWakeLock");
                wakeLock.acquire();
            }
            android.net.wifi.WifiManager wm = (android.net.wifi.WifiManager) getApplicationContext().getSystemService(WIFI_SERVICE);
            if (wm != null) {
                int lockType = Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q ? 
                        android.net.wifi.WifiManager.WIFI_MODE_FULL_HIGH_PERF : android.net.wifi.WifiManager.WIFI_MODE_FULL;
                wifiLock = wm.createWifiLock(lockType, "AudioOverLAN:PlaybackWifiLock");
                wifiLock.acquire();
            }

            if (isExclusiveMode) {
                requestAudioFocus();
            }
            if (!useAAudio && audioTrack != null) {
                audioTrack.play();
            }
            jitterBuffer.start();
            
            startPlayback();
            startStatsUpdate();

            new Thread(() -> {
                try {
                    udpClient = new UdpRealtimeClient(currentIpAddress, port, jitterBuffer);
                    udpClient.setControlMessageListener(message -> {
                        if ("SERVER_SHUTDOWN".equals(message)) {
                            Log.w(TAG, "Server requested shutdown");
                            // Update UI/Notification state
                            updateNotification();
                        }
                    });
                    udpClient.setConnectionListener(new UdpRealtimeClient.OnConnectionStateListener() {
                        @Override
                        public void onConnected() {
                            updateNotification();
                        }

                        @Override
                        public void onDisconnected() {
                            updateNotification();
                        }
                    });
                    udpClient.start();
                    
                } catch (Exception e) {
                    Log.e(TAG, "Failed to start UDP client", e);
                }
            }).start();
        }
        return START_NOT_STICKY;
    }

    private void startStatsUpdate() {
        statsThread = new Thread(() -> {
            while (isPlaying) {
                try {
                    Thread.sleep(1000);
                    updateNotification();
                } catch (InterruptedException e) {
                    break;
                }
            }
        });
        statsThread.start();
    }

    private void updateNotification() {
        boolean connected = isServerConnected();
        String content = connected ? 
                String.format(Locale.getDefault(), "Jitter: %dms", currentLatencyVal) :
                "SERVER DISCONNECTED - Waiting...";

        Notification notification = new NotificationCompat.Builder(this, CHANNEL_ID)
                .setContentTitle(connected ? "AudioOverLAN Active" : "AudioOverLAN [OFFLINE]")
                .setContentText(content)
                .setSubText(currentIpAddress)
                .setSmallIcon(connected ? android.R.drawable.ic_media_play : android.R.drawable.ic_media_pause)
                .setPriority(NotificationCompat.PRIORITY_HIGH)
                .setCategory(NotificationCompat.CATEGORY_SERVICE)
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .build();

        notificationManager.notify(NOTIFICATION_ID, notification);
    }

    private synchronized void writeToOutput(short[] samples, int offset, int length) {
        if (useAAudio && aaudioPlayer != null && aaudioPlayer.isStarted()) {
            aaudioPlayer.write(samples, offset, length);
        } else if (!useAAudio && audioTrack != null && audioTrack.getPlayState() == AudioTrack.PLAYSTATE_PLAYING) {
            audioTrack.write(samples, offset, length, AudioTrack.WRITE_NON_BLOCKING);
        }
    }

    private void startPlayback() {
        playbackThread = new Thread(() -> {
            android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_AUDIO);

            while (isPlaying) {
                JitterBuffer.AudioPacket packet = jitterBuffer.take();

                if (packet == null) {
                    if (jitterBuffer.isBuffering()) {
                        // Wait a tiny bit and poll again
                        try { Thread.sleep(2); } catch (InterruptedException e) { break; }
                        continue;
                    } 
                } else if (packet.isPLC) {
                    // GAP detected -> generate PLC audio
                    short[] pcmSamples = opusCodec.decodePLC();
                    if (pcmSamples != null && pcmSamples.length > 0) {
                        writeToOutput(pcmSamples, 0, pcmSamples.length);
                    }
                    jitterBuffer.recyclePacket(packet);
                } else {
                    // Normal decode
                    short[] pcmSamples = opusCodec.decode(packet.data, 0, packet.length);
                    if (pcmSamples != null && pcmSamples.length > 0) {
                        JitterBuffer.Statistics stats = jitterBuffer.getStatistics();
                        currentLatencyVal = stats.delayMs; // approximate jitter delay
                        
                        // Update global stats
                        packetCount++;
                        totalLatencySum += currentLatencyVal;
                        avgLatencyVal = (double) totalLatencySum / packetCount;
                        if (currentLatencyVal > maxLatencyVal) {
                            maxLatencyVal = currentLatencyVal;
                        }

                        // MinDelay Clock drift monitor
                        if (baseMinDelay == -1 && stats.minDelay > 0) {
                            baseMinDelay = stats.minDelay;
                        } else if (baseMinDelay > 0 && stats.minDelay > 0) {
                            long drift = stats.minDelay - baseMinDelay;
                            
                            // Adaptive resample
                            if (drift > 15) {
                                currentRatio = 1.002; // Receiver faster -> generate more samples
                                baseMinDelay += 5; // adjust base to avoid permanent fast resampling
                            } else if (drift < -15) {
                                currentRatio = 0.998; // Receiver slower -> generate fewer samples
                                baseMinDelay -= 5;
                            } else if (Math.abs(drift) < 5) {
                                currentRatio = 1.0;
                            }
                        }

                        short[] finalSamples = resampleContinuous(pcmSamples, currentRatio);
                        latestSamples = finalSamples; // Capture for UI
                        writeToOutput(finalSamples, 0, finalSamples.length);
                    }
                    jitterBuffer.recyclePacket(packet);
                }
            }
        }, "AudioPlayback");
        playbackThread.start();
    }

    @Override
    public void onDestroy() {
        super.onDestroy();
        instance = null;
        isServiceRunning = false;
        connectedIp = null;
        isPlaying = false;
        if (playbackThread != null) playbackThread.interrupt();
        if (statsThread != null) statsThread.interrupt();
        if (udpClient != null) udpClient.close();
        if (jitterBuffer != null) jitterBuffer.clear();
        releaseAudioFocus();
        if (aaudioPlayer != null) {
            aaudioPlayer.stop();
            aaudioPlayer = null;
        }
        if (audioTrack != null) {
            audioTrack.stop();
            audioTrack.release();
        }
        if (opusCodec != null) opusCodec.release();

        if (wakeLock != null && wakeLock.isHeld()) {
            wakeLock.release();
            wakeLock = null;
        }
        if (wifiLock != null && wifiLock.isHeld()) {
            wifiLock.release();
            wifiLock = null;
        }
    }

    private double currentPhase = 0.0;
    
    private short[] resampleContinuous(short[] in, double ratio) {
        if (Math.abs(ratio - 1.0) < 0.0001) {
            currentPhase = 0.0;
            return in;
        }

        int channels = (CHANNEL_CONFIG == AudioFormat.CHANNEL_OUT_STEREO) ? 2 : 1;
        int numFramesIn = in.length / channels;
        
        int maxFramesOut = (int) Math.ceil((numFramesIn - currentPhase) * ratio) + 2;
        short[] out = new short[maxFramesOut * channels];
        
        int outIdx = 0;
        double step = 1.0 / ratio;
        
        while (currentPhase < numFramesIn) {
            int idx = (int) currentPhase;
            double frac = currentPhase - idx;
            
            for (int c = 0; c < channels; c++) {
                short val0 = in[idx * channels + c];
                short val1;
                if (idx + 1 < numFramesIn) {
                    val1 = in[(idx + 1) * channels + c];
                } else {
                    val1 = val0; // clamp at boundary
                }
                out[outIdx * channels + c] = (short) (val0 + frac * (val1 - val0));
            }
            outIdx++;
            currentPhase += step;
        }
        
        currentPhase -= numFramesIn;
        
        if (outIdx < maxFramesOut) {
            short[] exactOut = new short[outIdx * channels];
            System.arraycopy(out, 0, exactOut, 0, outIdx * channels);
            return exactOut;
        }
        return out;
    }

    @Nullable
    @Override
    public IBinder onBind(Intent intent) { return null; }

    // Silence buffer for underruns/gaps
    private byte[] silenceBuffer;
    private short[] pcmBufferWorkspace;
}
