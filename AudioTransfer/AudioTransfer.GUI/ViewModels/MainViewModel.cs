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

        public event EventHandler<(string Message, string Title)>? RequestShowNotification;
        public event EventHandler? RequestTransitionToStats;
        public event EventHandler? RequestTransitionToConnect;
        
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
            _serverEngine.OnClientConnected += (s, ip) => 
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => {
                    IsClientConnected = true;
                    UpdateAppStatusText(ip);
                });
            };
            
            _serverEngine.OnClientDisconnected += (s, ip) => 
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => {
                    IsClientConnected = false;
                    UpdateAppStatusText(null);
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

            // Load Capture Devices
            CaptureDevices.Clear();
            CaptureDevices.Add(new DeviceItem { Id = string.Empty, Name = "System Default Device" });
            
            var captures = WasapiTimedCapture.GetRenderDevices();
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

            // Load Output/Render Devices
            OutputDevices.Clear();
            OutputDevices.Add(new DeviceItem { Id = string.Empty, Name = "System Default Device" });

            var renders = WasapiPlayer.GetRenderDeviceList();
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
            OnPropertyChanged(nameof(IsDarkTheme));
            OnPropertyChanged(nameof(LaunchOnStartup));
            OnPropertyChanged(nameof(AutoConnect));
            OnPropertyChanged(nameof(MinimizeToTray));
            
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
                    UpdateAppStatusText(IsClientConnected ? ActiveDeviceIp : null);
                    RequestApplyLanguage?.Invoke(this, value);
                    if (!_isInitializing) _ = _configRepository.SaveAsync(Config);
                }
            }
        }

        private void UpdateAppStatusText(string? connectedIp)
        {
            bool isVi = Language == "Vietnamese";
            if (string.IsNullOrEmpty(connectedIp))
            {
                AppStatusText = isVi ? "Chưa có thiết bị nào kết nối" : "No connected device yet";
            }
            else
            {
                ActiveDeviceIp = connectedIp;
                AppStatusText = isVi ? $"Đã kết nối qua mạng: {connectedIp}" : $"Connected over Network: {connectedIp}";
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
            CoreLogger.Instance.Log($"[MainViewModel] AddDiscoveredServer: {name}, {ip}, {port}");  
            if (DiscoveredServers.Any(x => x.IpAddress == ip)) return;
            
            var item = new DiscoveredServiceItem { Name = name, IpAddress = ip, Port = port };
            DiscoveredServers.Add(item);

            // Auto Connect Logic
            CheckAutoConnect();
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
}

