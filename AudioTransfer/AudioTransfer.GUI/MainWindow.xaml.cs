using System.Windows;
using System.IO;
using System;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Threading;
using System.Threading.Tasks;
using AudioTransfer.Core.Audio;
using AudioTransfer.Core.Facade;
using AudioTransfer.Core.Logging;
using AudioTransfer.Core.Models;

using System.Windows.Media;
using AudioTransfer.Core.Network;
using System.Linq;
using Microsoft.Win32;
using System.Drawing;
using System.Windows.Forms;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AudioTransfer.GUI
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private readonly IServerEngine _serverEngine;
        private readonly IPlayerEngine _playerEngine;
        
        public ViewModels.MainViewModel ViewModel { get; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private MdnsDiscoveryClient? _guiDiscoveryClient;

        public MainWindow(IServerEngine serverEngine, IPlayerEngine playerEngine, ViewModels.MainViewModel viewModel)
        {
            _serverEngine = serverEngine;
            _playerEngine = playerEngine;
            ViewModel = viewModel;
            this.DataContext = this;
            InitializeComponent();

            // Subscribe to VM setting requests
            ViewModel.RequestApplyTheme += (s, dark) => ApplyTheme(dark);
            ViewModel.RequestApplyLanguage += (s, lang) => ApplyLanguage(lang);
            ViewModel.RequestUpdateStartup += (s, startup) => UpdateStartup(startup);
            
            ViewModel.RequestShowNotification += (s, args) => ShowNotification(args.Message, args.Title);
            ViewModel.RequestTransitionToStats += (s, e) => Dispatcher.Invoke(() => {
                var sb = PlayerTabRoot.Resources["TransitionToStats"] as System.Windows.Media.Animation.Storyboard;
                sb?.Begin();
            });
            ViewModel.RequestTransitionToConnect += (s, e) => Dispatcher.Invoke(() => {
                var sb = PlayerTabRoot.Resources["TransitionToConnect"] as System.Windows.Media.Animation.Storyboard;
                sb?.Begin();
            });
            ViewModel.RequestRefreshDiscovery += (s, e) => {
                // Clear the mDNS client's internal cache so it re-triggers events for current servers
                _guiDiscoveryClient?.ClearCache();
                
                // Send a burst of queries for better reliability on refresh
                Task.Run(async () => {
                    for (int i = 0; i < 3; i++) {
                        _guiDiscoveryClient?.SendQuery();
                        await Task.Delay(500);
                    }
                });
            };
            
            _guiDiscoveryClient = new MdnsDiscoveryClient("_audiooverlan-mic._udp");
            _guiDiscoveryClient.OnServiceDiscovered += (s, service) =>
            {
                Dispatcher.Invoke(() => ViewModel.AddDiscoveredServer(service.DisplayName, service.IPAddress, service.Port));
            };
            _guiDiscoveryClient.OnServiceLost += (s, ip) =>
            {
                Dispatcher.Invoke(() => ViewModel.RemoveDiscoveredServer(ip));
            };
            _guiDiscoveryClient.Start();

            InitializeNotifyIcon();
            _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            CoreLogger.Instance.Log("[Init] Starting InitializeAsync...");
 
            // 1. Init ViewModel and populate data (loads config too)
            await ViewModel.InitAsync();
            
            // Apply initial theme/lang from Vm
            ApplyTheme(ViewModel.IsDarkTheme);
            ApplyLanguage(ViewModel.Language);

            // 5. Initial UI state
            UpdateServerUI(false);

            // 6. Navigation
            MainTabs.SelectedIndex = 0;
            NavServer.IsChecked = true;

            // 7. Auto-start server (on background thread to avoid blocking UI)
            await Task.Run(() => StartServer());

            CoreLogger.Instance.Log("AudioTransfer UI Modernized.");
        }

        // Custom ComboBox click handler (replaces ToggleButton to avoid click routing conflicts)
        private void ComboBox_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Controls.ComboBox cb)
            {
                if (cb.IsDropDownOpen)
                {
                    // If the dropdown is open, only toggle it closed if clicking on the main header area.
                    // This allows clicks on items in the dropdown to pass through and be selected.
                    var pos = e.GetPosition(cb);
                    if (pos.X >= 0 && pos.X <= cb.ActualWidth && pos.Y >= 0 && pos.Y <= cb.ActualHeight)
                    {
                        cb.IsDropDownOpen = false;
                        e.Handled = true;
                    }
                }
                else
                {
                    // If closed, click to open.
                    cb.IsDropDownOpen = true;
                    e.Handled = true;
                }
            }
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
            if (WindowState == WindowState.Minimized && ViewModel.MinimizeToTray)
            {
                Hide();
                if (_notifyIcon != null) _notifyIcon.Visible = true;
            }
            base.OnStateChanged(e);
        }

        private void StartServer()
        {
            Dispatcher.Invoke(() =>
            {
                if (!ViewModel.IsServerRunning)
                {
                    ViewModel.ToggleServerCommand.Execute(null);
                }
            });
        }



        public void ApplyTheme(bool isDark)
        {
            try
            {
                string themeFile = isDark ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml";
                var newDict = new ResourceDictionary { Source = new Uri(themeFile, UriKind.Relative) };

                // Find old theme dictionary and replace it
                var existingDict = System.Windows.Application.Current.Resources.MergedDictionaries
                    .FirstOrDefault(d => d.Source != null && (d.Source.OriginalString.Contains("DarkTheme.xaml") || d.Source.OriginalString.Contains("LightTheme.xaml")));

                if (existingDict != null)
                {
                    System.Windows.Application.Current.Resources.MergedDictionaries.Remove(existingDict);
                }
                System.Windows.Application.Current.Resources.MergedDictionaries.Add(newDict);
                
                // Also update individual resources that might be referenced as StaticResource in some places (legacy)
                // but mostly we should use DynamicResource in XAML.
            }
            catch (Exception ex)
            {
                CoreLogger.Instance.Log($"Failed to apply theme: {ex.Message}");
            }
        }

        public void ApplyLanguage(string lang)
        {
            try
            {
                bool isVi = lang == "Vietnamese";
                string langFile = isVi ? "Themes/Strings.vi.xaml" : "Themes/Strings.en.xaml";
                var newDict = new ResourceDictionary { Source = new Uri(langFile, UriKind.Relative) };

                var existingDict = System.Windows.Application.Current.Resources.MergedDictionaries
                    .FirstOrDefault(d => d.Source != null && (d.Source.OriginalString.Contains("Strings.vi.xaml") || d.Source.OriginalString.Contains("Strings.en.xaml")));

                if (existingDict != null)
                {
                    System.Windows.Application.Current.Resources.MergedDictionaries.Remove(existingDict);
                }
                System.Windows.Application.Current.Resources.MergedDictionaries.Add(newDict);
            }
            catch (Exception ex)
            {
                CoreLogger.Instance.Log($"Failed to apply language: {ex.Message}");
            }
        }


        private void UpdateStartup(bool start)
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

 
  

        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            if (MainTabs == null || TxtActivePage == null) return;

            int oldIndex = MainTabs.SelectedIndex;
            int newIndex = oldIndex;

            if (sender is System.Windows.Controls.RadioButton rb)
            {
                if (rb == NavServer)
                {
                    newIndex = 0;
                    TxtActivePage.Text = "SERVER CONSOLE";
                }
                else if (rb == NavPlayer)
                {
                    newIndex = 1;
                    TxtActivePage.Text = "PLAYER HUB";
                }
                else if (rb == NavSettings)
                {
                    newIndex = 2;
                    TxtActivePage.Text = "GENERAL SETTINGS";
                }

                if (newIndex != oldIndex)
                {
                    MainTabs.SelectedIndex = newIndex;
                    var sb = Resources["TabFadeIn"] as System.Windows.Media.Animation.Storyboard;
                    sb?.Begin();
                }
            }
        }


        // Logic moved to ViewModel
        private void UpdateServerUI(bool running)
        {
            // AppStatusText is now purely event-driven in the ViewModel
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (ViewModel.MinimizeToTray)
            {
                e.Cancel = true;
                WindowState = WindowState.Minimized;
                Hide();
                if (_notifyIcon != null) _notifyIcon.Visible = true;
                return;
            }

            if (ViewModel.AutoMute && _serverEngine != null)
            {
                _serverEngine.SetSystemMute(false);
            }
            
            ViewModel.StopAll();
            ViewModel.Dispose();

            // Engines are Singletons now, their lifespans are managed by IHost.
            // AppHost.Dispose() handles the disposal in completely exiting.
            _guiDiscoveryClient?.Dispose();
            _notifyIcon?.Dispose();
            System.Windows.Application.Current.Shutdown();
        }

        private void ExitApplication()
        {
            ViewModel.MinimizeToTray = false; // Bypass cancel logic
            Close();
        }

        #region In-App Notifications (Toasts)

        private CancellationTokenSource? _toastCts;

        private void ShowNotification(string message, string title = "Notification")
        {
            Dispatcher.Invoke(async () =>
            {
                // Cancel existing toast hide timer
                _toastCts?.Cancel();
                _toastCts = new CancellationTokenSource();
                var token = _toastCts.Token;

                // Stop any previous animations to avoid conflicts and race conditions with 'Completed' events
                var showSb = Resources["ShowToast"] as System.Windows.Media.Animation.Storyboard;
                var hideSb = Resources["HideToast"] as System.Windows.Media.Animation.Storyboard;
                var timerSb = Resources["ToastTimerAnim"] as System.Windows.Media.Animation.Storyboard;
                
                showSb?.Stop();
                hideSb?.Stop();
                timerSb?.Stop();

                TxtToastTitle.Text = title.ToUpper();
                TxtToastMessage.Text = message;
                
                // ShowToast now handles Visibility.Visible
                showSb?.Begin();
                timerSb?.Begin();

                try
                {
                    // Wait for 4 seconds
                    await Task.Delay(4000, token);

                    // Hide toast
                    HideNotification();
                }
                catch (TaskCanceledException)
                {
                    // Another toast replaced this one
                }
            });
        }

        private void HideNotification()
        {
            var hideSb = Resources["HideToast"] as System.Windows.Media.Animation.Storyboard;
            // Visibility.Collapsed is now handled within the HideToast storyboard in XAML
            hideSb?.Begin();
        }

        private void BtnCloseToast_Click(object sender, RoutedEventArgs e)
        {
            _toastCts?.Cancel();
            HideNotification();
        }

        #endregion
    }
}