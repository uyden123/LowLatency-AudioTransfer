using System;
using System.Threading;
using AudioTransfer.Core.Audio;
using AudioTransfer.Core.Codec;
using AudioTransfer.Core.Logging;
using AudioTransfer.Core.Network;
using AudioTransfer.Core.Models;

namespace AudioTransfer.Core.Audio
{
    /// <summary>
    /// Orchestrates the playback process: JitterBuffer -> Decoder -> Resampler -> Wasapi.
    /// Manages the high-priority playback thread.
    /// </summary>
    public class PlayerPlaybackOrchestrator : IDisposable
    {
        private readonly MicJitterBuffer _jitterBuffer;
        private readonly OpusDecoderWrapper _decoder;
        private readonly WasapiPlayer _wasapiPlayer;
        private readonly Action<string> _logger;
        private readonly ServerStatistics _stats;
        
        private readonly AudioResampler _resampler = new();
        private readonly DriftManager _driftManager = new();
        
        private readonly short[] _pcmBuffer = new short[48000 * 60 / 1000];
        private readonly short[] _resampledBuffer = new short[48000 * 120 / 1000];
        
        private Thread? _playbackThread;
        private CancellationTokenSource? _cts;
        private bool _isRunning;
        private long _lastDriftLogTime;
        
        // VAD
        public bool VadEnabled { get; set; } = false;
        public double VadThreshold { get; set; } = -45.0; // dB
        public event Action<double>? OnVolumeUpdate; // -100 to 0 dB

        private double _currentRms;
        private int _vadHangoverCounter = 0;
        private const int VAD_HANGOVER_PACKETS = 10; // 200ms

        public PlayerPlaybackOrchestrator(
            MicJitterBuffer jitterBuffer, 
            OpusDecoderWrapper decoder, 
            WasapiPlayer wasapiPlayer,
            ServerStatistics stats,
            Action<string> logger)
        {
            _jitterBuffer = jitterBuffer;
            _decoder = decoder;
            _wasapiPlayer = wasapiPlayer;
            _stats = stats;
            _logger = logger;
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _cts = new CancellationTokenSource();
            _playbackThread = new Thread(PlaybackLoop)
            {
                Name = "MicPlaybackThread",
                IsBackground = true,
                Priority = ThreadPriority.Highest
            };
            _playbackThread.Start();
        }

        public void Stop()
        {
            _isRunning = false;
            _cts?.Cancel();
            _playbackThread?.Join(1000);
            _resampler.Reset();
            _driftManager.Reset();
        }

        private void PlaybackLoop()
        {
            _logger("[PlayerPlayback] Thread started.");
            var spinner = new SpinWait();

            try
            {
                while (_isRunning && !_cts!.Token.IsCancellationRequested)
                {
                    var packet = _jitterBuffer.Take();

                    if (packet == null)
                    {
                        if (_jitterBuffer.IsBuffering) { Thread.Sleep(2); continue; }
                        spinner.SpinOnce();
                        continue;
                    }
                    spinner.Reset();

                    int samplesPerChannel;
                    if (packet.IsPLC)
                    {
                        samplesPerChannel = _decoder.DecodePLCTo(_pcmBuffer);
                        // Zero level for PLC
                        _currentRms = 0;
                    }
                    else
                    {
                        samplesPerChannel = _decoder.DecodeTo(packet.Data, 0, packet.Length, _pcmBuffer);
                        
                        // Calculate RMS level
                        if (samplesPerChannel > 0) 
                        {
                            double sum = 0;
                            for (int i = 0; i < samplesPerChannel; i++) {
                                double s = _pcmBuffer[i] / 32768.0;
                                sum += s * s;
                            }
                            _currentRms = Math.Sqrt(sum / samplesPerChannel);
                        }
                        
                        // Notify UI about volume level
                        double db = 20 * Math.Log10(_currentRms + 1e-9);
                        OnVolumeUpdate?.Invoke(db);

                        // VAD / Gating Logic
                        if (VadEnabled)
                        {
                            bool above = db >= VadThreshold;
                            if (above) {
                                _vadHangoverCounter = VAD_HANGOVER_PACKETS;
                            } else {
                                if (_vadHangoverCounter > 0) {
                                    _vadHangoverCounter--;
                                    // Let the audio pass during hangover
                                } else {
                                    // Gate open: absolute silence
                                    Array.Clear(_pcmBuffer, 0, samplesPerChannel);
                                }
                            }
                        }

                        // Drift compensation
                        int bufferedMs = (int)(_wasapiPlayer.BufferedBytes * 1000L / (48000 * 4));
                        double ratio = _driftManager.CalculateRatio(_jitterBuffer, bufferedMs);

                        // Logging periodically
                        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        if (now - _lastDriftLogTime > 10000)
                        {
                            var stats = _jitterBuffer.GetStatistics();
                            _logger($"[PlayerPlayback] Buffer={bufferedMs}ms, Ratio={ratio:F4}, JitterBuf={stats.BufferLevel}/{stats.TargetPackets}");
                            _lastDriftLogTime = now;
                        }

                        _stats.IncrementPacketsSent();
                        _stats.IncrementBytesSent(packet.Length);
                    }

                    if (samplesPerChannel > 0)
                    {
                        int resampledCount = _resampler.Resample(_pcmBuffer, samplesPerChannel, 1, _driftManager.CurrentRatio, _resampledBuffer);
                        _wasapiPlayer.AddSamples(_resampledBuffer, 0, resampledCount);
                    }

                    _jitterBuffer.RecyclePacket(packet);
                }
            }
            catch (Exception ex)
            {
                if (_isRunning) _logger($"[PlayerPlayback] Fatal error: {ex.Message}");
            }
            finally
            {
                _logger("[PlayerPlayback] Thread stopped.");
            }
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}
