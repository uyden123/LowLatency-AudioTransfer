using System;
using System.Threading;
using System.Threading.Tasks;
using AudioTransfer.Core.Audio;

namespace AudioTransfer.Core.Audio
{
    /// <summary>
    /// Manages the lifecycle of audio capture hardware (WASAPI).
    /// Handles device switching and automatic restarts on device changes.
    /// </summary>
    public class WasapiManager : IDisposable
    {
        private readonly IAudioCapture _capture;
        private readonly Action<string> _logger;
        private string? _currentDeviceId;
        private bool _isRunning;

        private readonly SemaphoreSlim _restartLock = new(1, 1);
        private DateTime _lastRestartTime = DateTime.MinValue;

        public delegate void DataAvailableHandler(IntPtr data, int length, bool isSilent);
        public event DataAvailableHandler? OnDataAvailable;

        public WasapiManager(IAudioCapture capture, Action<string> logger)
        {
            _capture = capture ?? throw new ArgumentNullException(nameof(capture));
            _logger = logger;
            
            _capture.OnDataAvailable = (data, len, silent) => OnDataAvailable?.Invoke(data, len, silent);
            _capture.DefaultDeviceChanged += async (s, e) => await RestartWasapiAsync("Default audio device change");
        }

        public void Start(string? deviceId = null)
        {
            _currentDeviceId = deviceId;
            _isRunning = true;
            _capture.Initialize(deviceId);
            _capture.Start();
            _logger?.Invoke($"[WasapiManager] Capture started on device: {deviceId ?? "Default"}");
        }

        public void Stop()
        {
            _isRunning = false;
            _capture.Stop();
            _logger?.Invoke("[WasapiManager] Capture stopped.");
        }

        public async Task SwitchDeviceAsync(string? deviceId)
        {
            _currentDeviceId = deviceId;
            _logger?.Invoke($"[WasapiManager] Switching device to: {deviceId ?? "Default"}");
            
            await Task.Run(() =>
            {
                try
                {
                    _capture.Stop();
                    _capture.Initialize(deviceId);
                    _capture.Start();
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"[WasapiManager] Failed to switch device: {ex.Message}");
                }
            });
        }

        private async Task RestartWasapiAsync(string reason)
        {
            if (!_isRunning) return;
            if ((DateTime.UtcNow - _lastRestartTime).TotalMilliseconds < 1000) return;
            
            _lastRestartTime = DateTime.UtcNow;
            _logger?.Invoke($"[WasapiManager] {reason} detected. Restarting WASAPI...");

            if (!await _restartLock.WaitAsync(0)) return;
            bool success = false;
            try
            {
                await Task.Delay(1500); // Wait for device to stabilize
                _capture.Stop();
                _capture.Initialize(_currentDeviceId);
                _capture.Start();
                _logger?.Invoke("[WasapiManager] WASAPI restarted successfully.");
                success = true;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[WasapiManager] Failed to restart WASAPI: {ex.Message}");
            }
            finally
            {
                _restartLock.Release();
                if (success) _lastRestartTime = DateTime.UtcNow;
            }
        }

        public void Dispose()
        {
            Stop();
            _restartLock.Dispose();
        }
    }
}
