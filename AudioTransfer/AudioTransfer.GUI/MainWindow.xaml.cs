using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AudioTransfer.Core.Audio;
using AudioTransfer.Core.Facade;
using AudioTransfer.Core.Logging;
using AudioTransfer.Core.Models;
using System.Net;
using System.Windows.Media;
using AudioTransfer.Core.Network;
using System.Collections.Concurrent;
using System.Linq;
using AudioTransfer.Core.Plugins;
using Microsoft.Win32;
using System.Drawing;
using System.Windows.Forms;

namespace AudioTransfer.GUI
{
    public partial class MainWindow : Window
    {
        private AudioEngine? _serverEngine;
        private AudioEngine? _playerEngine;
        private DispatcherTimer _statsTimer;
        private string? _selectedCaptureDeviceId = null; // null = system default
        private uint? _selectedOutputDeviceId = null; // null = system default
        private MdnsDiscoveryClient? _guiDiscoveryClient;
        private readonly ConcurrentDictionary<string, MdnsDiscoveryClient.DiscoveredService> _discoveredServices = new();
        private PlayerConfig _playerConfig = new();
        private bool _isAutoConnecting = false;
        
        public MainWindow()
        {
            InitializeComponent();
            
            // Setup stats timer
            _statsTimer = new DispatcherTimer();
            _statsTimer.Interval = TimeSpan.FromSeconds(1);
            _statsTimer.Tick += StatsTimer_Tick;
            _statsTimer.Start();

            // Populate output devices (Player tab)
            PopulateDevices();

            // Populate render devices (Server tab - audio capture source)
            PopulateRenderDevices();

            // Load Info
            TxtHostName.Text = Dns.GetHostName().ToUpper();
            TxtLocalIps.Text = string.Join("  •  ", AudioEngine.GetLocalIPAddresses());

            // Initial UI state
            UpdateServerUI(false);
            UpdatePlayerUI(false);

            // Ensure the first tab is selected
            MainTabs.SelectedIndex = 0;
            NavServer.IsChecked = true;

            // Load Player Config
            _ = LoadPlayerConfig();

            // AUTO-START SERVER
            StartServer();

            // START DISCOVERY FOR GUI
            StartGuiDiscovery();

            InitializeNotifyIcon();

            CoreLogger.Instance.Log("AudioTransfer UI Modernized.");
        }

        private System.Windows.Forms.NotifyIcon? _notifyIcon;

        private void InitializeNotifyIcon()
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            try
            {
                // Attempt to extract app icon or use fallback
                string appPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (appPath.EndsWith(".dll")) appPath = appPath.Replace(".dll", ".exe");
                
                if (System.IO.File.Exists(appPath))
                    _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(appPath);
                else
                    _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }
            catch
            {
                _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }
            
            _notifyIcon.Text = "AudioTransfer";

            // Context Menu
            var contextMenu = new System.Windows.Forms.ContextMenuStrip();
            contextMenu.Items.Add("Open", null, (s, e) => {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            });
            contextMenu.Items.Add("-"); // Separator
            contextMenu.Items.Add("Exit", null, (s, e) => ExitApplication());
            _notifyIcon.ContextMenuStrip = contextMenu;

