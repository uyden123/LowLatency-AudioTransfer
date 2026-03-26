using System.Windows;
using AudioTransfer.Core.Logging;

namespace AudioTransfer.GUI
{
    public partial class LogWindow : Window
    {
        public LogWindow()
        {
            InitializeComponent();
            
            // Load existing logs
            var recent = CoreLogger.Instance.GetRecentLogs(500);
            foreach (var log in recent)
            {
                AppendLog(log);
            }

            // Subscribe to new logs
            CoreLogger.Instance.LogEvent += CoreLogger_LogEvent;
        }

        private bool _isPaused = false;
        private void CoreLogger_LogEvent(object? sender, LogEventArgs e)
        {
            if (_isPaused) return;
            Dispatcher.Invoke(() => AppendLog(e.LogMessage));
        }

        private void AppendLog(LogMessage log)
        {
            TxtLogs.AppendText(log.ToString() + "\n");
            TxtLogs.ScrollToEnd();
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            CoreLogger.Instance.Clear();
            TxtLogs.Clear();
            TxtStatus.Text = "Logs cleared.";
        }

        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            _isPaused = !_isPaused;
            TxtPauseIcon.Text = _isPaused ? "\uE768" : "\uE769"; // Play icon : Pause icon
            TxtStatus.Text = _isPaused ? "Log updates paused." : "Monitoring active logs...";
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            TxtLogs.Clear();
            var recent = CoreLogger.Instance.GetRecentLogs(1000);
            foreach (var log in recent)
            {
                AppendLog(log);
            }
            TxtStatus.Text = $"Reloaded {recent.Length} logs.";
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sfd = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                    FileName = $"AudioTransfer_Log_{System.DateTime.Now:yyyyMMdd_HHmmss}.txt"
                };

                if (sfd.ShowDialog() == true)
                {
                    System.IO.File.WriteAllText(sfd.FileName, TxtLogs.Text);
                    TxtStatus.Text = "Logs exported successfully.";
                }
            }
            catch (System.Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to export logs: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Just hide instead of closing to keep the subscription alive and data intact
            e.Cancel = true;
            this.Hide();
        }
    }
}
