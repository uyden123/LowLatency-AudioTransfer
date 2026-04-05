package com.example.audiooverlan.audio;

import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.util.Log;

import com.example.audiooverlan.viewmodels.PlayerState;
import com.example.audiooverlan.viewmodels.PlayerStateRepository;

public class PlayerAudioPipeline {
    private static final String TAG = "AudioPipeline";

    private final JitterBuffer jitterBuffer;
    private final OpusCodec opusCodec;
    private final AudioResampler resampler;
    private volatile AAudioPlayer aaudioPlayer;
    private volatile android.media.AudioTrack audioTrack;
    
    private Thread playbackThread;
    private Thread watchdogThread;
    private volatile boolean isPlaying = false;
    private final Object outputLock = new Object();
    private final short[] pcmBufferWorkspace = new short[960 * 2];
    private final short[][] resampleRef = new short[1][];
    private final JitterBuffer.Statistics stats = new JitterBuffer.Statistics();
    
    private final int sampleRate;
    private final int channels;
    private final int audioFormat;
    private boolean useAAudio;
    private boolean isExclusiveMode;
    private boolean isScoActive = false;

    // Stats    // Metrics
    private long lastMetricsTime = 0;
    private long totalPacketsProcessed = 0;
    
    // Smooth speed transitions to avoid resampling phase artifacts
    private double smoothedSpeedRatio = 1.0;
    
    // Original Stats & State
    private long packetCount = 0;
    private long totalLatencySum = 0;
    private long currentLatencyVal = 0;
    private long maxLatencyVal = 0;
    private double avgLatencyVal = 0;
    private long baseMinDelay = -1;
    private double currentRatio = 1.0;
    private final String connectedIp;
    private volatile short[] latestSamples;
    private volatile String currentCodecName = "Opus";

    public interface ClockOffsetProvider { long getOffset(); }
    public interface OnStatsUpdateListener { void onUpdate(); }
    
    private ClockOffsetProvider clockOffsetProvider;
    private OnStatsUpdateListener statsUpdateListener;

    public PlayerAudioPipeline(AudioConfig config, String ip) {
        this.sampleRate = config.sampleRate;
        this.channels = config.channels;
        this.audioFormat = config.audioFormat;
        this.useAAudio = config.useAAudio;
        this.isExclusiveMode = config.exclusiveMode;
        this.connectedIp = ip;

        this.jitterBuffer = new JitterBuffer();
        this.jitterBuffer.setBufferingConfig(config.bufferMode, config.minBufferMs, config.maxBufferMs);
        this.opusCodec = new OpusCodec(sampleRate, channels, AudioConstants.OPUS_FRAME_SIZE_MS);
        this.opusCodec.initDecoder();
        this.resampler = new AudioResampler(channels);
    }

    public synchronized void setVolume(float volume) {
        if (aaudioPlayer != null) {
            aaudioPlayer.setVolume(volume);
        } else if (audioTrack != null) {
            audioTrack.setVolume(volume);
        }
    }

    public void setClockOffsetProvider(ClockOffsetProvider p) { this.clockOffsetProvider = p; }
    public void setOnStatsUpdateListener(OnStatsUpdateListener l) { this.statsUpdateListener = l; }

    public synchronized void setExclusiveMode(boolean exclusive) {
        if (this.isExclusiveMode == exclusive) return;
        this.isExclusiveMode = exclusive;
        if (isPlaying) restartAudioEngine(isScoActive);
    }

    public synchronized void setAAudioMode(boolean enabled) {
        if (this.useAAudio == enabled) return;
        this.useAAudio = enabled;
        if (isPlaying) restartAudioEngine(isScoActive);
    }

    public synchronized void start() {
        if (isPlaying) return;
        isPlaying = true;
        initAudioEngine();
        playbackThread = new Thread(this::playbackLoop, "PlayerPlaybackThread");
        playbackThread.setPriority(Thread.MAX_PRIORITY);
        playbackThread.start();

        startWatchdog();
    }

