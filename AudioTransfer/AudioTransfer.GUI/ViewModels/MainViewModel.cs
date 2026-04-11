using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading.Tasks;
using AudioTransfer.Core.Facade;
using AudioTransfer.Core.Logging;
using AudioTransfer.Core.Audio;
using System.Windows;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Linq;
using AudioTransfer.Core.Models;
using AudioTransfer.GUI.ViewModels.States;
using System.Diagnostics;
using System.Net;

namespace AudioTransfer.GUI.ViewModels
{
    public partial class MainViewModel : ObservableObject, IDisposable
    {
        private readonly IServerEngine _serverEngine;
        private readonly IPlayerEngine _playerEngine;
        private readonly CancellationTokenSource _statsCts = new();

        private IPlayerState _currentState = new DisconnectedState();
        public IPlayerState CurrentState => _currentState;

        public void ChangeState(IPlayerState newState)
        {
            CoreLogger.Instance.Log($"[MainViewModel] Changing state to: {newState.GetType().Name}");
            _currentState = newState;
            var app = System.Windows.Application.Current;
            if (app != null && app.Dispatcher != null)
            {
                app.Dispatcher.Invoke(() => {
                    _currentState.UpdateUi(this);
                });
            }
            else
            {
                // Fallback for unit testing where Application.Current is null
                _currentState.UpdateUi(this);
            }
        }

        public IPlayerEngine PlayerEngine => _playerEngine;

        public void NotifyUser(string message, string title)
        {
            RequestShowNotification?.Invoke(this, (message, title));
        }

        public void TransitionToStatsView()
        {
            RequestTransitionToStats?.Invoke(this, EventArgs.Empty);
        }

        public void TransitionToConnectView()
        {
            RequestTransitionToConnect?.Invoke(this, EventArgs.Empty);
        }

        public void SuppressAutoConnect(TimeSpan duration)
        {
            _autoConnectSuppressedUntil = DateTime.Now.Add(duration);
        }

        [ObservableProperty]
        private PlayerConfig _config = new PlayerConfig();

        [ObservableProperty]
        private string _appStatusText = "Ready";

        // View State Properties
        [ObservableProperty]
        private string _hostName = "Loading...";

        [ObservableProperty]
        private string _localIps = "";

        [ObservableProperty]
        private string _targetIp = "";

        [ObservableProperty]
        private string _targetPort = "5003";

        // Lists for UI ComboBoxes
        public ObservableCollection<DeviceItem> CaptureDevices { get; } = new ObservableCollection<DeviceItem>();
        public ObservableCollection<DeviceItem> OutputDevices { get; } = new ObservableCollection<DeviceItem>();

        [ObservableProperty]
        private DeviceItem? _selectedCaptureDevice;

        [ObservableProperty]
        private DeviceItem? _selectedOutputDevice;

        public ObservableCollection<DiscoveredServiceItem> DiscoveredServers { get; } = new ObservableCollection<DiscoveredServiceItem>();
        public ObservableCollection<ConnectedClientItem> ConnectedClients { get; } = new ObservableCollection<ConnectedClientItem>();

        [ObservableProperty]
        private string _playerStatusText = "CONNECT";

        [ObservableProperty]
        private bool _isPlayerRunning = false;

        [ObservableProperty]
        private bool _isPlayerConnecting = false;

        [ObservableProperty]
        private string _playerStatusColor = "WarningColor"; // Resource name

        [ObservableProperty]
        private string _playerStatusIcon = "⏳";

        [ObservableProperty]
        private bool _isStatsVisible = false;

        [ObservableProperty]
        private string _activeDeviceName = "";

        [ObservableProperty]
        private string _activeDeviceIp = "";

        [ObservableProperty]
        private bool _isClientConnected = false;

        [ObservableProperty]
        private string _connectTime = "00:00";

        [ObservableProperty]
        private string _bufferDelay = "Waiting...";

        [ObservableProperty]
        private string _averageLatency = "—";

        [ObservableProperty]
        private string _maxLatency = "—";

        private DateTime _autoConnectSuppressedUntil = DateTime.MinValue;
        private bool _isAutoConnecting = false;
        private bool _isInitializing = false;

        [ObservableProperty]
        private bool _isServerRunning = false;

        [ObservableProperty]
        private double _serverCpuLoad = 0;

        [ObservableProperty]
        private string _serverBandwidth = "0 Kbps";

