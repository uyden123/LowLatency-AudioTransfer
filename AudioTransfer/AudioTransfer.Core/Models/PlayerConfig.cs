using System.Text.Json;
using System.IO;
using System.Threading.Tasks;

namespace AudioTransfer.Core.Models
{
    public sealed class PlayerConfig
    {
        public bool AutoConnect { get; set; } = false;
        public string LastConnectedIp { get; set; } = "";
        public int LastConnectedPort { get; set; } = 5003;

        // Display Settings
        public string DeviceName { get; set; } = Environment.MachineName.ToUpper();
        public bool IsDarkTheme { get; set; } = true;
        public bool MinimizeToTray { get; set; } = true;
        public string Language { get; set; } = "English";

        // Startup Settings
        public bool LaunchOnStartup { get; set; } = false;

        // Audio Settings
        public bool AutoMute { get; set; } = false;

        private const string DefaultConfigFile = "player_config.json";

        public static async Task<PlayerConfig> LoadOrDefaultAsync(string path = DefaultConfigFile)
        {
            if (File.Exists(path))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(path);
                    return JsonSerializer.Deserialize<PlayerConfig>(json) ?? new PlayerConfig();
                }
                catch
                {
                    return new PlayerConfig();
                }
            }
            return new PlayerConfig();
        }

        public async Task SaveAsync(string path = DefaultConfigFile)
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json);
        }
    }
}
