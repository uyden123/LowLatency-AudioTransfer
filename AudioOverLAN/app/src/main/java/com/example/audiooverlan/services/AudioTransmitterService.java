package com.example.audiooverlan.services;

import android.annotation.SuppressLint;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;
import android.media.audiofx.AcousticEchoCanceler;
import android.media.audiofx.AutomaticGainControl;
import android.media.audiofx.NoiseSuppressor;
import android.os.Build;
import android.os.IBinder;
import android.os.PowerManager;
import android.util.Log;

import androidx.annotation.Nullable;
import androidx.core.app.NotificationCompat;

import com.example.audiooverlan.audio.AAudioRecorder;
import com.example.audiooverlan.audio.OpusCodec;
import com.example.audiooverlan.network.NsdMicAdvertiser;
import com.example.audiooverlan.network.UdpMicSender;
import com.example.audiooverlan.UI.MainActivity;
import com.rikorose.deepfilternet.NativeDeepFilterNet;

import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/**
 * Audio Transmitter Service - records mic audio, encodes with Opus,
 * and sends via UDP to the PC receiver.
 */
public class AudioTransmitterService extends Service {
    private static final String TAG = "TransmitterService";
    private static final String DF_TAG = "DeepFilterMonitor";
    private static final String CHANNEL_ID = "TransmitterChannel";
    private static final int NOTIFICATION_ID = 2;

    // Audio config - 48kHz mono to match Opus optimal rate
    private static final int SAMPLE_RATE = 48000;
    private static final int CHANNEL_CONFIG = AudioFormat.CHANNEL_IN_MONO;
    private static final int AUDIO_FORMAT = AudioFormat.ENCODING_PCM_16BIT;
    private static final int PACKET_DURATION_MS = 20;
    private static final int CHANNELS = 1;

    // Opus frame size
    private static final int FRAME_SIZE_SAMPLES = SAMPLE_RATE * PACKET_DURATION_MS / 1000; // 960

    private UdpMicSender udpSender;
    private NsdMicAdvertiser nsdAdvertiser;
    private OpusCodec opusCodec;
    private AudioRecord audioRecord;
    private AAudioRecorder aaudioRecorder;
    private Thread recordingThread;
    private volatile boolean isRunning = false;
    public static boolean useAAudio = true;
    public static boolean isExclusiveMode = true;
    private String targetIp;
    private int targetPort;
    private PowerManager.WakeLock wakeLock;
    private android.net.wifi.WifiManager.WifiLock wifiLock;

    // DeepFilterNet
    private NativeDeepFilterNet deepFilterNet;
    private ByteBuffer dfBuffer;
    private short[] dfOverflow;
    private int dfOverflowCount = 0;

    public static volatile boolean isServiceRunning = false;
    public static UdpMicSender currentUdpSender;
    public static float volumeGain = 1.0f;
    public static long startTimeMillis = 0;
    
    private static AudioTransmitterService instance;
    public static AudioTransmitterService getInstance() { return instance; }
    
    // Settings states
    public static boolean appNoiseSuppressionEnabled = false;
    public static boolean systemAgcEnabled = false;
    public static boolean systemNoiseSuppressionEnabled = true;
    public static boolean systemEchoCancellationEnabled = false; // Internal only now, or remove if unused elsewhere
    // AI AEC removed

    // setAiAecEnabled removed
    
    // Audio Source
    public static int micSource = MediaRecorder.AudioSource.VOICE_COMMUNICATION;
    
    // Keys for persistence
    public static final String KEY_VOLUME_BOOST = "trans_volume_boost";
    public static final String KEY_NOISE_SUPPRESSION = "trans_noise_suppression";
    public static final String KEY_MIC_SOURCE = "trans_mic_source";
    public static final String KEY_AAUDIO_ENABLED = "trans_aaudio_enabled";
    public static final String KEY_EXCLUSIVE_MODE = "trans_exclusive_enabled";
    
    // Noise Gate for App Noise Suppression
    private static final float NOISE_GATE_THRESHOLD = 0.005f; // Adjust as needed