        [ObservableProperty]
        private string _serverTotalData = "0 MB";

        [ObservableProperty]
        private string _serverProcessingHealth = "Stable";

        [ObservableProperty]
        private string _serverCpuLoadText = "0%";

        [ObservableProperty]
        private string _serverButtonText = "START SERVER";

        [ObservableProperty]
        private string _clientListPlaceholder = "SERVER OFFLINE";

        [ObservableProperty]
        private bool _isSearchingServers;

        public ObservableCollection<LogMessageViewModel> SystemLogs { get; } = new();

        [ObservableProperty]
        private bool _isLogsPaused;

        [ObservableProperty]
        private double _currentVolumeLevel = -100;

        public bool IsAboveThreshold => CurrentVolumeLevel >= VadThreshold;

        public bool VadEnabled
        {
            get => Config.VadEnabled;
            set
            {
                if (Config.VadEnabled != value)
                {
                    Config.VadEnabled = value;
                    _playerEngine.VadEnabled = value;
                    OnPropertyChanged();
                    if (!_isInitializing) _ = _configRepository.SaveAsync(Config);
                }
            }
        }

        public double VadThreshold
        {
            get => Config.VadThreshold;
            set
            {
                if (Math.Abs(Config.VadThreshold - value) > 0.1)
                {
                    Config.VadThreshold = value;
                    _playerEngine.VadThreshold = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsAboveThreshold));
                    if (!_isInitializing) _ = _configRepository.SaveAsync(Config);
                }
            }
        }

        public event EventHandler<(string Message, string Title)>? RequestShowNotification;
        public event EventHandler? RequestTransitionToStats;
        public event EventHandler? RequestTransitionToConnect;
        public event EventHandler? RequestRefreshDiscovery;
        
        private readonly IConfigRepository<PlayerConfig> _configRepository;

        public MainViewModel(
            IServerEngine serverEngine, 
            IPlayerEngine playerEngine, 
            IConfigRepository<PlayerConfig> configRepository)
        {
            _serverEngine = serverEngine;
            _playerEngine = playerEngine;
            _configRepository = configRepository;
            
            _ = StartStatsTimer();

            // Connect to core events to update properties
            _serverEngine.OnClientConnected += (s, data) => 
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => {
                    if (data.IpAddress != null && !ConnectedClients.Any(x => x.IpAddress == data.IpAddress))
                    {
                        ConnectedClients.Add(new ConnectedClientItem { IpAddress = data.IpAddress, DeviceType = data.DeviceName });
                    }
                    IsClientConnected = ConnectedClients.Count > 0;
                    UpdateAppStatusText(data.IpAddress);
                });
            };
            
            _serverEngine.OnClientDisconnected += (s, ip) => 
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => {
                    if (ip != null)
                    {
                        var item = ConnectedClients.FirstOrDefault(x => x.IpAddress == ip);
                        if (item != null) ConnectedClients.Remove(item);
                    }
                    IsClientConnected = ConnectedClients.Count > 0;
                    UpdateAppStatusText(ConnectedClients.Count > 0 ? ConnectedClients.Last().IpAddress : null);
                });
            };

            _playerEngine.OnClientConnected += (s, ip) => 
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => {
                    //do nothing for now
                });
            };

            _playerEngine.OnClientDisconnected += (s, ip) => 
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => {
                    //do nothing for now
                });
            };

            _playerEngine.OnStopped += (s, e) => 
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => {
                    // Only trigger the "back to connect view" animation if we're actually in stats view
                    // and not already in a stopping state (which handles its own transition)
                    if (IsStatsVisible && !(_currentState is StoppingState))
                    {
                        TransitionToConnectView();
                    }

                    IsPlayerRunning = false;
                    IsPlayerConnecting = false;
                    PlayerStatusText = "CONNECT";
                    IsStatsVisible = false;

                    // If we're not already in a disconnected or stopping state, sync the state machine
                    if (!(_currentState is DisconnectedState) && !(_currentState is StoppingState))
                    {
                        ChangeState(new DisconnectedState());
                    }
                });
            };

            _playerEngine.OnVolumeUpdate += (s, db) => 
            {
                // Gate noise floor: anything below -70dB is silence
                double gated = db < -70 ? -100 : db;
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() => {
                    CurrentVolumeLevel = gated;
                    OnPropertyChanged(nameof(IsAboveThreshold));
                });
            };

            CoreLogger.Instance.LogEvent += (s, e) => 
            {
                if (IsLogsPaused) return;
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() => {
                    AddFormattedLog(e.LogMessage);
                });
            };

            // Pre-load recent logs
            var recent = CoreLogger.Instance.GetRecentLogs(100);
            foreach (var log in recent)
            {
                AddFormattedLog(log);
            }
        }

        private void AddFormattedLog(LogMessage log)
        {
            var vm = new LogMessageViewModel 
            { 
                Time = $"[{log.Timestamp:HH:mm:ss}] ",
                Level = $"[{log.Level.ToString().ToUpper()}] ",
                Message = log.Message,
                Color = GetLogBrush(log.Level),
                LogLevel = log.Level
            };
            SystemLogs.Add(vm);
            if (SystemLogs.Count > 500) SystemLogs.RemoveAt(0);
        }

        private string GetLogBrush(LogLevel level)
        {
            bool dark = IsDarkTheme;
            return level switch
            {
                LogLevel.Error   => dark ? "#FF5252" : "#C62828",
                LogLevel.Warning => dark ? "#FFB74D" : "#E56800",
                LogLevel.Debug   => dark ? "#A3C9FF" : "#1A5276",
                _                => dark ? "#C0C7D4" : "#3A4050"
            };
        }

        private void RefreshLogColors()
        {
            foreach (var log in SystemLogs)
            {
                log.Color = GetLogBrush(log.LogLevel);
            }
        }

        private async Task StartStatsTimer()
        {
            try
            {
                while (!_statsCts.Token.IsCancellationRequested)
                {
                    UpdateStats();
                    await Task.Delay(1000, _statsCts.Token);
                }
            }
            catch (TaskCanceledException) { }
        }

        private void UpdateStats()
        {
            // 1. Update Player Stats (Client Side)
            if (_playerEngine.IsRunning)
            {
                var elapsed = DateTime.UtcNow - _playerEngine.StartTime;
                ConnectTime = $"{(int)elapsed.TotalMinutes:D2}:{(int)elapsed.Seconds:D2}";

                var jitterStats = _playerEngine.MicJitter?.GetStatistics();
                if (jitterStats != null)
                {
                    BufferDelay = $"{jitterStats.DelayMs} ms";
                    AverageLatency = $"{jitterStats.LastTransitDelay} ms";
                    MaxLatency = $"{jitterStats.LossRate:F1}% Loss";
                }
                else
                {
                    BufferDelay = "Waiting...";
                    AverageLatency = "—";
                    MaxLatency = "—";
                }
            }
            else
            {
                ConnectTime = "00:00";
                BufferDelay = "—";
                AverageLatency = "—";
                MaxLatency = "—";
            }

            // 2. Update Server Stats (Host Side)
            if (_serverEngine.IsRunning && _serverEngine is ServerEngine engine)
            {
                var stats = engine.Stats;
                var snapshot = stats.TakeRateSnapshot();
                
                // Bandwidth
                if (snapshot.KbitsPerSec > 1000)
                    ServerBandwidth = $"{(snapshot.KbitsPerSec / 1000.0):F2} Mbps";
                else
                    ServerBandwidth = $"{snapshot.KbitsPerSec:F0} Kbps";

                // Total Data
                double totalMb = stats.BytesSent / (1024.0 * 1024.0);
                if (totalMb > 1024)
                    ServerTotalData = $"{(totalMb / 1024.0):F2} GB";
                else
                    ServerTotalData = $"{totalMb:F1} MB";

                // CPU Load (Simulated/Simplified for the process)
                UpdateServerCpuUsage();

                // Logic for health text
                ServerProcessingHealth = stats.ProcessingErrors > 0 ? "Errors Detected" : "Optimized";
            }
            else
            {
                ServerBandwidth = "0 Kbps";
                ServerCpuLoad = 0;
                ServerCpuLoadText = "0%";
            }
            UpdateClientListPlaceholder();
        }

        private DateTime _lastCpuTime = DateTime.MinValue;
        private TimeSpan _lastProcessorTime;
        private void UpdateServerCpuUsage()
        {
            try
            {
                var currentProcess = Process.GetCurrentProcess();
                var now = DateTime.UtcNow;

                if (_lastCpuTime != DateTime.MinValue)
                {
                    var elapsedBase = (now - _lastCpuTime).TotalMilliseconds * Environment.ProcessorCount;
                    var elapsedProcessor = (currentProcess.TotalProcessorTime - _lastProcessorTime).TotalMilliseconds;
                    
                    if (elapsedBase > 0)
                    {
                        double usage = (elapsedProcessor / elapsedBase) * 100.0;
                        ServerCpuLoad = Math.Min(100, Math.Max(0, usage));
                        ServerCpuLoadText = $"{ServerCpuLoad:F0}%";
                    }
                }

                _lastCpuTime = now;
                _lastProcessorTime = currentProcess.TotalProcessorTime;
            }
            catch { /* Ignore process access errors */ }
        }

        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _statsCts.Cancel(); } catch (ObjectDisposedException) { }
            _statsCts.Dispose();
        }

        public async Task SaveConfigAsync()
        {
            if (!_isInitializing)
            {
                await _configRepository.SaveAsync(Config);
            }
        }

        public async Task InitAsync()
        {
            _isInitializing = true;
            try 
            {
                // 0. Load Config
                Config = await _configRepository.LoadOrDefaultAsync();
                
                // Init IPs (Host/Target are handled by OnConfigChanged)
                LocalIps = string.Join("  •  ", ServerEngine.GetLocalIPAddresses());

            // Put a placeholder FIRST so the ComboBox is never completely empty
            // (An empty ComboBox causes a 0-height invisible popup that locks UI focus)
            CaptureDevices.Clear();
            CaptureDevices.Add(new DeviceItem { Id = string.Empty, Name = "Gathering Devices..." });
            SelectedCaptureDevice = CaptureDevices[0];

            var captures = App.CaptureDevicesTask != null ? await App.CaptureDevicesTask : await Task.Run(() => WasapiTimedCapture.GetRenderDevices());

            CaptureDevices.Clear();
            CaptureDevices.Add(new DeviceItem { Id = string.Empty, Name = "System Default Device" });
            foreach (var dev in captures)
            {
                CaptureDevices.Add(new DeviceItem { Id = dev.Id, Name = dev.Name });
            }

            if (!string.IsNullOrEmpty(Config.LastServerDeviceId))
            {
                SelectedCaptureDevice = CaptureDevices.FirstOrDefault(x => x.Id == Config.LastServerDeviceId) ?? CaptureDevices[0];
            }
            else
            {
                SelectedCaptureDevice = CaptureDevices[0];
            }

            OutputDevices.Clear();
            OutputDevices.Add(new DeviceItem { Id = string.Empty, Name = "Gathering Devices..." });
            SelectedOutputDevice = OutputDevices[0];

            var renders = App.RenderDevicesTask != null ? await App.RenderDevicesTask : await Task.Run(() => WasapiPlayer.GetRenderDeviceList());

            OutputDevices.Clear();
            OutputDevices.Add(new DeviceItem { Id = string.Empty, Name = "System Default Device" });
            foreach(var dev in renders)
            {
                OutputDevices.Add(new DeviceItem { Id = dev.Id, Name = dev.Name });
            }

            // Fallback rules: prefer CABLE Input if saved ID doesn't exist or is empty
            if (string.IsNullOrEmpty(Config.LastPlayerDeviceId))
            {
                var cable = OutputDevices.FirstOrDefault(x => x.Name.Contains("CABLE Input", System.StringComparison.OrdinalIgnoreCase));
                SelectedOutputDevice = cable ?? OutputDevices[0];
            }
            else
            {
                SelectedOutputDevice = OutputDevices.FirstOrDefault(x => x.Id == Config.LastPlayerDeviceId) ?? OutputDevices[0];
            }
                // Final sync for app status
                UpdateAppStatusText(null);
                ServerButtonText = LanguageManager.Instance.GetString("BtnStartServer");
                UpdateClientListPlaceholder();
            }
            finally
            {
                _isInitializing = false;
            }
        }

        partial void OnSelectedCaptureDeviceChanged(DeviceItem? value)
        {
            if (value != null)
            {
                Config.LastServerDeviceId = value.Id;
                if (!_isInitializing) _ = _configRepository.SaveAsync(Config);

                // If server is running, switch device live
                if (_serverEngine.IsRunning)
                {
                    _ = _serverEngine.SwitchCaptureDevice(value.Id);
                    AppStatusText = $"Switched to {value.Name}";
                }
            }
        }

        partial void OnSelectedOutputDeviceChanged(DeviceItem? value)
        {
            if (value != null)
            {
                Config.LastPlayerDeviceId = value.Id;
                if (!_isInitializing) _ = _configRepository.SaveAsync(Config);

                // If player is running, tell it to change output device 
                // (PlayerEngine might need a Stop/Start or a live update)
                if (IsPlayerRunning)
                {
                    // For now, let's keep it simple: the next connection will use it.
                    // If we want it to apply IMMEDIATELY to a running connection, 
                    // we'd need a specific method in PlayerEngine.
                    // But usually, changing output device requires a restart of the stream.
                    // For now, let's just log it.
                    CoreLogger.Instance.Log($"Output device changed to {value.Name}. Will apply on next connection or manual restart.");
                }
                
                OnPropertyChanged(nameof(SelectedOutputDevice)); // Ensure UI updates if needed
            }
        }

        // Example Command to start future refactoring
        [RelayCommand]
        public void StopAll()
        {
            if (_serverEngine.IsRunning)
                _serverEngine.Stop();
                
            if (IsPlayerRunning)
            {
                _ = ToggleConnectPlayer();
            }
        }

        // Settings events for MainWindow to react to
        public event EventHandler<bool>? RequestApplyTheme;
        public event EventHandler<string>? RequestApplyLanguage;
        public event EventHandler<bool>? RequestUpdateStartup;

        partial void OnConfigChanged(PlayerConfig value)
        {
            if (value == null) return;
            
            // 1. Sync field-backed properties
            TargetIp = value.LastConnectedIp;
            TargetPort = value.LastConnectedPort.ToString();
            HostName = value.DeviceName;
            
            // 2. Notify manual properties that wrap Config
            OnPropertyChanged(nameof(DeviceName));
            OnPropertyChanged(nameof(AutoMute));
            OnPropertyChanged(nameof(Language));
            OnPropertyChanged(nameof(LanguageIndex));
            OnPropertyChanged(nameof(IsDarkTheme));
            OnPropertyChanged(nameof(LaunchOnStartup));
            OnPropertyChanged(nameof(StartMinimized));
            OnPropertyChanged(nameof(AutoConnect));
            OnPropertyChanged(nameof(MinimizeToTray));
            OnPropertyChanged(nameof(VadEnabled));
            OnPropertyChanged(nameof(VadThreshold));
            OnPropertyChanged(nameof(IsAboveThreshold));

            _playerEngine.VadEnabled = value.VadEnabled;
            _playerEngine.VadThreshold = value.VadThreshold;
            
            // 3. Trigger events
            RequestApplyTheme?.Invoke(this, value.IsDarkTheme);
            RequestApplyLanguage?.Invoke(this, value.Language);
            RequestUpdateStartup?.Invoke(this, value.LaunchOnStartup);
        }

        partial void OnTargetIpChanged(string value)
        {
            Config.LastConnectedIp = value;
            if (!_isInitializing) _ = _configRepository.SaveAsync(Config);
        }

        partial void OnTargetPortChanged(string value)
        {
            if (int.TryParse(value, out int port))
            {
                Config.LastConnectedPort = port;
                if (!_isInitializing) _ = _configRepository.SaveAsync(Config);
            }
        }

        public bool IsDarkTheme
        {
            get => Config.IsDarkTheme;
            set
            {
                if (Config.IsDarkTheme != value)
                {
                    Config.IsDarkTheme = value;
                    OnPropertyChanged();
                    RequestApplyTheme?.Invoke(this, value);
                    RefreshLogColors();
                    UpdateAppStatusText(IsClientConnected ? ActiveDeviceIp : null);
                    if (!_isInitializing) _ = _configRepository.SaveAsync(Config);
                }
            }
        }

        public bool MinimizeToTray
        {
            get => Config.MinimizeToTray;
            set
            {
                if (Config.MinimizeToTray != value)
                {
                    Config.MinimizeToTray = value;
                    OnPropertyChanged();
                    if (!_isInitializing) _ = _configRepository.SaveAsync(Config);
                }
            }
        }

        public bool AutoMute
        {
            get => Config.AutoMute;
            set
            {
                if (Config.AutoMute != value)
                {
                    Config.AutoMute = value;
                    OnPropertyChanged();
                    _serverEngine.SetSystemMute(value);
                    if (!_isInitializing) _ = _configRepository.SaveAsync(Config);
                }
            }
        }

        public string DeviceName
        {
            get => Config.DeviceName;
            set
            {
                if (Config.DeviceName != value)
                {
                    Config.DeviceName = value;
                    OnPropertyChanged();
                    HostName = value;
                    if (!_isInitializing) _ = _configRepository.SaveAsync(Config);
                }
            }
        }

        public string Language
        {
            get => Config.Language;
            set
            {
                if (Config.Language != value)
                {
                    Config.Language = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(LanguageIndex));
                    UpdateAppStatusText(IsClientConnected ? ActiveDeviceIp : null);
                    RequestApplyLanguage?.Invoke(this, value);
                    if (!_isInitializing) _ = _configRepository.SaveAsync(Config);
                }
            }
        }

        /// <summary>
        /// Index-based property for the Language ComboBox binding.
        /// 0 = English, 1 = Vietnamese
        /// </summary>
        public int LanguageIndex
        {
            get => Language == "Vietnamese" ? 1 : 0;
            set
            {
                Language = value == 1 ? "Vietnamese" : "English";
            }
        }

        private void UpdateAppStatusText(string? connectedIp)
        {
            var lm = LanguageManager.Instance;
            if (string.IsNullOrEmpty(connectedIp))
            {
                AppStatusText = lm.GetString("StatusNoDevice");
            }
            else
            {
                ActiveDeviceIp = connectedIp;
                AppStatusText = string.Format(lm.GetString("StatusConnected"), connectedIp);
            }
            UpdateClientListPlaceholder();
        }

        private void UpdateClientListPlaceholder()
        {
            var lm = LanguageManager.Instance;
            if (!IsServerRunning)
            {
                ClientListPlaceholder = lm.GetString("StatusServerOffline");
            }
            else if (ConnectedClients.Count == 0)
            {
                ClientListPlaceholder = lm.GetString("StatusWaitingConnections");
            }
            else
            {
                ClientListPlaceholder = "";
            }
        }

        public bool LaunchOnStartup
        {
            get => Config.LaunchOnStartup;
            set
            {
                if (Config.LaunchOnStartup != value)
                {
                    Config.LaunchOnStartup = value;
                    OnPropertyChanged();
                    RequestUpdateStartup?.Invoke(this, value);
                    if (!_isInitializing) _ = _configRepository.SaveAsync(Config);
                }
            }
        }

        public bool StartMinimized
        {
            get => Config.StartMinimized;
            set
            {
                if (Config.StartMinimized != value)
                {
                    Config.StartMinimized = value;
                    OnPropertyChanged();
                    RequestUpdateStartup?.Invoke(this, LaunchOnStartup);
                    if (!_isInitializing) _ = _configRepository.SaveAsync(Config);
                }
            }
        }

        public bool AutoConnect
        {
            get => Config.AutoConnect;
            set
            {
                if (Config.AutoConnect != value)
                {
                    Config.AutoConnect = value;
                    OnPropertyChanged();
                    if (!_isInitializing) _ = _configRepository.SaveAsync(Config);
                    
                    if (value && !_isInitializing)
                    {
                        CheckAutoConnect();
                    }
                }
            }
        }

        private void CheckAutoConnect()
        {
            if (AutoConnect && !_isAutoConnecting && !_playerEngine.IsRunning)
            {
                if (DateTime.Now < _autoConnectSuppressedUntil) return;

                var lastIp = Config.LastConnectedIp;
                if (string.IsNullOrEmpty(lastIp)) return;

                var match = DiscoveredServers.FirstOrDefault(x => x.IpAddress == lastIp);

                if (match != null)
                {
                    CoreLogger.Instance.Log($"[MainViewModel] AutoConnecting to {match.Name} ({match.IpAddress})");
                    _isAutoConnecting = true;
                    TargetIp = match.IpAddress;
                    TargetPort = match.Port.ToString();
                    _ = ToggleConnectPlayer();
                    _isAutoConnecting = false;
                }
            }
        }


        [RelayCommand]
        private void ToggleLanguage()
        {
            Language = Language == "English" ? "Vietnamese" : "English";
        }

        public void AddDiscoveredServer(string name, string ip, int port)
        {
            if (DiscoveredServers.Any(s => s.IpAddress == ip)) return;
            System.Windows.Application.Current.Dispatcher.Invoke(() => {
                DiscoveredServers.Add(new DiscoveredServiceItem { Name = name, IpAddress = ip, Port = port });
            });
            
            // Auto Connect Logic
            CheckAutoConnect();
        }

        public void ClearDiscoveredServers()
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() => {
                DiscoveredServers.Clear();
            });
        }

        public void RemoveDiscoveredServer(string ip)
        {
            var item = DiscoveredServers.FirstOrDefault(x => x.IpAddress == ip);
            if (item != null)
            {
                DiscoveredServers.Remove(item);
            }
        }

        [RelayCommand]
        private async Task ToggleConnectPlayer()
        {
            await _currentState.HandleConnectToggleAsync(this);
        }


        [RelayCommand]
        private void ToggleServer()
        {
            var lm = LanguageManager.Instance;
            if (_serverEngine.IsRunning)
            {
                _serverEngine.Stop();
                IsServerRunning = false;
                AppStatusText = lm.GetString("StatusServerStopped");
                ServerButtonText = lm.GetString("BtnStartServer");
            }
            else
            {
                try
                {
                    _serverEngine.StartWasapiToAndroid(5000, SelectedCaptureDevice?.Id, DeviceName);
                    IsServerRunning = true;
                    AppStatusText = lm.GetString("StatusServerStarted");
                    ServerButtonText = lm.GetString("BtnStopServer");
                }
                catch (Exception ex)
                {
                    NotifyUser(string.Format(lm.GetString("ErrServerStart"), ex.Message), lm.GetString("ErrServerTitle"));
                }
            }
            UpdateClientListPlaceholder();
        }

        [RelayCommand]
        private async Task RefreshServers()
        {
            if (IsSearchingServers) return;
            IsSearchingServers = true;
            
            // Clear current list to see fresh results
            ClearDiscoveredServers();
            
            RequestRefreshDiscovery?.Invoke(this, EventArgs.Empty);
            
            // Allow 5 seconds of "Searching" status
            await Task.Delay(5000);
            IsSearchingServers = false;
        }

        [RelayCommand]
        private void ClearLogs()
        {
            SystemLogs.Clear();
            CoreLogger.Instance.Clear();
        }

        [RelayCommand]
        private void RefreshLogs()
        {
            SystemLogs.Clear();
            var recent = CoreLogger.Instance.GetRecentLogs(200);
            foreach (var log in recent)
            {
                AddFormattedLog(log);
            }
        }

        [RelayCommand]
        private void ExportLogs()
        {
            try
            {
                var sfd = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "Text Files (*.txt)|*.txt",
                    FileName = $"AudioTransfer_Logs_{DateTime.Now:yyyyMMdd_HHmm}.txt"
                };

                if (sfd.ShowDialog() == true)
                {
                    var content = string.Join("\n", SystemLogs.Select(l => $"{l.Time}{l.Level}{l.Message}"));
                    System.IO.File.WriteAllText(sfd.FileName, content);
                    RequestShowNotification?.Invoke(this, ("Logs exported successfully.", "Export"));
                }
            }
            catch (Exception ex)
            {
                RequestShowNotification?.Invoke(this, ($"Export failed: {ex.Message}", "Error"));
            }
        }

        [RelayCommand]
        private void TogglePauseLogs()
        {
            IsLogsPaused = !IsLogsPaused;
        }

        [RelayCommand]
        private void ConnectToService(DiscoveredServiceItem service)
        {
            if (service == null) return;
            TargetIp = service.IpAddress;
            TargetPort = "5003";
            _ = ToggleConnectPlayer();
        }
    }

    // Quick Data Class for Dropdowns
    public class DeviceItem
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        
        // For WPF ComboBox Display
        public override string ToString() => Name;
    }

    public class DiscoveredServiceItem
    {
        public string Name { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public int Port { get; set; } = 5003;
    }

    public class ConnectedClientItem
    {
        public string IpAddress { get; set; } = string.Empty;
        public string DeviceType { get; set; } = "Device";
    }

    public class LogMessageViewModel : ObservableObject
    {
        public string Time { get; set; } = string.Empty;
        public string Level { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public LogLevel LogLevel { get; set; } = LogLevel.Info;

        private string _color = "#C0C7D4";
        public string Color
        {
            get => _color;
            set => SetProperty(ref _color, value);
        }
    }
}

