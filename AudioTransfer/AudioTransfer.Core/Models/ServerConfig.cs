using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioTransfer.Core.Models
{
    /// <summary>
    /// Top-level server configuration, serializable to/from JSON.
    /// </summary>
    public sealed class ServerConfig
    {
        public NetworkConfig Network { get; set; } = new();
        public AudioStreamConfig Audio { get; set; } = new();
        public OpusConfig Opus { get; set; } = new();

    }

    public sealed class NetworkConfig
    {
        public int Port { get; set; } = 5000;
        public int MaxSubscribers { get; set; } = 10;
        public int SubscriberTimeoutMinutes { get; set; } = 2;
    }

    public sealed class AudioStreamConfig
    {
        public int SampleRate { get; set; } = 48000;
        public int Channels { get; set; } = 2;
        public int BitDepth { get; set; } = 16;
        public CaptureMode Mode { get; set; } = CaptureMode.WASAPI;
    }

    public enum CaptureMode
    {
        WASAPI,
        ASIO
    }

    public sealed class OpusConfig
    {
        public bool Enabled { get; set; } = true;
        public int Bitrate { get; set; } = 128000;
        public int Complexity { get; set; } = 10;
        public double FrameSizeMs { get; set; } = 10f;
        public bool EnableFEC { get; set; } = true;
        public bool EnableDTX { get; set; } = false;
    }
}