    private void startWatchdog() {
        watchdogThread = new Thread(() -> {
            Log.i(TAG, "Watchdog thread started");
            while (isPlaying) {
                try {
                    Thread.sleep(2500); // Check every 2.5 seconds
                    if (isPlaying && !isAudioOutputAlive()) {
                        Log.w(TAG, "Watchdog detected dead audio output, restarting...");
                        restartAudioEngine();
                    }
                } catch (InterruptedException e) {
                    break;
                } catch (Exception e) {
                    Log.e(TAG, "Watchdog error", e);
                }
            }
            Log.i(TAG, "Watchdog thread exiting");
        }, "AudioWatchdog");
        watchdogThread.start();
    }

    private void stopWatchdog() {
        if (watchdogThread != null) {
            watchdogThread.interrupt();
            watchdogThread = null;
        }
    }

    private void initAudioEngine() {
        if (useAAudio && AAudioPlayer.isAvailable()) {
            aaudioPlayer = new AAudioPlayer();
            int usage = (isScoActive) ? 2 :  isExclusiveMode ?  AudioAttributes.USAGE_GAME: AudioAttributes.USAGE_MEDIA;
            int content = (isScoActive) ? 1 : (isExclusiveMode ? AudioAttributes.CONTENT_TYPE_MOVIE : AudioAttributes.CONTENT_TYPE_MUSIC);
            boolean success = aaudioPlayer.start(sampleRate, channels, 0, isExclusiveMode, usage, content);
            if (!success) {
                aaudioPlayer = null;
                initAudioTrack();
            }
        } else {
            initAudioTrack();
        }
    }

    private void initAudioTrack() {
        int channelConfig = (channels == 2) ? AudioFormat.CHANNEL_OUT_STEREO : AudioFormat.CHANNEL_OUT_MONO;
        int minBuf = android.media.AudioTrack.getMinBufferSize(sampleRate, channelConfig, audioFormat);
        audioTrack = new android.media.AudioTrack.Builder()
                .setAudioAttributes(new android.media.AudioAttributes.Builder()
                        .setUsage(isScoActive ? android.media.AudioAttributes.USAGE_VOICE_COMMUNICATION : android.media.AudioAttributes.USAGE_MEDIA)
                        .setContentType(isScoActive ? android.media.AudioAttributes.CONTENT_TYPE_SPEECH : android.media.AudioAttributes.CONTENT_TYPE_MUSIC)
                        .setFlags(isExclusiveMode ? android.media.AudioAttributes.FLAG_LOW_LATENCY : 0)
                        .build())
                .setAudioFormat(new AudioFormat.Builder()
                        .setEncoding(audioFormat)
                        .setSampleRate(sampleRate)
                        .setChannelMask(channelConfig)
                        .build())
                .setBufferSizeInBytes(Math.max(minBuf, 4096))
                .build();
    }

    private void playbackLoop() {
        android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_AUDIO);
        if (audioTrack != null && !useAAudio) audioTrack.play();

        int frameSamples = AudioConstants.FRAME_SIZE_SAMPLES;

