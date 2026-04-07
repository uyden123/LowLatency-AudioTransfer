using System;
using System.Net;
using System.Threading.Tasks;
using AudioTransfer.Core.Network;

namespace AudioTransfer.Core.Facade;

public interface IPlayerEngine : IDisposable
{
    bool IsRunning { get; }
    DateTime StartTime { get; }
    MicJitterBuffer? MicJitter { get; }
    UdpMicReceiver? MicReceiver { get; }
    bool VadEnabled { get; set; }
    double VadThreshold { get; set; }

    event EventHandler<string>? OnLog;
    event EventHandler<string>? OnClientConnected;
    event EventHandler<string>? OnClientDisconnected;
    event EventHandler? OnStopped;
    event EventHandler<double>? OnVolumeUpdate;

    void SetSystemMute(bool muted);
    Task<bool> StartAndroidMicListenerAsync(string androidIp, int androidPort = 5003, string? playbackDeviceId = null, string? deviceName = null);
    void Stop();
}
