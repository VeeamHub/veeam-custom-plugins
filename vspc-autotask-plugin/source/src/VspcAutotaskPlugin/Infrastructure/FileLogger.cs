using System.Text;

namespace VspcAutotaskPlugin.Infrastructure;

/// <summary>
/// Minimal file logger (Information and above) writing daily files under the data
/// directory's logs folder. These files are what the VSPC "download logs" host
/// endpoint (/api/v1/logs) packages, and the primary evidence when the plugin
/// misbehaves on a VSPC server (console output is not persisted by the host).
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _sync = new();

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    internal void Write(string line)
    {
        try
        {
            lock (_sync)
            {
                var path = Path.Combine(_directory, $"service-{DateTime.UtcNow:yyyyMMdd}.log");
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the service down.
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z [{logLevel,-11}] {_category}: {formatter(state, exception)}";
            if (exception != null)
                line += Environment.NewLine + "    " + exception.ToString().Replace(Environment.NewLine, Environment.NewLine + "    ");
            _provider.Write(line);
        }
    }
}