    // Effect instances to keep them alive
    private AutomaticGainControl agc;
    private NoiseSuppressor ns;
    private AcousticEchoCanceler aec;

    private final Object cleanupLock = new Object();
    private boolean isCleanedUp = false;

    @Override
    public void onCreate() {
        super.onCreate();
        instance = this;
        loadSettings();
        createNotificationChannel();
    }

    private void loadSettings() {
        android.content.SharedPreferences prefs = getSharedPreferences(MainActivity.PREFS_NAME, android.content.Context.MODE_PRIVATE);
        
        // Load Volume Boost
        boolean volumeBoostEnabled = prefs.getBoolean(KEY_VOLUME_BOOST, false);
        volumeGain = volumeBoostEnabled ? 2.0f : 1.0f;
        
        // Load Noise Suppression
        appNoiseSuppressionEnabled = prefs.getBoolean(KEY_NOISE_SUPPRESSION, false);
        
        // Load AAudio settings
        useAAudio = prefs.getBoolean(KEY_AAUDIO_ENABLED, true);
        isExclusiveMode = prefs.getBoolean(KEY_EXCLUSIVE_MODE, true);
        
        Log.i(TAG, "Settings loaded: VolumeBoost=" + volumeBoostEnabled + 
                ", NoiseSuppression=" + appNoiseSuppressionEnabled + 
                ", MicSource=" + micSource +
                ", UseAAudio=" + useAAudio +
                ", Exclusive=" + isExclusiveMode);
    }