            _notifyIcon.DoubleClick += (s, e) => {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            };
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Minimized && _playerConfig.MinimizeToTray)
            {
                Hide();
                if (_notifyIcon != null) _notifyIcon.Visible = true;
            }
            base.OnStateChanged(e);
        }

        private LogWindow? _logWindow;
        private void ShowLogWindow()
        {
            if (_logWindow == null || !IsWindowOpen(_logWindow))
            {
                _logWindow = new LogWindow();
            }

            _logWindow.Show();
            _logWindow.Activate();
            if (_logWindow.WindowState == WindowState.Minimized)
                _logWindow.WindowState = WindowState.Normal;
        }

        private bool IsWindowOpen(Window window)
        {
            return System.Windows.Application.Current.Windows.Cast<Window>().Any(x => x == window);
        }

        private void BtnShowLogs_Click(object sender, RoutedEventArgs e)
        {
            ShowLogWindow();
        }

        private void StartServer()
        {
            try
            {
                _serverEngine?.Dispose();
                _serverEngine = new AudioEngine();
                
                // Subscribe to connection events
                _serverEngine.OnClientConnected += (s, ip) => Dispatcher.Invoke(() => {
                    TxtConnections.Text = $"Connected: {ip}";
                    TxtConnections.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("AccentBrush");
                });
                
                _serverEngine.OnClientDisconnected += (s, ip) => Dispatcher.Invoke(() => {
                    TxtConnections.Text = "No connected device yet";
                    TxtConnections.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("TextSecBrush");
                });

                _serverEngine.StartWasapiToAndroid(5000, _selectedCaptureDeviceId);
                UpdateServerUI(true);
            }
            catch (Exception ex)
            {
                CoreLogger.Instance.Log($"Failed to auto-start server: {ex.Message}");
            }
        }

        private void PopulateDevices()
        {
            try
            {
                var devices = WasapiPlayer.GetAvailableDevices();
                OutputDeviceList.Children.Clear();

                // Add "System default" option
                var defaultBtn = new System.Windows.Controls.RadioButton
                {
                    GroupName = "OutputDevice",
                    IsChecked = _selectedOutputDeviceId == null,
                    Tag = (uint?)null,
                    Style = (Style)FindResource("DeviceRadioButton")
                };
                var defaultSp = new StackPanel();
                defaultSp.Children.Add(new TextBlock
                {
                    Text = "System default (Communication)",
                    FontSize = 13,
                    Foreground = (System.Windows.Media.SolidColorBrush)FindResource("TextPrimaryBrush")
                });
                defaultBtn.Content = defaultSp;
                defaultBtn.Checked += OutputDevice_Checked;
                OutputDeviceList.Children.Add(defaultBtn);

                foreach (var kv in devices)
                {
                    var rb = new System.Windows.Controls.RadioButton
                    {
                        GroupName = "OutputDevice",
                        Tag = kv.Key,
                        IsChecked = _selectedOutputDeviceId == kv.Key,
                        Style = (Style)FindResource("DeviceRadioButton")
                    };
                    var sp = new StackPanel();
                    sp.Children.Add(new TextBlock
                    {
                        Text = kv.Value,
                        FontSize = 13,
                        Foreground = (System.Windows.Media.SolidColorBrush)FindResource("TextPrimaryBrush"),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                    rb.Content = sp;
                    rb.Checked += OutputDevice_Checked;
                    OutputDeviceList.Children.Add(rb);
                }

                // Initial label update
                UpdateSelectedOutputDeviceLabel(_selectedOutputDeviceId == null ? "System default" : devices.FirstOrDefault(x => x.Key == _selectedOutputDeviceId).Value ?? "Unknown Device");
            }
            catch (Exception ex)
            {
                CoreLogger.Instance.Log($"Error loading devices: {ex.Message}");
            }
        }

        private void OutputDevice_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.RadioButton rb)
            {
                _selectedOutputDeviceId = rb.Tag as uint?;

                // Update the button label with the selected device name
                var nameBlock = rb.Content as StackPanel;
                string displayName = "System default";
                if (nameBlock?.Children[0] is TextBlock tb)
                    displayName = tb.Text;
                UpdateSelectedOutputDeviceLabel(displayName);

                // Auto-navigate back with animation
                if (OutputDevicePickerView.Visibility == Visibility.Visible)
                {
                    BtnBackFromOutputDevices_Click(this, new RoutedEventArgs());
                }
            }
        }

        private void UpdateSelectedOutputDeviceLabel(string name)
        {
            var txt = FindNameInTemplate(BtnSelectOutputDevice, "TxtSelectedOutputDeviceName") as TextBlock;
            if (txt != null) txt.Text = name;
        }

        private void BtnSelectOutputDevice_Click(object sender, RoutedEventArgs e)
        {
            PlayerMainView.Visibility = Visibility.Collapsed;
            OutputDevicePickerView.Visibility = Visibility.Visible;

            var sb = PlayerTabRoot.Resources["SlideInOutputPicker"] as System.Windows.Media.Animation.Storyboard;
            sb?.Begin();
        }

        private void BtnBackFromOutputDevices_Click(object sender, RoutedEventArgs e)
        {
            OutputDevicePickerView.Visibility = Visibility.Collapsed;
            PlayerMainView.Visibility = Visibility.Visible;

            var sb = PlayerTabRoot.Resources["SlideInOutputMain"] as System.Windows.Media.Animation.Storyboard;
            sb?.Begin();
        }

        private void PopulateRenderDevices()
        {
            try
            {
                var devices = WasapiTimedCapture.GetRenderDevices();
                DeviceList.Children.Clear();

                // Add "System default" option
                var defaultBtn = new System.Windows.Controls.RadioButton
                {
                    GroupName = "CaptureDevice",
                    IsChecked = true,
                    Tag = (string?)null,
                    Style = (Style)FindResource("DeviceRadioButton")
                };
                var defaultSp = new StackPanel();
                defaultSp.Children.Add(new TextBlock
                {
                    Text = "System default",
                    FontSize = 13,
                    Foreground = (System.Windows.Media.SolidColorBrush)FindResource("TextPrimaryBrush")
                });
                defaultBtn.Content = defaultSp;
                defaultBtn.Checked += CaptureDevice_Checked;
                DeviceList.Children.Add(defaultBtn);

                foreach (var (id, name) in devices)
                {
                    var rb = new System.Windows.Controls.RadioButton
                    {
                        GroupName = "CaptureDevice",
                        Tag = id,
                        Style = (Style)FindResource("DeviceRadioButton")
                    };
                    var sp = new StackPanel();
                    sp.Children.Add(new TextBlock
                    {
                        Text = name,
                        FontSize = 13,
                        Foreground = (System.Windows.Media.SolidColorBrush)FindResource("TextPrimaryBrush"),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                    rb.Content = sp;
                    rb.Checked += CaptureDevice_Checked;
                    DeviceList.Children.Add(rb);
                }
            }
            catch (Exception ex)
            {
                CoreLogger.Instance.Log($"Error loading render devices: {ex.Message}");
            }
        }

        private void CaptureDevice_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.RadioButton rb)
            {
                _selectedCaptureDeviceId = rb.Tag as string;
                
                // Update the button label with the selected device name
                var nameBlock = rb.Content as StackPanel;
                string displayName = "System default";
                if (nameBlock?.Children[0] is TextBlock tb)
                    displayName = tb.Text;
                UpdateSelectedDeviceLabel(displayName);
                
                // Switch device in real-time if engine is running
                if (_serverEngine != null && _serverEngine.IsRunning)
                {
                    _ = _serverEngine.SwitchCaptureDevice(_selectedCaptureDeviceId);
                }

                // Auto-navigate back with animation
                if (DevicePickerView.Visibility == Visibility.Visible)
                {
                    BtnBackFromDevices_Click(this, new RoutedEventArgs());
                }
            }
        }

        private void UpdateSelectedDeviceLabel(string name)
        {
            // The TextBlock is inside a ControlTemplate, so we find it by name after the template is applied
            var txt = FindNameInTemplate(BtnSelectDevice, "TxtSelectedDeviceName") as TextBlock;
            if (txt != null) txt.Text = name;
        }

        private static DependencyObject? FindNameInTemplate(System.Windows.Controls.Control control, string name)
        {
            var template = control.Template;
            if (template == null) return null;
            return template.FindName(name, control) as DependencyObject;
        }

        private void BtnSelectDevice_Click(object sender, RoutedEventArgs e)
        {
            ServerMainView.Visibility = Visibility.Collapsed;
            DevicePickerView.Visibility = Visibility.Visible;
            
            var sb = ServerTabRoot.Resources["SlideInPicker"] as System.Windows.Media.Animation.Storyboard;
            sb?.Begin();
        }

        private void BtnBackFromDevices_Click(object sender, RoutedEventArgs e)
        {
            DevicePickerView.Visibility = Visibility.Collapsed;
            ServerMainView.Visibility = Visibility.Visible;
            
            var sb = ServerTabRoot.Resources["SlideInMain"] as System.Windows.Media.Animation.Storyboard;
            sb?.Begin();
        }

        #region Player Discovery

        private void StartGuiDiscovery()
        {
            _guiDiscoveryClient = new MdnsDiscoveryClient("_audiooverlan-mic._udp");
            _guiDiscoveryClient.OnServiceDiscovered += (s, service) =>
            {
                _discoveredServices[service.IPAddress] = service;
                Dispatcher.Invoke(UpdateDiscoveredListUI);
            };
            _guiDiscoveryClient.OnServiceLost += (s, ip) =>
            {
                _discoveredServices.TryRemove(ip, out _);
                Dispatcher.Invoke(UpdateDiscoveredListUI);
            };
            _guiDiscoveryClient.Start();
        }

        private async Task LoadPlayerConfig()
        {
            _playerConfig = await PlayerConfig.LoadOrDefaultAsync();
            Dispatcher.Invoke(() =>
            {
                TxtDeviceName.Text = _playerConfig.DeviceName;
                ChkDarkTheme.IsChecked = _playerConfig.IsDarkTheme;
                ChkMinimizeToTray.IsChecked = _playerConfig.MinimizeToTray;
                BtnLanguage.Content = _playerConfig.Language;
                ChkStartup.IsChecked = _playerConfig.LaunchOnStartup;
                ChkAutoMute.IsChecked = _playerConfig.AutoMute;
                ChkAutoConnect.IsChecked = _playerConfig.AutoConnect;

                if (!string.IsNullOrEmpty(_playerConfig.LastConnectedIp))
                {
                    InputAndroidIp.Text = _playerConfig.LastConnectedIp;
                    InputAndroidPort.Text = _playerConfig.LastConnectedPort.ToString();
                }
            });
        }

        private void SettingsChanged(object sender, EventArgs e)
        {
            if (_playerConfig == null) return;

            // Sync UI to Config
            if (sender == TxtDeviceName) _playerConfig.DeviceName = TxtDeviceName.Text;
            else if (sender == ChkDarkTheme) _playerConfig.IsDarkTheme = ChkDarkTheme.IsChecked ?? true;
            else if (sender == ChkMinimizeToTray) _playerConfig.MinimizeToTray = ChkMinimizeToTray.IsChecked ?? true;
            else if (sender == ChkAutoMute) _playerConfig.AutoMute = ChkAutoMute.IsChecked ?? false;

            _ = _playerConfig.SaveAsync();
        }

        private void BtnLanguage_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.MessageBox.Show("Language selection will be implemented in the next version.", "Information");
        }

        private void ChkStartup_Checked(object sender, RoutedEventArgs e)
        {
            SetStartup(true);
            if (_playerConfig != null)
            {
                _playerConfig.LaunchOnStartup = true;
                _ = _playerConfig.SaveAsync();
            }
        }

        private void ChkStartup_Unchecked(object sender, RoutedEventArgs e)
        {
            SetStartup(false);
            if (_playerConfig != null)
            {
                _playerConfig.LaunchOnStartup = false;
                _ = _playerConfig.SaveAsync();
            }
        }

        private void SetStartup(bool start)
        {
            try
            {
                string appName = "AudioTransfer";
                string appPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                // For .exe instead of .dll if running via dotnet exec
                if (appPath.EndsWith(".dll")) appPath = appPath.Replace(".dll", ".exe");

                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true)!)
                {
                    if (start) key.SetValue(appName, $"\"{appPath}\"");
                    else key.DeleteValue(appName, false);
                }
            }
            catch (Exception ex)
            {
                CoreLogger.Instance.Log($"Failed to set startup: {ex.Message}");
            }
        }

        private void ChkAutoConnect_Click(object sender, RoutedEventArgs e)
        {
            _playerConfig.AutoConnect = ChkAutoConnect.IsChecked ?? false;
            _ = _playerConfig.SaveAsync();
        }

        private void UpdateDiscoveredListUI()
        {
            DiscoveredDevicesList.Children.Clear();
            if (_discoveredServices.IsEmpty)
            {
                DiscoveredDevicesList.Children.Add(new TextBlock
                {
                    Text = "Searching for devices...",
                    Style = (Style)FindResource("SettingDescription"),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    Margin = new Thickness(0, 10, 0, 0)
                });
                return;
            }

            // Check for Auto Connect
            if (_playerConfig.AutoConnect && !_isAutoConnecting && (_playerEngine == null || !_playerEngine.IsRunning))
            {
                var lastDevice = _discoveredServices.Values.FirstOrDefault(x => x.IPAddress == _playerConfig.LastConnectedIp);
                if (lastDevice != null)
                {
                    _isAutoConnecting = true;
                    CoreLogger.Instance.Log($"[AutoConnect] Found last device {lastDevice.IPAddress}. Attempting connection...");
                    InputAndroidIp.Text = lastDevice.IPAddress;
                    InputAndroidPort.Text = lastDevice.Port.ToString();
                    BtnStartMic_Click(this, new RoutedEventArgs());
                    _isAutoConnecting = false;
                }
            }

            foreach (var service in _discoveredServices.Values.OrderBy(x => x.InstanceName))
            {
                var btn = new System.Windows.Controls.Button
                {
                    Style = (Style)FindResource("PrimaryButton"),
                    Margin = new Thickness(0, 4, 0, 4),
                    HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Padding = new Thickness(16, 12, 16, 12)
                };

                var grid = new Grid { HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var icon = new TextBlock { Text = "📱", FontSize = 20, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(icon, 0);
                grid.Children.Add(icon);

                var stp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
                stp.Children.Add(new TextBlock { Text = service.DisplayName, FontWeight = FontWeights.Bold, FontSize = 14 });
                stp.Children.Add(new TextBlock { Text = service.IPAddress, FontSize = 12, Foreground = (System.Windows.Media.Brush)FindResource("TextSecBrush") });
                Grid.SetColumn(stp, 1);
                grid.Children.Add(stp);

                btn.Content = grid;
                btn.Click += (s, e) =>
                {
                    InputAndroidIp.Text = service.IPAddress;
                    InputAndroidPort.Text = service.Port.ToString();
                    BtnStartMic_Click(this, new RoutedEventArgs());
                };
                DiscoveredDevicesList.Children.Add(btn);
            }
        }

        #endregion

        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            if (MainTabs == null) return;

            if (sender is System.Windows.Controls.RadioButton rb)
            {
                if (rb == NavServer) MainTabs.SelectedIndex = 0;
                else if (rb == NavPlayer) MainTabs.SelectedIndex = 1;
                else if (rb == NavSettings) MainTabs.SelectedIndex = 2;
            }
        }

        private async void BtnStartMic_Click(object sender, RoutedEventArgs e)
        {
            if (_playerEngine != null && _playerEngine.IsRunning)
            {
                // Stop Player asynchronously to prevent UI block
                var engine = _playerEngine;
                BtnConnectPlayer.IsEnabled = false;
                if (BtnConnectText != null) BtnConnectText.Text = "STOPPING...";
                await Task.Run(() => engine.Stop());
                BtnConnectPlayer.IsEnabled = true;
                UpdatePlayerUI(false);
                return;
            }

            try
            {
                string targetIp = InputAndroidIp.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(targetIp))
                {
                    System.Windows.MessageBox.Show("Please enter an Android IP address or use discovery.");
                    return;
                }

                // Validate IP format if not auto-discovery
                if (targetIp != "0.0.0.0" && targetIp != "Any")
                {
                    if (!System.Net.IPAddress.TryParse(targetIp, out _))
                    {
                        System.Windows.MessageBox.Show("Invalid IP address format. Example: 192.168.1.100");
                        return;
                    }
                }

                if (!int.TryParse(InputAndroidPort.Text, out int port)) port = 5003;
                
                uint deviceId = _selectedOutputDeviceId ?? uint.MaxValue; // uint.MaxValue = WAVE_MAPPER (System Default)

                _playerEngine?.Dispose();
                _playerEngine = new AudioEngine();
                
                // Active verification before showing "Running" UI
                bool success = await _playerEngine.StartAndroidMicListenerAsync(targetIp, port, (int)deviceId);
                if (success)
                {
                    // Auto-mute if setting is enabled
                    if (_playerConfig.AutoMute)
                    {
                        _playerEngine.SetSystemMute(true);
                    }
                }
                else
                {
                    System.Windows.MessageBox.Show($"Could not connect to {targetIp}:{port}.\n\n1. Ensure Android is on the same WiFi.\n2. Ensure 'Server' is STARTED on Android app.");
                    _playerEngine.Dispose();
                    _playerEngine = null;
                    UpdatePlayerUI(false);
                    return;
                }

                _playerEngine.OnClientConnected += (s, ip) => Dispatcher.Invoke(() => {
                    TxtPlayerStatus.Text = $"Receiving from {ip}";
                    PlayerStatusIconColor.Color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4CAF50"); // Green
                    PlayerStatusIcon.Text = "✓";
                });

                _playerEngine.OnClientDisconnected += (s, ip) => Dispatcher.Invoke(() => {
                    TxtPlayerStatus.Text = "Waiting for Android...";
                    PlayerStatusIconColor.Color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFC107"); // Amber
                    PlayerStatusIcon.Text = "⚠";
                });

                UpdatePlayerUI(true);


                // Save last successful connection
                _playerConfig.LastConnectedIp = targetIp;
                _playerConfig.LastConnectedPort = port;
                _ = _playerConfig.SaveAsync();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to connect: {ex.Message}");
                UpdatePlayerUI(false);
            }
        }

        private async void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            BtnConnectPlayer.IsEnabled = false;
            var server = _serverEngine;
            var player = _playerEngine;

            await Task.Run(() => {
                server?.Stop();
                player?.Stop();
            });

            BtnConnectPlayer.IsEnabled = true;
            UpdatePlayerUI(false);
            UpdateServerUI(false);
        }



        private void UpdateServerUI(bool running)
        {
            if (!running)
            {
                TxtConnections.Text = "No connected device yet";
                TxtConnections.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("TextSecBrush");
            }
        }

        private void UpdatePlayerUI(bool running)
        {
            // Update Toggle Button content via named elements
            if (BtnConnectIcon != null) BtnConnectIcon.Text = running ? "⏹" : "📡";
            if (BtnConnectText != null) BtnConnectText.Text = running ? "STOP" : "CONNECT";
            
            // Disable inputs while running
            InputAndroidIp.IsEnabled = !running;
            InputAndroidPort.IsEnabled = !running;
            BtnSelectOutputDevice.IsEnabled = !running;

            // Trigger animations if state changes
            bool isShowingStats = PlayerStatsCard.Visibility == Visibility.Visible;
            if (running && !isShowingStats)
            {
                var sb = PlayerTabRoot.Resources["TransitionToStats"] as System.Windows.Media.Animation.Storyboard;
                sb?.Begin();
            }
            else if (!running && isShowingStats)
            {
                var sb = PlayerTabRoot.Resources["TransitionToConnect"] as System.Windows.Media.Animation.Storyboard;
                sb?.Begin();
            }

            if (running)
            {
                TxtPlayerStatus.Text = $"Streaming from {InputAndroidIp.Text}";
                PlayerStatusBg.Color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#154CAF50"); // Dim accent green
                PlayerStatusIcon.Text = "✓";
                PlayerStatusIconColor.Color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4CAF50");
            }
            else
            {
                TxtPlayerStatus.Text = "Not connected";
                PlayerStatusBg.Color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2A2020"); // Dark warning red
                PlayerStatusIcon.Text = "⚠";
                PlayerStatusIconColor.Color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF5252");
                
                TxtBuffer.Text = "0 ms";
                TxtLatency.Text = "0.0 ms";
            }
        }

        private void StatsTimer_Tick(object? sender, EventArgs e)
        {
            // Update Player Stats
            if (_playerEngine != null && _playerEngine.IsRunning)
            {
                var snap = _playerEngine.Stats.GetSnapshot();
                if (_playerEngine.CurrentMode == AudioEngine.StreamingMode.AndroidMicListener)
                {
                    var jitterStats = _playerEngine.MicJitter?.GetStatistics();
                    if (jitterStats != null)
                    {
                        TxtBuffer.Text = $"{jitterStats.DelayMs} ms ({jitterStats.BufferLevel}/{jitterStats.TargetPackets})"; 
                        TxtLatency.Text = $"{jitterStats.LastTransitDelay} ms (Loss: {jitterStats.LossRate:F1}%)";
                        
                        if (jitterStats.BitrateKbps > 0.1)
                        {
                            TxtPlayerStatus.Text = $"Receiving from {InputAndroidIp.Text} ({jitterStats.BitrateKbps:F0} kbps)";
                            PlayerStatusIconColor.Color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4CAF50");
                            PlayerStatusIcon.Text = "✓";
                        }
                        else
                        {
                            TxtPlayerStatus.Text = $"Connected to {InputAndroidIp.Text}, but no data";
                            PlayerStatusIconColor.Color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFC107");
                            PlayerStatusIcon.Text = "⚠";
                        }
                    }
                    else
                    {
                        TxtBuffer.Text = "Waiting...";
                        TxtLatency.Text = "—";
                    }
                }
                if (!_playerEngine.IsRunning) UpdatePlayerUI(false);
            }
            else
            {
                UpdatePlayerUI(false);
            }

            // Update Server Stats
            if (_serverEngine != null)
            {
                if (!_serverEngine.IsRunning) UpdateServerUI(false);
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_playerConfig.MinimizeToTray)
            {
                e.Cancel = true;
                WindowState = WindowState.Minimized;
                Hide();
                if (_notifyIcon != null) _notifyIcon.Visible = true;
                return;
            }

            _serverEngine?.Dispose();
            _playerEngine?.Dispose();
            _guiDiscoveryClient?.Dispose();
            _notifyIcon?.Dispose();
            System.Windows.Application.Current.Shutdown();
        }

        private void ExitApplication()
        {
            _playerConfig.MinimizeToTray = false; // Bypass cancel logic
            Close();
        }
    }
}