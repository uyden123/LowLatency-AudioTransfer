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

        private const string DefaultConfigFile = "server_config.json";

        /// <summary>
        /// Load config from JSON file, or return default config if file doesn't exist.
        /// </summary>
        public static async Task<ServerConfig> LoadOrDefaultAsync(string path = DefaultConfigFile)
        {
            if (File.Exists(path))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(path);
                    return JsonSerializer.Deserialize<ServerConfig>(json) ?? new ServerConfig();
                }
                catch
                {
                    return new ServerConfig();
                }
            }
            return new ServerConfig();
        }

        /// <summary>
        /// Save config to JSON file.
        /// </summary>
        public async Task SaveAsync(string path = DefaultConfigFile)
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json);
        }
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
    }

    public sealed class OpusConfig
    {
        public bool Enabled { get; set; } = true;
        public int Bitrate { get; set; } = 128000;
        public int Complexity { get; set; } = 10;
        public int FrameSizeMs { get; set; } = 20;
        public bool EnableFEC { get; set; } = true;
        public bool EnableDTX { get; set; } = false;
    }
}
