using Microsoft.Extensions.Logging;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal sealed class NativeCheckLogger(string directory, LogLevel minimumLevel = LogLevel.Information) : ILoggerFactory, ILogger
{
    private readonly Lock _gate = new();

    public ILogger CreateLogger(string categoryName) => this;
    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
    public void Dispose() { }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel && logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (this.IsEnabled(logLevel)) { this.Write("owner-lifecycle.txt", $"[{logLevel}] {formatter(state, exception)} {exception}"); }
    }

    public void RecordFailure(string file, Exception exception, string detail) => this.Write(file, $"{detail}{Environment.NewLine}{exception}");

    private void Write(string file, string message)
    {
        lock (this._gate)
        {
            try
            {
                var path = Path.Combine(directory, file);
                if (File.Exists(path) && new FileInfo(path).Length >= 4 * 1024 * 1024) { return; }
                File.AppendAllText(path, $"{DateTime.UtcNow:O} [{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}