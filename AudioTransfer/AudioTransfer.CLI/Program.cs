using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AudioTransfer.Core.Audio;
using AudioTransfer.Core.Facade;
using AudioTransfer.Core.Logging;
using AudioTransfer.Core.Models;

namespace AudioTransfer.CLI
{
    internal static class Program
    {
        // ANSI Color Codes
        private const string RESET   = "\x1b[0m";
        private const string BOLD    = "\x1b[1m";
        private const string DIM     = "\x1b[2m";
        private const string CYAN    = "\x1b[36m";
        private const string GREEN   = "\x1b[32m";
        private const string YELLOW  = "\x1b[33m";
        private const string RED     = "\x1b[31m";
        private const string MAGENTA = "\x1b[35m";
        private const string WHITE   = "\x1b[97m";
        private const string BLUE    = "\x1b[34m";
        private const string GRAY    = "\x1b[90m";

        private static readonly object ConsoleLock = new();
        private static readonly Queue<string> LogBuffer = new();
        private const int MAX_LOG_LINES = 12;

        public static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            EnableAnsiSupport();

            // Subscribe to global logs ONCE
            CoreLogger.Instance.LogEvent += (s, e) =>
            {
                string logStr = e.LogMessage.ToString();
                AddLog(logStr);
            };

            bool exitApp = false;
            while (!exitApp)
            {
                ClearScreen();
                PrintBanner();

                try
                {
                    var mode = SelectMode(out exitApp);
                    if (exitApp) break;

                    if (mode == AudioEngine.StreamingMode.AndroidMicListener)
                        await RunMode1_AndroidMic();
                    else if (mode == AudioEngine.StreamingMode.WasapiToAndroid)
                        await RunMode2_WasapiToAndroid();
                }
                catch (Exception ex)
                {
                    PrintError($"Fatal error: {ex.Message}");
                    Console.Error.WriteLine(ex.StackTrace);
                    await Task.Delay(2000);
                }
            }

            Console.WriteLine($"\n{DIM}  Exiting...{RESET}");
            await Task.Delay(500);
        }

        // ============================================================
        //  MODE SELECTION
        // ============================================================

        private static AudioEngine.StreamingMode SelectMode(out bool shouldExit)
        {
            shouldExit = false;
            Console.WriteLine();
            Console.WriteLine($"  {BOLD}+------------------------------------------------------+{RESET}");
            Console.WriteLine($"  {BOLD}|{RESET}  Select Streaming Mode                               {BOLD}|{RESET}");
            Console.WriteLine($"  {BOLD}+------------------------------------------------------+{RESET}");
            Console.WriteLine($"  {BOLD}|{RESET}  {GREEN}1{RESET}  Android Mic -> PC Driver                       {BOLD}|{RESET}");
            Console.WriteLine($"  {BOLD}|{RESET}  {CYAN}2{RESET}  WASAPI -> Android UDP {DIM}(default){RESET}                {BOLD}|{RESET}");
            Console.WriteLine($"  {BOLD}|{RESET}  {RED}X{RESET}  Exit Application                               {BOLD}|{RESET}");
            Console.WriteLine($"  {BOLD}+------------------------------------------------------+{RESET}");

            Console.Write($"\n  {CYAN}>{RESET} Choice [1-2, X]: ");
            var input = Console.ReadLine()?.Trim().ToUpper();

            if (input == "X")
            {
                shouldExit = true;
                return AudioEngine.StreamingMode.WasapiToAndroid;
            }

            return input switch
            {
                "1" => AudioEngine.StreamingMode.AndroidMicListener,
                _ => AudioEngine.StreamingMode.WasapiToAndroid,
            };
        }

        // ============================================================
        //  MODE 3: WASAPI -> Android (Low-Latency UDP)
        // ============================================================

        private static async Task RunMode2_WasapiToAndroid()
        {
            Console.Write($"\n  {CYAN}>{RESET} UDP listen port {DIM}(default 5000){RESET}: ");
            int port = PromptInt(5000, 1, 65535);

            var config = await ServerConfig.LoadOrDefaultAsync();
            using var engine = new AudioEngine(config);

            WireEngineEvents(engine);
            engine.StartWasapiToAndroid(port);

            ClearScreen();
            PrintModeHeader("WASAPI -> Android", "UDP Low-Latency", port);
            PrintLocalAddresses(port, "UDP");

            Console.WriteLine($"\n  {DIM}Working... Press 'q' + Enter to go back{RESET}\n");

            await RunDashboardLoop(engine);
        }

