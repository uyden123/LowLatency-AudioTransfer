using System;

namespace AudioTransfer.Core.Audio
{
    /// <summary>
    /// Common interface for audio capture engines (WASAPI, ASIO, etc.)
    /// </summary>
    public interface IAudioCapture : IDisposable
    {
        /// <summary>
        /// Delegate for zero-latency direct audio capture.
        /// </summary>
        delegate void AudioDataAvailableDelegate(IntPtr data, int length, bool isSilent);

        /// <summary>
        /// When set, captured audio is pushed directly to this callback.
        /// </summary>
        AudioDataAvailableDelegate? OnDataAvailable { get; set; }

        /// <summary>
        /// Current capture format.
        /// </summary>
        AudioFormat Format { get; }

        /// <summary>
        /// Event fired when the default device changes or the stream needs restart.
        /// </summary>
        event EventHandler? DefaultDeviceChanged;

        /// <summary>
        /// Initialize the capture hardware.
        /// </summary>
        /// <param name="deviceId">Optional device identifier.</param>
        void Initialize(string? deviceId = null);

        /// <summary>
        /// Start capturing.
        /// </summary>
        void Start();

        /// <summary>
        /// Stop capturing.
        /// </summary>
        void Stop();
    }
}
