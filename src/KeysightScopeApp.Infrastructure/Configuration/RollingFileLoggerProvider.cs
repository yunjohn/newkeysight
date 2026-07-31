using Microsoft.Extensions.Logging;

namespace KeysightScopeApp.Infrastructure.Configuration;

public sealed class RollingFileLoggerProvider(string directory) : ILoggerProvider
{
    private readonly object gate = new();
    private bool disposed;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Write(string category, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        lock (gate)
        {
            if (disposed) return;
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"scope-{DateTime.Now:yyyyMMdd}.log");
            string suffix = File.Exists(path) && new FileInfo(path).Length >= 10 * 1024 * 1024
                ? $"-{DateTime.Now:HHmmss}"
                : "";
            if (suffix.Length > 0)
                path = Path.Combine(directory, $"scope-{DateTime.Now:yyyyMMdd}{suffix}.log");
            string line =
                $"{DateTimeOffset.Now:O} [{level}] {category} ({eventId.Id}) {message}{Environment.NewLine}";
            File.AppendAllText(path, line + (exception is null ? "" : exception + Environment.NewLine));
        }
    }

    public void Dispose()
    {
        lock (gate) disposed = true;
    }

    private sealed class FileLogger(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                provider.Write(category, logLevel, eventId, formatter(state, exception), exception);
        }
    }
}