        // ============================================================
        //  MODE 2: Android Mic -> PC
        // ============================================================

        private static async Task RunMode1_AndroidMic()
        {
            Console.Write($"\n  {CYAN}>{RESET} Android device IP: ");
            string androidIp = Console.ReadLine()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(androidIp))
            {
                PrintError("No IP provided. Exiting.");
                return;
            }

            Console.Write($"  {CYAN}>{RESET} Android port {DIM}(default 5003){RESET}: ");
            int port = PromptInt(5003, 1, 65535);

            // List available playback devices
            Console.WriteLine($"\n  {BOLD}Available Playback Devices:{RESET}");
            var devices = WasapiPlayer.GetAvailableDevices();
            int autoSelectId = -1;
            foreach (var kv in devices)
            {
                bool isVBCable = kv.Value.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase);
                string highlight = isVBCable ? $"{GREEN}{BOLD}" : "";
                string suffix = isVBCable ? $" {DIM}(VB-Cable suggested){RESET}" : "";
                Console.WriteLine($"    {GREEN}{kv.Key}{RESET}: {highlight}{kv.Value}{RESET}{suffix}");
                if (isVBCable && autoSelectId == -1) autoSelectId = (int)kv.Key;
            }

            Console.Write($"\n  {CYAN}>{RESET} Select device ID {DIM}(default {(autoSelectId >= 0 ? autoSelectId : "0")}){RESET}: ");
            int deviceId = PromptInt(autoSelectId >= 0 ? autoSelectId : 0, 0, (int)devices.Keys.Max());

            using var engine = new AudioEngine();
            WireEngineEvents(engine);

            bool success = await engine.StartAndroidMicListenerAsync(androidIp, port, deviceId);
            if (!success)
            {
                PrintError($"Could not connect to {androidIp}:{port}. Ensure Android server is running.");
                await Task.Delay(2000);
                return;
            }

            ClearScreen();
            PrintModeHeader("Android Mic -> PC Driver", "UDP Receiver", port);
            Console.WriteLine($"  {BOLD}Output:{RESET} {devices[(uint)deviceId]}");

            Console.WriteLine($"\n  {DIM}Working... Press 'q' + Enter to go back{RESET}\n");

            await RunDashboardLoop(engine);
        }

        // ============================================================
        //  LIVE DASHBOARD
        // ============================================================

        private static async Task RunDashboardLoop(AudioEngine engine)
        {
            // Reset to top for a clean start
            ClearScreen();
            
            bool quit = false;
            _ = Task.Run(() =>
            {
                while (!quit)
                {
                    var line = Console.ReadLine();
                    if (string.Equals(line, "q", StringComparison.OrdinalIgnoreCase))
                    {
                        quit = true;
                        break;
                    }
                }
            });

            while (!quit && engine.IsRunning)
            {
                await Task.Delay(1000);
                if (quit) break;

                try
                {
                    lock (ConsoleLock)
                    {
                        Console.SetCursorPosition(0, 0);
                        RenderDashboard(engine);
                    }
                }
                catch { }
            }

            engine.Stop();
            Console.Clear();
            Console.WriteLine($"\n  {GREEN}[OK]{RESET} Engine stopped gracefully.");
        }

        private static void RenderDashboard(AudioEngine engine)
        {
            var snap = engine.Stats.GetSnapshot();
            var rates = engine.Stats.TakeRateSnapshot();
            var uptime = engine.Uptime;

            // Clear to EOL sequence
            const string EL = "\x1b[K";

            var sb = new StringBuilder();

            // Status Bar
            string statusColor = engine.IsRunning ? GREEN : RED;
            string statusText = engine.IsRunning ? "[RUNNING]" : "[STOPPED]";

            sb.AppendLine($"  {statusColor}{BOLD}{statusText}{RESET}  |  Uptime: {uptime:hh\\:mm\\:ss}  |  Mode: {CYAN}{engine.CurrentMode}{RESET}{EL}");
            sb.AppendLine($"{EL}");

            // Metrics Table
            sb.AppendLine($"  {BOLD}+==================+==================+==================+{RESET}{EL}");
            sb.AppendLine($"  {BOLD}|{RESET} {CYAN}Packets Sent{RESET}      {BOLD}|{RESET} {GREEN}Data Sent{RESET}        {BOLD}|{RESET} {YELLOW}Throughput{RESET}      {BOLD}|{RESET}{EL}");
            sb.AppendLine($"  {BOLD}|{RESET}  {WHITE}{snap.PacketsSent,14:N0}{RESET} {BOLD}  |{RESET}  {WHITE}{snap.BytesSentMB,12:F2} MB{RESET} {BOLD}|{RESET} {WHITE}{rates.KbitsPerSec,10:F0} kbps{RESET} {BOLD}|{RESET}{EL}");
            sb.AppendLine($"  {BOLD}+==================+==================+==================+{RESET}{EL}");
            sb.AppendLine($"  {BOLD}|{RESET} {BLUE}Buffers Recv{RESET}      {BOLD}|{RESET} {MAGENTA}Pkt/s{RESET}            {BOLD}|{RESET} {RED}Errors{RESET}          {BOLD}|{RESET}{EL}");
            sb.AppendLine($"  {BOLD}|{RESET}  {WHITE}{snap.BuffersReceived,14:N0}{RESET} {BOLD}  |{RESET}  {WHITE}{rates.PacketsPerSec,14:F1}{RESET} {BOLD} |{RESET}  {WHITE}{snap.ProcessingErrors + snap.RecordingErrors,14:N0}{RESET} {BOLD}|{RESET}{EL}");
            sb.AppendLine($"  {BOLD}+==================+==================+==================+{RESET}{EL}");

            // Connection Info
            sb.AppendLine($"{EL}");
            if (engine.CurrentMode == AudioEngine.StreamingMode.WasapiToAndroid)
            {
                var client = engine.ConnectedClient;
                string clientStr = client != null
                    ? $"{GREEN}{client}{RESET}"
                    : $"{RED}{BOLD}[DISCONNECTED]{RESET} {DIM}waiting for SUBSCRIBE/HEARTBEAT...{RESET}";
                sb.AppendLine($"  {BOLD}Client:{RESET} {clientStr}{EL}");

                if (engine.CircularBufferCapacity > 0)
                {
                    int bufMs = engine.CircularBufferAvailable * 1000 / (48000 * 4);
                    int capMs = engine.CircularBufferCapacity * 1000 / (48000 * 4);
                    double pct = (double)engine.CircularBufferAvailable / engine.CircularBufferCapacity * 100;
                    string barColor = pct > 80 ? RED : pct > 50 ? YELLOW : GREEN;
                    string bar = RenderBar(pct, 20);
                    sb.AppendLine($"  {BOLD}Buffer:{RESET} {barColor}{bar}{RESET} {bufMs}ms / {capMs}ms ({pct:F0}%){EL}");
                }
            }
            else if (engine.CurrentMode == AudioEngine.StreamingMode.WasapiBroadcast)
            {
                sb.AppendLine($"  {BOLD}Subscribers:{RESET} {CYAN}{snap.ActiveSubscribers}{RESET} active / {snap.TotalSubscribers} total{EL}");
                foreach (var kv in engine.Subscribers.OrderBy(k => k.Key.ToString()).Take(3))
                {
                    var info = kv.Value;
                    sb.AppendLine($"    {GREEN}*{RESET} {kv.Key,-22} uptime={info.Uptime:hh\\:mm\\:ss}  pkts={info.PacketsSent:N0}  {info.BytesSent / 1024:N0}KB{EL}");
                }
            }
            else if (engine.CurrentMode == AudioEngine.StreamingMode.AndroidMicListener)
            {
                var mic = engine.MicReceiver;
                if (mic != null)
                {
                    string connColor = mic.IsConnected ? GREEN : RED;
                    string connText = mic.IsConnected ? "[Connected]" : "[Disconnected]";
                    sb.AppendLine($"  {BOLD}Android:{RESET} {connColor}{connText}{RESET}  pkts={mic.PacketsReceived:N0}  bytes={mic.BytesReceived / 1024:N0}KB{EL}");
                }
            }

            // Log Window
            sb.AppendLine($"{EL}");
            sb.AppendLine($"  {DIM}--- Recent Log (q+Enter to quit) ------------------------{RESET}{EL}");
            string[] logs;
            lock (LogBuffer)
            {
                logs = LogBuffer.ToArray();
            }

            int logLinesToShow = Math.Min(logs.Length, MAX_LOG_LINES);
            int consoleWidth = Math.Max(Console.WindowWidth - 10, 40);

            for (int i = logs.Length - logLinesToShow; i < logs.Length; i++)
            {
                string log = logs[i];
                // Truncate long logs to prevent wrapping
                if (log.Length > consoleWidth) log = log.Substring(0, consoleWidth - 3) + "...";
                
                if (log.Contains("[ERROR]") || log.Contains("[Error]"))
                    sb.AppendLine($"  {RED}{log}{RESET}{EL}");
                else if (log.Contains("[WARN]") || log.Contains("[Warning]"))
                    sb.AppendLine($"  {YELLOW}{log}{RESET}{EL}");
                else
                    sb.AppendLine($"  {DIM}{log}{RESET}{EL}");
            }

            // Pad with empty lines
            for (int i = logLinesToShow; i < MAX_LOG_LINES; i++)
                sb.AppendLine($"{EL}");

            sb.AppendLine($"  {DIM}---------------------------------------------------------{RESET}{EL}");
            
            // Clear any old lines below the dashboard if the console was taller
            sb.Append($"{EL}");

            Console.Write(sb.ToString());
        }

        private static string RenderBar(double percent, int width)
        {
            int filled = (int)(percent / 100.0 * width);
            filled = Math.Clamp(filled, 0, width);
            return new string('#', filled) + new string('.', width - filled);
        }

        // ============================================================
        //  ENGINE EVENTS
        // ============================================================

        private static void WireEngineEvents(AudioEngine engine)
        {
            // Clear buffer when switching modes
            lock (LogBuffer)
            {
                LogBuffer.Clear();
            }

            engine.OnClientConnected += (s, ep) => AddLog($"[+] Client connected: {ep}");
            engine.OnClientDisconnected += (s, ep) => AddLog($"[-] Client disconnected: {ep}");
            engine.OnStopped += (s, e) => AddLog("Engine stopped");
        }

        private static void AddLog(string message)
        {
            string timestamped = $"[{DateTime.Now:HH:mm:ss}] {message}";
            lock (LogBuffer)
            {
                LogBuffer.Enqueue(timestamped);
                while (LogBuffer.Count > MAX_LOG_LINES * 2)
                    LogBuffer.Dequeue();
            }
        }

        // ============================================================
        //  UI HELPERS
        // ============================================================

        private static void PrintBanner()
        {
            Console.WriteLine();
            Console.WriteLine($"  {CYAN}{BOLD}+===================================================+{RESET}");
            Console.WriteLine($"  {CYAN}{BOLD}|{RESET}                                                   {CYAN}{BOLD}|{RESET}");
            Console.WriteLine($"  {CYAN}{BOLD}|{RESET}   {WHITE}{BOLD}AudioOverLAN Server  v3.0{RESET}                      {CYAN}{BOLD}|{RESET}");
            Console.WriteLine($"  {CYAN}{BOLD}|{RESET}   {DIM}WASAPI Capture | Opus | Low-Latency UDP{RESET}        {CYAN}{BOLD}|{RESET}");
            Console.WriteLine($"  {CYAN}{BOLD}|{RESET}                                                   {CYAN}{BOLD}|{RESET}");
            Console.WriteLine($"  {CYAN}{BOLD}+===================================================+{RESET}");
        }

        private static void PrintModeHeader(string modeName, string subtitle, int port)
        {
            PrintBanner();
            Console.WriteLine();
            Console.WriteLine($"  {BOLD}Mode:{RESET}  {GREEN}{modeName}{RESET}");
            Console.WriteLine($"  {BOLD}Proto:{RESET} {subtitle}");
            Console.WriteLine($"  {BOLD}Port:{RESET}  {CYAN}{port}{RESET}");
        }

        private static void PrintLocalAddresses(int port, string proto)
        {
            Console.WriteLine();
            Console.WriteLine($"  {BOLD}Listen Addresses:{RESET}");
            var addrs = AudioEngine.GetLocalIPAddresses();
            foreach (var addr in addrs)
                Console.WriteLine($"    {GREEN}>{RESET} {addr}:{port} ({proto})");
        }

        private static void PrintError(string message)
        {
            Console.WriteLine($"\n  {RED}{BOLD}[X]{RESET} {RED}{message}{RESET}");
        }

        private static int PromptInt(int defaultValue, int min = int.MinValue, int max = int.MaxValue)
        {
            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(input)) return defaultValue;
            if (int.TryParse(input, out int val) && val >= min && val <= max) return val;
            return defaultValue;
        }

        private static void ClearScreen()
        {
            try { Console.Clear(); } catch { }
        }

        private static void EnableAnsiSupport()
        {
            try
            {
                var handle = GetStdHandle(-11);
                GetConsoleMode(handle, out uint mode);
                SetConsoleMode(handle, mode | 0x0004);
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int handle);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool SetConsoleMode(IntPtr handle, uint mode);
    }
}