    private void createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationChannel channel = new NotificationChannel(
                    CHANNEL_ID, "Audio Transmitter Service", NotificationManager.IMPORTANCE_HIGH); // Changed to HIGH
            channel.setLockscreenVisibility(Notification.VISIBILITY_PUBLIC);
            NotificationManager manager = getSystemService(NotificationManager.class);
            if (manager != null) manager.createNotificationChannel(channel);
        }
    }

    private Notification createNotification(String content) {
        return new NotificationCompat.Builder(this, CHANNEL_ID)
                .setContentTitle("Audio Server Active")
                .setContentText(content)
                .setSmallIcon(android.R.drawable.ic_btn_speak_now)
                .setPriority(NotificationCompat.PRIORITY_HIGH)
                .setCategory(NotificationCompat.CATEGORY_SERVICE)
                .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
                .setOngoing(true)
                .build();
    }

    @SuppressLint("MissingPermission")
    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null) return START_NOT_STICKY;

        targetIp = intent.getStringExtra("IP_ADDRESS");
        targetPort = intent.getIntExtra("PORT", 5003);

        // If IP is not provided, we rely on the PC discovering us
        String notificationText = (targetIp != null && !targetIp.isEmpty()) 
                ? "Sending mic to " + targetIp + ":" + targetPort
                : "Mic service active (waiting for PC)";

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(NOTIFICATION_ID, createNotification(notificationText),
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE);
        } else {
            startForeground(NOTIFICATION_ID, createNotification(notificationText));
        }

        startRecordingAndSending();
        isServiceRunning = true;
        return START_NOT_STICKY;
    }

    private void updateNotification() {
        boolean connected = udpSender != null && udpSender.isConnected();
        String content = connected ? 
                "Mic Streaming - Connected" : 
                "Mic Server Active - Waiting for PC...";
        
        Notification notification = createNotification(content);
        NotificationManager manager = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        if (manager != null) manager.notify(NOTIFICATION_ID, notification);
    }

    private void startRecordingAndSending() {
        startTimeMillis = System.currentTimeMillis();
        // Acquire WakeLock to keep CPU running when screen is off
        PowerManager powerManager = (PowerManager) getSystemService(POWER_SERVICE);
        if (powerManager != null) {
            wakeLock = powerManager.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "AudioOverLAN:MicWakeLock");
            wakeLock.acquire();
        }
        android.net.wifi.WifiManager wm = (android.net.wifi.WifiManager) getApplicationContext().getSystemService(WIFI_SERVICE);
        if (wm != null) {
            int lockType = Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q ? 
                    android.net.wifi.WifiManager.WIFI_MODE_FULL_HIGH_PERF : android.net.wifi.WifiManager.WIFI_MODE_FULL;
            wifiLock = wm.createWifiLock(lockType, "AudioOverLAN:MicWifiLock");
            wifiLock.acquire();
        }

        isRunning = true;
        recordingThread = new Thread(() -> {
            android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_AUDIO);

            try {
                // Initialize Opus encoder (48kHz, mono, 20ms frames)
                opusCodec = new OpusCodec(SAMPLE_RATE, CHANNELS, PACKET_DURATION_MS,
                        64000, 10, true, false); // 64kbps for voice
                if (!opusCodec.initEncoder()) {
                    Log.e(TAG, "Failed to init Opus encoder");
                    stopSelf();
                    return;
                }

                // Always start the UDP sender so it can listen for SUBSCRIBE probes and discovery
                udpSender = new UdpMicSender(targetIp, targetPort, SAMPLE_RATE, PACKET_DURATION_MS);
                udpSender.setConnectionListener(new UdpMicSender.OnConnectionStateListener() {
                    @Override
                    public void onConnected() {
                        updateNotification();
                    }

                    @Override
                    public void onDisconnected() {
                        updateNotification();
                    }
                });
                currentUdpSender = udpSender;
                udpSender.start();

                // Start mDNS/NSD advertisement so the PC can discover us
                nsdAdvertiser = new NsdMicAdvertiser(this, targetPort);
                nsdAdvertiser.start();

                // Try AAudio first if requested and supported
                boolean aaudioStarted = false;
                if (useAAudio && AAudioRecorder.isAvailable()) {
                    aaudioRecorder = new AAudioRecorder();
                    if (aaudioRecorder.isAAudioSupported()) {
                        Log.i(TAG, "Attempting to use AAudio for capture...");
                        
                        // AEC Logic removed
                        aaudioStarted = aaudioRecorder.start(SAMPLE_RATE, CHANNELS, isExclusiveMode);
                    }
                }

                if (aaudioStarted) {
                    Log.i(TAG, "AAudio capture started: " + aaudioRecorder.getStreamInfo());
                } else {
                    Log.i(TAG, "Falling back to AudioRecord...");
                    // Initialize AudioRecord with current micSource
                    int minBufferSize = AudioRecord.getMinBufferSize(SAMPLE_RATE, CHANNEL_CONFIG, AUDIO_FORMAT);
                    audioRecord = new AudioRecord(micSource,
                            SAMPLE_RATE, CHANNEL_CONFIG, AUDIO_FORMAT, Math.max(minBufferSize * 2, FRAME_SIZE_SAMPLES * 2 * 4));

                    if (audioRecord.getState() != AudioRecord.STATE_INITIALIZED) {
                        Log.e(TAG, "AudioRecord initialization failed");
                        stopSelf();
                        return;
                    }

                    audioRecord.startRecording();
                    Log.i(TAG, "AudioRecord started with source: " + micSource);

                    // Initial application of system effects
                    updateSystemEffectsInternal();
                }

                // Initialize DeepFilterNet if enabled
                if (appNoiseSuppressionEnabled) {
                    initDeepFilter();
                }

                // PCM buffer for exactly one 20ms frame
                short[] pcmFrame = new short[FRAME_SIZE_SAMPLES * CHANNELS];

                while (isRunning) {
                    // Read exactly one frame (960 samples for 20ms @ 48kHz)
                    int totalRead = 0;
                    while (totalRead < pcmFrame.length && isRunning) {
                        int read;
                        if (aaudioRecorder != null && aaudioRecorder.isStarted()) {
                            read = aaudioRecorder.read(pcmFrame, totalRead, pcmFrame.length - totalRead);
                        } else {
                            read = audioRecord.read(pcmFrame, totalRead, pcmFrame.length - totalRead);
                        }

                        if (read > 0) {
                            totalRead += read;
                        } else if (read < 0) {
                            Log.e(TAG, "Audio read error: " + read);
                            break;
                        } else {
                            // No data yet, yield to avoid spin lock (especially for AAudio)
                            try { Thread.sleep(1); } catch (InterruptedException ignored) {}
                        }
                    }

                    if (totalRead < pcmFrame.length) continue;

                    // 1. App Noise Suppression (DeepFilterNet)
                    if (appNoiseSuppressionEnabled) {
                        applyDeepFilter(pcmFrame);
                    }

                    // 2. Volume boost if gain != 1.0
                    if (volumeGain != 1.0f) {
                        for (int i = 0; i < pcmFrame.length; i++) {
                            int val = (int) (pcmFrame[i] * volumeGain);
                            if (val > 32767) val = 32767;
                            else if (val < -32768) val = -32768;
                            pcmFrame[i] = (short) val;
                        }
                    }

                    // Encode with Opus (Zero-allocation using internal buffer)
                    int encodedLength = opusCodec.encodeToInternalBuffer(pcmFrame, 0, FRAME_SIZE_SAMPLES);
                    if (encodedLength > 0) {
                        // Send via UDP if sender is initialized
                        if (udpSender != null) {
                            udpSender.sendAudioPacket(opusCodec.getEncodeBuffer(), encodedLength);
                        }
                    }
                }
            } catch (IOException e) {
                Log.e(TAG, "Failed to start UDP sender", e);
            } catch (Exception e) {
                Log.e(TAG, "Recording error", e);
            } finally {
                cleanup();
                stopSelf();
            }
        }, "MicRecordingThread");
        recordingThread.start();
    }

    private void cleanup() {
        synchronized (cleanupLock) {
            if (isCleanedUp) return;
            isCleanedUp = true;
        }

        isRunning = false;
        isServiceRunning = false;
        instance = null;
        currentUdpSender = null;
        if (audioRecord != null) {
            try {
                if (agc != null) agc.release();
                if (ns != null) ns.release();
                if (aec != null) aec.release();
                audioRecord.stop();
                audioRecord.release();
            } catch (Exception ignored) {}
            audioRecord = null;
            agc = null;
            ns = null;
            aec = null;
        }
        if (aaudioRecorder != null) {
            try {
                aaudioRecorder.stop();
            } catch (Exception ignored) {}
            aaudioRecorder = null;
        }
        if (udpSender != null) {
            udpSender.stop();
        }
        if (nsdAdvertiser != null) {
            nsdAdvertiser.stop();
        }
        if (opusCodec != null) {
            opusCodec.release();
        }
        if (deepFilterNet != null) {
            deepFilterNet.release();
            deepFilterNet = null;
        }

        if (wakeLock != null && wakeLock.isHeld()) {
            wakeLock.release();
            wakeLock = null;
        }
        if (wifiLock != null && wifiLock.isHeld()) {
            wifiLock.release();
            wifiLock = null;
        }
    }

    private void initDeepFilter() {
        if (deepFilterNet != null) return;
        try {
            deepFilterNet = new NativeDeepFilterNet(this);
            int dfFrameLengthBytes = (int) deepFilterNet.getFrameLength();
            dfBuffer = ByteBuffer.allocateDirect(dfFrameLengthBytes);
            dfBuffer.order(ByteOrder.LITTLE_ENDIAN);
            dfOverflow = new short[FRAME_SIZE_SAMPLES * 2]; // Buffer for overlap
            dfOverflowCount = 0;
            Log.i(DF_TAG, "DeepFilterNet initialized successfully via logic. Model Frame Length: " + dfFrameLengthBytes + " bytes");
        } catch (Exception e) {
            Log.e(DF_TAG, "CRITICAL: Failed to init DeepFilterNet: " + e.getMessage());
            appNoiseSuppressionEnabled = false;
        }
    }

    private int dfProcessCount = 0;
    private long dfTotalTime = 0;

    private void applyDeepFilter(short[] pcmFrame) {
        if (deepFilterNet == null) {
            initDeepFilter();
            if (deepFilterNet == null) return;
        }

        long startTime = System.nanoTime();
        int dfFrameSamples = (int) (deepFilterNet.getFrameLength() / 2); // 16-bit PCM
        int pcmPos = 0;

        // Combine overflow and new data
        short[] combined;
        if (dfOverflowCount > 0) {
            combined = new short[dfOverflowCount + pcmFrame.length];
            System.arraycopy(dfOverflow, 0, combined, 0, dfOverflowCount);
            System.arraycopy(pcmFrame, 0, combined, dfOverflowCount, pcmFrame.length);
        } else {
            combined = pcmFrame;
        }

        int totalSamples = combined.length;
        int processedPos = 0;
        int outPos = 0;

        while (totalSamples - processedPos >= dfFrameSamples) {
            dfBuffer.clear();
            for (int i = 0; i < dfFrameSamples; i++) {
                dfBuffer.putShort(combined[processedPos + i]);
            }
            dfBuffer.flip();
            
            deepFilterNet.processFrame(dfBuffer);
            
            dfBuffer.rewind();
            for (int i = 0; i < dfFrameSamples; i++) {
                if (outPos < pcmFrame.length) {
                    pcmFrame[outPos++] = dfBuffer.getShort();
                } else {
                    // This shouldn't happen if durations are multiples, 
                    // but for safety we'd handle it if we were streaming back to a larger buffer
                    dfBuffer.getShort(); 
                }
            }
            processedPos += dfFrameSamples;
        }

        // Store remaining samples for next time
        dfOverflowCount = totalSamples - processedPos;
        if (dfOverflowCount > 0) {
            System.arraycopy(combined, processedPos, dfOverflow, 0, dfOverflowCount);
        }

        long endTime = System.nanoTime();
        dfTotalTime += (endTime - startTime);
        dfProcessCount++;

        // Log stats every 100 frames (~2 seconds)
        if (dfProcessCount >= 100) {
            double avgMs = (dfTotalTime / (double) dfProcessCount) / 1000000.0;
            Log.d(DF_TAG, "Stats: Processed 100 frames. Avg processing time per 20ms block: " + String.format("%.2f", avgMs) + "ms");
            dfTotalTime = 0;
            dfProcessCount = 0;
        }
    }

    /**
     * Updates the enabled state of system audio effects in real-time.
     */
    public synchronized void updateSystemEffectsExternal() {
        if (audioRecord == null || audioRecord.getState() != AudioRecord.STATE_INITIALIZED) return;
        updateSystemEffectsInternal();
    }

    private void updateSystemEffectsInternal() {
        if (audioRecord == null) return;
        int sessionId = audioRecord.getAudioSessionId();

        // AGC
        if (AutomaticGainControl.isAvailable()) {
            if (systemAgcEnabled) {
                if (agc == null) agc = AutomaticGainControl.create(sessionId);
                if (agc != null) agc.setEnabled(true);
            } else if (agc != null) {
                agc.setEnabled(false);
            }
        }

        // Noise Suppression
        if (NoiseSuppressor.isAvailable()) {
            if (systemNoiseSuppressionEnabled) {
                if (ns == null) ns = NoiseSuppressor.create(sessionId);
                if (ns != null) ns.setEnabled(true);
            } else if (ns != null) {
                ns.setEnabled(false);
            }
        }

        // Echo Cancellation
        if (AcousticEchoCanceler.isAvailable()) {
            if (systemEchoCancellationEnabled) {
                if (aec == null) aec = AcousticEchoCanceler.create(sessionId);
                if (aec != null) aec.setEnabled(true);
            } else if (aec != null) {
                aec.setEnabled(false);
            }
        }
    }

    /**
     * Restarts the recording thread to apply changes that require a new AudioRecord instance.
     */
    public void restartCapture() {
        new Thread(() -> {
            isRunning = false;
            try {
                if (recordingThread != null) recordingThread.join(1000);
            } catch (InterruptedException ignored) {}
            startRecordingAndSending();
        }).start();
    }

    @Override
    public void onDestroy() {
        cleanup();
        super.onDestroy();
    }

    @Nullable
    @Override
    public IBinder onBind(Intent intent) { return null; }
}
