using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AudioTransfer.Core.Audio;
using AudioTransfer.Core.Codec;
using AudioTransfer.Core.Logging;
using AudioTransfer.Core.Models;
using AudioTransfer.Core.Network;

namespace AudioTransfer.Core.Facade
{
    /// <summary>
    /// Player engine: receives audio from Android mic and plays back on PC (Mode 2).
    /// </summary>
    public class PlayerEngine : IPlayerEngine
    {
        private UdpMicReceiver? _udpMicReceiver;
        private MicJitterBuffer? _micJitterBuffer;
        private OpusDecoderWrapper? _opusDecoder;
        private WasapiPlayer? _wasapiPlayer;
        private MicDriverSidebandAgent? _sidebandAgent;
        private MdnsDiscoveryClient? _micDiscoveryClient;
        private PlayerPlaybackOrchestrator? _playbackOrchestrator;
        private SystemAudioController? _systemAudio;

        private CancellationTokenSource? _cts;
        private volatile bool _running;
        private DateTime _startTime;
        private readonly ServerStatistics _stats = new();

        // Events
        public event EventHandler<string>? OnLog;
        public event EventHandler<string>? OnClientConnected;
        public event EventHandler<string>? OnClientDisconnected;
        public event EventHandler? OnStopped;

        public ServerStatistics Stats => _stats;
        public DateTime StartTime => _startTime;
        public bool IsRunning => _running;
        public TimeSpan Uptime => _running ? DateTime.UtcNow - _startTime : TimeSpan.Zero;
        public MicJitterBuffer? MicJitter => _micJitterBuffer;
        public UdpMicReceiver? MicReceiver => _udpMicReceiver;

        public PlayerEngine()
        {
            _systemAudio = new SystemAudioController(Log);
        }

        #region System Control

        public void SetSystemMute(bool muted) => _systemAudio?.SetMute(muted);

        #endregion

        #region Mode 2: Android Mic → PC (UDP with Jitter/PLC/Resample)

        public virtual async Task<bool> StartAndroidMicListenerAsync(string androidIp, int androidPort = 5003, string? playbackDeviceId = null, string? deviceName = null)
        {
            if (_running) throw new InvalidOperationException("Already running.");
            _running = true;
            _startTime = DateTime.UtcNow;
            _cts = new CancellationTokenSource();

            // 1. Networking & Discovery
            _sidebandAgent = new MicDriverSidebandAgent();
            if (!_sidebandAgent.Open()) Log("INFO: Sideband agent not found.");

            _micJitterBuffer = new MicJitterBuffer();
            _opusDecoder = new OpusDecoderWrapper(48000, 1, 20);

            IPEndPoint? targetEp = null;
            if (IPAddress.TryParse(androidIp, out var addr) && androidIp != "0.0.0.0")
            {
                targetEp = new IPEndPoint(addr, androidPort);
            }

            _udpMicReceiver = new UdpMicReceiver(androidPort, _micJitterBuffer, targetEp, deviceName);
            _udpMicReceiver.OnAndroidConnected += (s, e) => OnClientConnected?.Invoke(this, _udpMicReceiver.ConnectedEndpoint?.ToString() ?? "Android");
            _udpMicReceiver.OnAndroidDisconnected += (s, e) => OnClientDisconnected?.Invoke(this, "Android");
            _udpMicReceiver.Start();

            if (targetEp == null)
            {
                _micDiscoveryClient = new MdnsDiscoveryClient("_audiooverlan-mic._udp");
                _micDiscoveryClient.OnServiceDiscovered += (s, service) =>
                {
                    if (IPAddress.TryParse(service.IPAddress, out var ip)) 
                        _udpMicReceiver.TargetEndpoint = new IPEndPoint(ip, service.Port);
                };
                _micDiscoveryClient.Start();
            }

            // 2. Playback Orchestration
            _wasapiPlayer = new WasapiPlayer(1000, playbackDeviceId);
            _wasapiPlayer.Initialize();
            _wasapiPlayer.Start();

            _playbackOrchestrator = new PlayerPlaybackOrchestrator(_micJitterBuffer, _opusDecoder, _wasapiPlayer, _stats, Log);
            _playbackOrchestrator.Start();

            Log($"[PlayerEngine] Started. Listening on port {androidPort}. Handshake initiated to {androidIp}");
            return true;
        }


        #endregion

        #region Lifecycle

        public virtual void Stop()
        {
            if (!_running) return;
            _running = false;
            Log("Shutting down player...");

            _cts?.Cancel();
            _udpMicReceiver?.Stop();
            _micJitterBuffer?.Clear();
            _wasapiPlayer?.Stop();
            _playbackOrchestrator?.Stop();
            try { _micDiscoveryClient?.Stop(); } catch { }

            OnStopped?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            Stop();
            _udpMicReceiver?.Dispose();
            _opusDecoder?.Dispose();
            _wasapiPlayer?.Dispose();
            _sidebandAgent?.Dispose();
            _micDiscoveryClient?.Dispose();
            _playbackOrchestrator?.Dispose();
            _cts?.Dispose();
        }

        #endregion

        #region Helpers

        public static string[] GetLocalIPAddresses()
        {
            try { return Dns.GetHostAddresses(Dns.GetHostName()).Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).Distinct().ToArray(); }
            catch { return new[] { "127.0.0.1" }; }
        }

        private void Log(string message)
        {
            CoreLogger.Instance.Log(message);
            OnLog?.Invoke(this, message);
        }

        #endregion
    }
}
