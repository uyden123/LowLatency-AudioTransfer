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

        private void CoreLogger_LogEvent(object? sender, LogEventArgs e)
        {
            Dispatcher.Invoke(() => AppendLog(e.LogMessage));
        }

        private void AppendLog(LogMessage log)
        {
            TxtLogs.AppendText(log.ToString() + "\n");
            TxtLogs.ScrollToEnd();
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            TxtLogs.Clear();
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
