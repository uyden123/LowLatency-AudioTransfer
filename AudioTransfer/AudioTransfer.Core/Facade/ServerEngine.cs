using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AudioTransfer.Core.Audio;
using AudioTransfer.Core.Audio.Mixer;
using AudioTransfer.Core.Buffers;
using AudioTransfer.Core.Codec;
using AudioTransfer.Core.Logging;
using AudioTransfer.Core.Models;
using AudioTransfer.Core.Network;
using AudioTransfer.Core.Plugins;
using AudioTransfer.Core.Commands;

namespace AudioTransfer.Core.Facade
{
    /// <summary>
    /// Server engine: captures WASAPI audio and streams to Android via UDP (Mode 3).
    /// </summary>
    public class ServerEngine : IServerEngine
    {
        private WasapiManager? _wasapiManager;
        private AudioEncoderEngine? _encoderEngine;
        private UdpBroadcaster? _broadcaster;
        private ControlOrchestrator? _controlOrchestrator;
        private ClientConnectionManager? _connectionManager;

        private UdpClient? _udpServer;
        private OpusEncoderWrapper? _opusEncoder;
        private MdnsAdvertiser? _mdnsAdvertiser;
        private SystemAudioController? _systemAudio;

        private CancellationTokenSource? _cts;
        private readonly AudioPipeline _pipeline = new();

        private volatile bool _running;
        private readonly ServerStatistics _stats = new();
        private readonly ServerConfig _config;
        private DateTime _startTime;
        private string? _deviceName;
        private readonly CommandRegistry _commandRegistry = new();
        private VolumeMixerManager? _volumeMixer;

        public event EventHandler<string>? OnLog;
        public event EventHandler<(string IpAddress, string DeviceName)>? OnClientConnected;
        public event EventHandler<string>? OnClientDisconnected;
        public event EventHandler? OnStopped;

        public ServerStatistics Stats => _stats;
        public ServerConfig Config => _config;
        public DateTime StartTime => _startTime;
        public bool IsRunning => _running;
        public bool IsMuted { get; set; } = false;
        public TimeSpan Uptime => _running ? DateTime.UtcNow - _startTime : TimeSpan.Zero;
        public IReadOnlyList<IAudioPlugin> Plugins => _pipeline.Stages;
        public IPEndPoint? ConnectedClient => _connectionManager?.GetAuthenticatedClients().FirstOrDefault();

        public ServerEngine(ServerConfig? config = null)
        {
            _config = config ?? new ServerConfig();
            _pipeline.Add(new DrcPlugin()).Add(new EqPlugin());
            _systemAudio = new SystemAudioController(Log);
        }

        protected ServerEngine() { _config = new ServerConfig(); }

        #region System Control

        public void SetSystemMute(bool muted) => _systemAudio?.SetMute(muted);
        public void TogglePlugin(string name, bool enabled)
        {
            _pipeline.TogglePlugin(name, enabled);
            Log($"Plugin {name} {(enabled ? "enabled" : "disabled")}");
        }

        #endregion

        private void ApplyPlugins(short[] buffer, int sampleRate, int channels) => _pipeline.Process(buffer, buffer.Length, sampleRate, channels);

        public async Task SwitchCaptureDevice(string? deviceId)
        {
            if (!_running || _wasapiManager == null) return;
            await _wasapiManager.SwitchDeviceAsync(deviceId);
        }

        #region Mode 3: WASAPI → Android (Low-Latency UDP)

