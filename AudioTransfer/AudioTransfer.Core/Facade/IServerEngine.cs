using System;
using System.Threading.Tasks;

namespace AudioTransfer.Core.Facade;

public interface IServerEngine : IDisposable
{
    bool IsRunning { get; }

    event EventHandler<string>? OnLog;
    event EventHandler<string>? OnClientConnected;
    event EventHandler<string>? OnClientDisconnected;
    event EventHandler? OnStopped;

    void SetSystemMute(bool muted);
    void StartWasapiToAndroid(int listenPort, string? captureDeviceId = null, string? instanceName = null);
    Task SwitchCaptureDevice(string? deviceId);
    void Stop();
}