        while (isPlaying) {
            JitterBuffer.AudioPacket packet = jitterBuffer.take();
            if (packet == null) {
                try { jitterBuffer.waitForData(10); } catch (InterruptedException e) { break; }
                continue;
            }

            double targetSpeedRatio = calculateSpeedRatio();
            
            // Dynamic EMA: Use faster adaptation (0.1) if far from target to clear burst bloat quickly, 
            // otherwise use smooth adaptation (0.02) for high pitch stability.
            int excess = jitterBuffer.size() - jitterBuffer.getTargetPackets();
            double emaWeight = (Math.abs(excess) > 5) ? 0.1 : 0.02;
            smoothedSpeedRatio = smoothedSpeedRatio * (1.0 - emaWeight) + targetSpeedRatio * emaWeight;
            
            double finalRatio = currentRatio * smoothedSpeedRatio;

            // NEW: Zero-copy Native Path for AAudio
            final AAudioPlayer currentAAudio = aaudioPlayer;
            if (useAAudio && currentAAudio != null && currentAAudio.isStarted()) {
                if (packet.isPLC) {
                    // FEC: Recover lost packet from the NEXT packet's redundant data if available
                    // IMPROVEMENT: Only use FEC if 'next' is the immediate numerical successor
                    JitterBuffer.AudioPacket next = jitterBuffer.peek();
                    if (next != null && !next.isPLC && next.sequence == (packet.sequence + 1) % 65536) {
                        currentAAudio.writeEncoded(next.data, next.length, frameSamples, finalRatio, true);
                    } else {
                        // Normal PLC (extrapolation)
                        currentAAudio.writeEncoded(null, 0, frameSamples, finalRatio, false);
                    }
                } else {
                    // Normal Decode
                    int written = currentAAudio.writeEncoded(packet.data, packet.length, frameSamples, finalRatio, false);
                    if (written > 0) {
                        currentCodecName = (packet.codec == 0) ? "PCM Raw" : "Opus";
                        updateMetrics(packet);
                    }
                }
            } else {
                // FALLBACK: Java path for AudioTrack
                final android.media.AudioTrack currentTrack = audioTrack;
                if (currentTrack == null) continue;

                int samplesPerChannel;
                if (packet.isPLC) {
                    samplesPerChannel = opusCodec.decodeTo(null, 0, pcmBufferWorkspace);
                } else {
                    currentCodecName = (packet.codec == 0) ? "PCM Raw" : "Opus";
                    samplesPerChannel = opusCodec.decodeTo(packet.data, packet.length, pcmBufferWorkspace);
                    if (samplesPerChannel > 0) updateMetrics(packet);
                }

                if (samplesPerChannel > 0) {
                    int totalSamples = samplesPerChannel * channels;
                    if (Math.abs(finalRatio - 1.0) < 0.0001) {
                        synchronized (outputLock) {
                            if (isPlaying) writeToOutput(pcmBufferWorkspace, 0, totalSamples);
                        }
                    } else {
                        int outCount = resampler.resample(pcmBufferWorkspace, totalSamples, finalRatio, resampleRef);
                        synchronized (outputLock) {
                            if (isPlaying) writeToOutput(resampleRef[0], 0, outCount);
                        }
                    }
                    if (latestSamples == null || latestSamples.length != totalSamples) {
                        latestSamples = new short[totalSamples];
                    }
                    System.arraycopy(pcmBufferWorkspace, 0, latestSamples, 0, totalSamples);
                }
            }
            jitterBuffer.recyclePacket(packet);
        }
    }

    private void updateMetrics(JitterBuffer.AudioPacket packet) {
        packetCount++;
        long offset = (clockOffsetProvider != null) ? clockOffsetProvider.getOffset() : 0;
        long pcTimeWithOffset = packet.wallClock + offset;
        long latency = System.currentTimeMillis() - pcTimeWithOffset;
        currentLatencyVal = Math.max(0, latency);
        totalLatencySum += currentLatencyVal;
        avgLatencyVal = (double) totalLatencySum / (packetCount / 50 + 1);
        if (currentLatencyVal > maxLatencyVal) maxLatencyVal = currentLatencyVal;

        if (packetCount % 50 == 0) {
            jitterBuffer.fillStatistics(stats);
            float currentBufferMs = jitterBuffer.size() * AudioConstants.OPUS_FRAME_SIZE_MS;
            float playbackLat = (useAAudio && aaudioPlayer != null) ? (float)aaudioPlayer.getLatencyMs() : 25.0f;
            float total = (float)currentLatencyVal + currentBufferMs + playbackLat;

            PlayerStateRepository.getInstance().updateState(new PlayerState.Playing(
                connectedIp, currentLatencyVal, maxLatencyVal, avgLatencyVal,
                jitterBuffer.size(), stats.targetPackets, stats.lastTransitDelay,
                stats.avgTransitDelay, stats.bitrateKbps, stats.lossRate, stats.latePackets,
                currentCodecName, currentBufferMs, playbackLat, total
            ));

            android.util.Log.d("AudioPipeline", String.format(java.util.Locale.US,
                "Latency breakdown: Total=%.1fms (Capture/Net/Enc=%dms, Buf=%.1fms, Play=%.1fms)",
                total, currentLatencyVal, currentBufferMs, playbackLat));

            updateClockDrift();
            if (statsUpdateListener != null) statsUpdateListener.onUpdate();
        }
    }

    private void updateClockDrift() {
        if (baseMinDelay == -1 && stats.minDelay > 0) {
            baseMinDelay = (long) stats.minDelay;
        } else if (baseMinDelay > 0 && stats.minDelay > 0) {
            long drift = (long) stats.minDelay - baseMinDelay;
            // SUBTLE DRIFT: Adjust clock by 0.05% if 10ms drift accumulated
            if (drift > 10) { currentRatio = 1.0005; baseMinDelay += 2; }
            else if (drift < -10) { currentRatio = 0.9995; baseMinDelay -= 2; }
            else { currentRatio = 1.0; }
        }
    }

    private double calculateSpeedRatio() {
        int bufferLevel = jitterBuffer.size();
        int target = jitterBuffer.getTargetPackets();
        if (target <= 0) target = 1;
        
        // BUFFER OVERFLOW: If we drift by more than 250ms, jump reset
        int resetThreshold = Math.max(target * 10, 50); 
        if (bufferLevel > resetThreshold) { 
            Log.w(TAG, "Buffer overflow (" + bufferLevel + "), resetting...");
            jitterBuffer.forceReset(); 
            return 1.0; 
        }
        
        int excess = bufferLevel - target;
        double correction = 0.0;
        if (Math.abs(excess) > 0) {
            // Proportional correction: 0.2% per packet of excess
            correction = excess * 0.002;
        }

        // Native ring buffer latency correction (proportional, centered at 25ms)
        if (useAAudio && aaudioPlayer != null && aaudioPlayer.isStarted()) {
            int ringFrames = aaudioPlayer.getBufferedFrames();
            double ringMs = (ringFrames * 1000.0) / AudioConstants.SAMPLE_RATE;
            
            // Linear proportional: 0 correction at 25ms, positive above, negative below
            double ringCorrection = (ringMs - 25.0) * 0.0005;
            correction += Math.max(-0.01, Math.min(0.015, ringCorrection));
        }
        
        // Clamp total correction: -3% (slow down) to +5% (aggressive catch-up)
        correction = Math.max(-0.03, Math.min(0.05, correction));
        
        return 1.0 + correction;
    }

    private void writeToOutput(short[] buffer, int offset, int size) {
        synchronized (outputLock) {
            if (useAAudio && aaudioPlayer != null) {
                aaudioPlayer.write(buffer, offset, size);
            } else if (audioTrack != null) {
                try {
                    audioTrack.write(buffer, offset, size);
                } catch (Exception e) {
                    Log.e(TAG, "AudioTrack write failed, might be device change", e);
                }
            }
        }
    }

    /**
     * Re-initializes the output engine (AAudio/AudioTrack) while keeping the jitter buffer.
     * Use this when audio devices change (e.g. Bluetooth disconnect).
     */
    public void restartAudioEngine() {
        restartAudioEngine(isScoActive);
    }

    public void restartAudioEngine(boolean scoActive) {
        Log.i(TAG, "Restarting audio engine (scoActive=" + scoActive + ", exclusive=" + isExclusiveMode + ")...");
        synchronized (outputLock) {
            this.isScoActive = scoActive;
            stopAudioOutputInternal();
            
            // Re-init with correct mode
            if (useAAudio && AAudioPlayer.isAvailable()) {
                aaudioPlayer = new AAudioPlayer();
                //int usage = (isScoActive) ? 2 :  (isExclusiveMode ?  AudioAttributes.USAGE_GAME: AudioAttributes.USAGE_MEDIA);
                //int content = (isScoActive) ? 1 : (isExclusiveMode ? AudioAttributes.CONTENT_TYPE_MOVIE : AudioAttributes.CONTENT_TYPE_MUSIC);
                int usage = (isScoActive) ? 2 :  AudioAttributes.USAGE_MEDIA;
                int content = (isScoActive) ? 1 :  AudioAttributes.CONTENT_TYPE_MUSIC;
                boolean success = aaudioPlayer.start(sampleRate, channels, 0, isExclusiveMode, usage, content);
                if (!success) {
                    aaudioPlayer = null;
                    initAudioTrack();
                }
            } else {
                initAudioTrack();
            }
            if (audioTrack != null && !useAAudio) {
                try { audioTrack.play(); } catch (Exception ignored) {}
            }
        }
    }

    public synchronized void stopAudioOutput() {
        synchronized (outputLock) {
            stopAudioOutputInternal();
        }
    }

    private void stopAudioOutputInternal() {
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
    }


    private short[] generatePinkNoise(int len) {
        short[] buf = new short[len];
        java.util.Random rnd = new java.util.Random();
        double[] octaves = new double[5];
        for (int i = 0; i < octaves.length; i++) octaves[i] = rnd.nextDouble() * 2.0 - 1.0;
        int counter = 0;
        for (int i = 0; i < len; i++) {
            int ctz = Integer.numberOfTrailingZeros(counter == 0 ? 1 : counter);
            if (ctz < octaves.length) octaves[ctz] = rnd.nextDouble() * 2.0 - 1.0;
            counter++;
            double sum = 0;
            for (double o : octaves) sum += o;
            buf[i] = (short) (sum / octaves.length * 4.0);
        }
        return buf;
    }

    public void stop() {
        isPlaying = false;
        stopWatchdog();
        if (playbackThread != null) { playbackThread.interrupt(); playbackThread = null; }
        if (aaudioPlayer != null) { aaudioPlayer.stop(); aaudioPlayer = null; }
        if (audioTrack != null) { try { audioTrack.stop(); audioTrack.release(); } catch (Exception ignored) {} audioTrack = null; }
        if (opusCodec != null) opusCodec.release();
        jitterBuffer.clear();
    }

    /**
     * Deep health check for whether audio is actually flowing.
     * Catches MIUI zombied streams where isStarted is true but the
     * native Oboe stream has been disconnected/paused by the OS.
     *
     * Checks 3 signals:
     * 1. Engine null → output was explicitly stopped
     * 2. Native stream dead → getLatencyMs() returns -1
     * 3. Buffer stall → jitter buffer has data but ring buffer is empty
     */
    public boolean isAudioOutputAlive() {
        synchronized (outputLock) {
            if (useAAudio) {
                if (aaudioPlayer == null) return false;
                if (!aaudioPlayer.isStarted()) return false;

                // Deep check 1: Native stream validity
                // getLatencyMs() returns -1 if gIsRunning is false or stream is null
                double latency = aaudioPlayer.getLatencyMs();
                if (latency < 0) {
                    Log.w(TAG, "Audio health check: native stream reports invalid latency");
                    return false;
                }

                // Deep check 2: Buffer stall detection
                // If jitter buffer has packets but AAudio ring buffer is empty,
                // audio is not flowing through the pipeline
                int jbSize = jitterBuffer.size();
                int ringFrames = aaudioPlayer.getBufferedFrames();
                if (jbSize > 3 && ringFrames == 0) {
                    Log.w(TAG, "Audio health check: buffer stall detected (jb=" + jbSize + ", ring=0)");
                    return false;
                }

                return true;
            } else {
                return audioTrack != null && audioTrack.getPlayState() == android.media.AudioTrack.PLAYSTATE_PLAYING;
            }
        }
    }

    public JitterBuffer getJitterBuffer() { return jitterBuffer; }
    public short[] getLatestSamples() { 
        if (useAAudio && aaudioPlayer != null && aaudioPlayer.isStarted()) {
            int copied = aaudioPlayer.getLatestSamples(pcmBufferWorkspace, pcmBufferWorkspace.length);
            if (copied > 0) {
                if (latestSamples == null || latestSamples.length != copied) {
                    latestSamples = new short[copied];
                }
                System.arraycopy(pcmBufferWorkspace, 0, latestSamples, 0, copied);
            }
        }
        return latestSamples; 
    }
    public long getCurrentLatency() { return currentLatencyVal; }
    public long getMaxLatency() { return maxLatencyVal; }
    public double getAvgLatency() { return avgLatencyVal; }
    public void fillStatistics(JitterBuffer.Statistics out) { jitterBuffer.fillStatistics(out); }
    public void setBufferingConfig(JitterBuffer.BufferMode mode, int minMs, int maxMs) {
        jitterBuffer.setBufferingConfig(mode, minMs, maxMs);
    }
    public void setBufferingMode(boolean a, int f) {
        jitterBuffer.setBufferingConfig(a ? JitterBuffer.BufferMode.MEDIUM : JitterBuffer.BufferMode.CUSTOM, f, f + 40);
    }
}
