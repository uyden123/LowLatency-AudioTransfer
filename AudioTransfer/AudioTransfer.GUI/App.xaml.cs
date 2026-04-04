using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using AudioTransfer.Core.Logging;
using AudioTransfer.Core.Facade;
using AudioTransfer.GUI.ViewModels;
using AudioTransfer.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AudioTransfer.GUI
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        public static IHost? AppHost { get; private set; }

        public App()
        {
            AppHost = Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    // Services (Business Logic)
                    services.AddSingleton<IServerEngine, ServerEngine>();
                    services.AddSingleton<IPlayerEngine, PlayerEngine>();
                    
                    // Repositories
                    services.AddSingleton<IConfigRepository<PlayerConfig>>(new JsonFileConfigRepository<PlayerConfig>("player_config.json"));
                    services.AddSingleton<IConfigRepository<ServerConfig>>(new JsonFileConfigRepository<ServerConfig>("server_config.json"));

                    // ViewModels
                    services.AddSingleton<MainViewModel>();

                    // Views
                    services.AddSingleton<MainWindow>();
                })
                .Build();
        }

        public static Task<System.Collections.Generic.List<(string Id, string Name)>>? CaptureDevicesTask { get; private set; }
        public static Task<System.Collections.Generic.List<(string Id, string Name)>>? RenderDevicesTask { get; private set; }

        protected override async void OnStartup(StartupEventArgs e)
        {
            // Fire-and-forget: Immediately start interrogating Windows Audio APIs concurrently with rendering
            CaptureDevicesTask = Task.Run(() => AudioTransfer.Core.Audio.WasapiTimedCapture.GetRenderDevices());
            RenderDevicesTask = Task.Run(() => AudioTransfer.Core.Audio.WasapiPlayer.GetRenderDeviceList());

            await AppHost!.StartAsync();

            base.OnStartup(e);

            // Handle UI thread exceptions
            this.DispatcherUnhandledException += (s, args) =>
            {
                HandleException(args.Exception, "DispatcherUnhandledException");
                args.Handled = true;
                Environment.Exit(1);
            };

            // Handle background thread exceptions
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                HandleException(args.ExceptionObject as Exception, "AppDomain.UnhandledException");
                Environment.Exit(1);
            };

            // Handle unobserved task exceptions
            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                HandleException(args.Exception, "TaskScheduler.UnobservedTaskException");
                args.SetObserved();
            };

            var mainWindow = AppHost.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }

        private void HandleException(Exception? ex, string source)
        {
            if (ex == null) return;

            string reportPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash_report.log");
            string report = $"[CRASH REPORT - {DateTime.Now:yyyy-MM-dd HH:mm:ss}] Source: {source}\n";
            report += $"Exception Type: {ex.GetType().FullName}\n";
            report += $"Message: {ex.Message}\n";
            report += $"StackTrace:\n{ex.StackTrace}\n";
            if (ex.InnerException != null)
            {
                report += $"\nInner Exception: {ex.InnerException.Message}\n";
                report += $"Inner StackTrace:\n{ex.InnerException.StackTrace}\n";
            }
            report += new string('-', 80) + "\n\n";

            try
            {
                File.AppendAllText(reportPath, report);
                CoreLogger.Instance.LogError($"Fatal crash trapped from {source}", ex);
            }
            catch { }

            System.Windows.MessageBox.Show($"Lỗi nghiêm trọng xảy ra và ứng dụng buộc phải đóng.\nChi tiết lỗi đã được ghi vào file 'crash_report.log' tại thư mục:\n{reportPath}\n\nThông báo lỗi: {ex.Message}", 
                            "Fatal Error - Application Crash", 
                            MessageBoxButton.OK, 
                            MessageBoxImage.Error);
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            if (AppHost != null)
            {
                await AppHost.StopAsync();
                AppHost.Dispose();
            }
            base.OnExit(e);
        }
    }
}
