using System;
using System.Collections.Concurrent;
using System.Linq;

namespace AudioTransfer.Core.Logging
{
    public sealed class CoreLogger
    {
        private static readonly Lazy<CoreLogger> _instance = new(() => new CoreLogger());
        public static CoreLogger Instance => _instance.Value;

        // In-memory metrics
        private readonly ConcurrentQueue<LogMessage> _memoryLogs = new();
        private const int MaxMemoryLogs = 1000;
        
        public event EventHandler<LogEventArgs> LogEvent;

        private CoreLogger() { }

        public void Log(string message, LogLevel level = LogLevel.Info)
        {
            var log = new LogMessage(DateTime.Now, message, level);
            
            _memoryLogs.Enqueue(log);
            while (_memoryLogs.Count > MaxMemoryLogs)
            {
                _memoryLogs.TryDequeue(out _);
            }

            LogEvent?.Invoke(this, new LogEventArgs(log));
        }

        public void Clear()
        {
            while (_memoryLogs.TryDequeue(out _)) { }
        }

        public void LogError(string message, Exception ex = null)
        {
            Log($"{message} {ex?.Message}", LogLevel.Error);
        }

        public LogMessage[] GetRecentLogs(int count = 50)
        {
            return _memoryLogs.TakeLast(count).ToArray();
        }
    }

    public enum LogLevel { Info, Warning, Error, Debug }

    public class LogMessage
    {
        public DateTime Timestamp { get; }
        public string Message { get; }
        public LogLevel Level { get; }

        public LogMessage(DateTime timestamp, string message, LogLevel level)
        {
            Timestamp = timestamp;
            Message = message;
            Level = level;
        }

        public override string ToString() => $"[{Timestamp:HH:mm:ss.fff}] [{Level}] {Message}";
    }

    public class LogEventArgs : EventArgs
    {
        public LogMessage LogMessage { get; }
        public LogEventArgs(LogMessage logMessage)
        {
            LogMessage = logMessage;
        }
    }
}
