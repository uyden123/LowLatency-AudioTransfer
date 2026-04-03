using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using AudioTransfer.Core.Models;
using AudioTransfer.Core.Network;

namespace AudioTransfer.Core.Facade
{
    /// <summary>
    /// Facade class that unites ServerEngine and PlayerEngine for backward compatibility with CLI/GUI.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class AudioEngine : IDisposable
    {
        public enum StreamingMode
        {
            WasapiToAndroid,
            AndroidMicListener,
            WasapiBroadcast
        }

        private ServerEngine? _server;
        private PlayerEngine? _player;

        public StreamingMode CurrentMode { get; private set; }
        public bool IsRunning => (_server != null && _server.IsRunning) || (_player != null && _player.IsRunning);

        public TimeSpan Uptime => _server?.Uptime ?? _player?.Uptime ?? TimeSpan.Zero;
        public ServerStatistics Stats => _server?.Stats ?? _player?.Stats ?? new ServerStatistics();
        public IPEndPoint? ConnectedClient => _server?.ConnectedClient;
        public UdpMicReceiver? MicReceiver => _player?.MicReceiver;

        public event EventHandler<string>? OnClientConnected;
        public event EventHandler<string>? OnClientDisconnected;
        public event EventHandler? OnStopped;

        public AudioEngine() { }

        public AudioEngine(ServerConfig config) 
        {
            // The old AudioEngine could take a config in constructor.
            // We'll just store it or use it when starting server.
        }

        public void StartWasapiToAndroid(int port, string? deviceId = null, string? instanceName = null)
        {
            CurrentMode = StreamingMode.WasapiToAndroid;
            _server = new ServerEngine();
            _server.OnClientConnected += (s, e) => OnClientConnected?.Invoke(this, e);
            _server.OnClientDisconnected += (s, e) => OnClientDisconnected?.Invoke(this, e);
            _server.OnStopped += (s, e) => OnStopped?.Invoke(this, EventArgs.Empty);
            _server.StartWasapiToAndroid(port, deviceId, instanceName);
        }

        public async Task<bool> StartAndroidMicListenerAsync(string ip, int port, string? deviceId, string? deviceName = null)
        {
            CurrentMode = StreamingMode.AndroidMicListener;
            _player = new PlayerEngine();
            _player.OnClientConnected += (s, e) => OnClientConnected?.Invoke(this, e);
            _player.OnClientDisconnected += (s, e) => OnClientDisconnected?.Invoke(this, e);
            _player.OnStopped += (s, e) => OnStopped?.Invoke(this, EventArgs.Empty);
            return await _player.StartAndroidMicListenerAsync(ip, port, deviceId, deviceName);
        }

        public void Stop()
        {
            _server?.Stop();
            _player?.Stop();
        }

        public void Dispose()
        {
            _server?.Dispose();
            _player?.Dispose();
        }

        public static string[] GetLocalIPAddresses()
        {
            return PlayerEngine.GetLocalIPAddresses();
        }
    }
}