        public void StartWasapiToAndroid(int listenPort, string? captureDeviceId = null, string? instanceName = null)
        {
            if (_running) throw new InvalidOperationException("Already running.");
            _deviceName = instanceName;
            _running = true;
            _startTime = DateTime.UtcNow;
            _cts = new CancellationTokenSource();
            _udpServer = new UdpClient(listenPort);
            
            // 1. Networking Managers
            _broadcaster = new UdpBroadcaster(_udpServer, Log);
            _connectionManager = new ClientConnectionManager(
                (data, ep) => { try { _udpServer.Send(data, data.Length, ep); } catch { } },
                Log
            );
            _connectionManager.ClientConnected += (ep, name) => OnClientConnected?.Invoke(this, (ep.Address.ToString(), name));
            _connectionManager.ClientDisconnected += (ep) => OnClientDisconnected?.Invoke(this, ep.Address.ToString());
            _connectionManager.StartMaintenance(_deviceName ?? Environment.MachineName, _cts.Token);

            int controlPort = listenPort + 1;
            _controlOrchestrator = new ControlOrchestrator(controlPort, _udpServer, _connectionManager, ProcessControlJson, Log);
            _controlOrchestrator.Start();

            _mdnsAdvertiser = new MdnsAdvertiser(_deviceName ?? Environment.MachineName.ToUpper(), "_audiooverlan._udp", listenPort);
            _mdnsAdvertiser.Start();

            // 2. Audio Engine
            int sampleRate = _config.Audio.SampleRate;
            int channels = _config.Audio.Channels;
            _opusEncoder = new OpusEncoderWrapper(channels, _config.Opus.Bitrate, _config.Opus.FrameSizeMs);
            
            int samplesPerFrame = (int)(sampleRate * _config.Opus.FrameSizeMs / 1000.0) * channels;
            _encoderEngine = new AudioEncoderEngine(_opusEncoder, samplesPerFrame, (int)(sampleRate * _config.Opus.FrameSizeMs / 1000.0));

            _wasapiManager = new WasapiManager(_config.Audio.Mode == CaptureMode.ASIO ? CreateAsioCapturePlaceholder() : new WasapiTimedCapture(), Log);
            
            short[] tempReadBuf = new short[8192];
            _wasapiManager.OnDataAvailable += (data, length, isSilent) =>
            {
                if (!_running) return;
                var clients = _connectionManager.GetAuthenticatedClients();
                if (clients.Count == 0) return;

                int samplesCount = length / sizeof(short);
                if (samplesCount > tempReadBuf.Length) Array.Resize(ref tempReadBuf, samplesCount);

                if (isSilent) Array.Clear(tempReadBuf, 0, samplesCount);
                else System.Runtime.InteropServices.Marshal.Copy(data, tempReadBuf, 0, samplesCount);

                ApplyPlugins(tempReadBuf, sampleRate, channels);

                var packets = _encoderEngine.Process(tempReadBuf, samplesCount, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                foreach (var p in packets)
                {
                    _broadcaster.Broadcast(p, p.Length, clients);
                    _stats.IncrementPacketsSent();
                    _stats.IncrementBytesSent(p.Length);
                }
            };
            
            _wasapiManager.Start(captureDeviceId);

            // 3. Volume Mixer integration
            try
            {
                _volumeMixer = VolumeMixerManager.Instance;
                _volumeMixer.OnSessionsUpdated += OnMixerSessionsUpdated;
                _volumeMixer.OnSessionChanged += OnMixerSessionChanged;
                _volumeMixer.Start();
            }
            catch (Exception ex) { Log($"[VolumeMixer] Failed to start: {ex.Message}"); }
        }

        private void ProcessControlJson(string json, IPEndPoint? sender)
        {
            var context = new CommandContext
            {
                OpusEncoder = _opusEncoder,
                SetSystemMute = SetSystemMute,
                TogglePlugin = TogglePlugin,
                EqPlugin = _pipeline.Stages.OfType<EqPlugin>().FirstOrDefault(),
                VolumeMixer = _volumeMixer,
                SendControlMessageAsync = async (msg) => await _controlOrchestrator!.SendBroadcastAsync(msg),
                Log = (msg) => Log(msg),
                DisconnectClient = () => { if (sender != null) _connectionManager?.RemoveClient(sender); },
                IsMuted = IsMuted
            };
            _commandRegistry.Execute(json, context);
            IsMuted = context.IsMuted;
        }

        #endregion

        #region Lifecycle

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            Log("Shutting down server...");

            _controlOrchestrator?.SendBroadcastAsync("{\"command\":\"stop\"}").Wait(500);
            _cts?.Cancel();
            _wasapiManager?.Stop();
            _controlOrchestrator?.Stop();

            if (_volumeMixer != null)
            {
                _volumeMixer.OnSessionsUpdated -= OnMixerSessionsUpdated;
                _volumeMixer.OnSessionChanged -= OnMixerSessionChanged;
            }
            
            try { _mdnsAdvertiser?.Stop(); } catch { }
            try { _udpServer?.Close(); } catch { }
            
            OnStopped?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            Stop();
            _wasapiManager?.Dispose();
            _encoderEngine?.Dispose();
            _controlOrchestrator?.Dispose();
            _opusEncoder?.Dispose();
            _udpServer?.Dispose();
            _mdnsAdvertiser?.Dispose();
            _cts?.Dispose();
        }

        private IAudioCapture CreateAsioCapturePlaceholder() => throw new NotSupportedException("ASIO is not yet implemented.");

        #endregion

        #region Helpers & Bridges

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

        private void OnMixerSessionsUpdated(List<AudioSessionModel> sessions) => _controlOrchestrator?.SendBroadcastAsync(JsonSerializer.Serialize(new { command = "mixer_sync", sessions }));
        private void OnMixerSessionChanged(AudioSessionModel session) => _controlOrchestrator?.SendBroadcastAsync(JsonSerializer.Serialize(new { command = "mixer_update", session }));

        #endregion
    }
}
