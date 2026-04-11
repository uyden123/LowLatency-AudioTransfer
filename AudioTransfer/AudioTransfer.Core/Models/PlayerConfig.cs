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
        public bool StartMinimized { get; set; } = true;

        // Audio Settings
        public bool AutoMute { get; set; } = false;
        public string? LastServerDeviceId { get; set; }
        public string? LastPlayerDeviceId { get; set; }

        // VAD Settings
        public bool VadEnabled { get; set; } = false;
        public double VadThreshold { get; set; } = -45.0; // dB
    }
}
