using Microsoft.Extensions.Logging;
using System.IO;

namespace SignalForwarder;

public class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logFilePath;
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public FileLoggerProvider(string logFilePath)
    {
        _logFilePath = logFilePath;
        var directory = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        _writer = new StreamWriter(logFilePath, append: true, encoding: System.Text.Encoding.UTF8)
        {
            AutoFlush = true
        };
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(categoryName, _writer, _lock);
    }

    public void Dispose()
    {
        _writer?.Dispose();
    }

    private class FileLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly StreamWriter _writer;
        private readonly object _lock;

        public FileLogger(string categoryName, StreamWriter writer, object lockObj)
        {
            _categoryName = categoryName;
            _writer = writer;
            _lock = lockObj;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lock)
            {
                var message = formatter(state, exception);
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                var logLevelStr = logLevel.ToString().ToUpperInvariant();
                var logMessage = $"{timestamp} - {logLevelStr} - {message}";
                
                _writer.WriteLine(logMessage);
                
                if (exception != null)
                {
                    _writer.WriteLine($"异常详情: {exception}");
                }
            }
        }
    }
}

