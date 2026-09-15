using System.Globalization;
using System.Text;
using JPSoftworks.MediaControlsExtension.Media.Hosting;
using Microsoft.Extensions.Logging;

namespace JPSoftworks.MediaControlsExtension.MediaHost;

internal sealed class WorkerFileLoggerProvider : ILoggerProvider
{
    private const long MaximumBytes = 4 * 1024 * 1024;
    private static readonly Encoding LogEncoding = new UTF8Encoding(false);
    private readonly Lock _gate = new();
    private readonly WorkerLoggingOptions _options;
    private readonly Timer _flushTimer;
    private StreamWriter? _writer;
    private long _writtenBytes;
    private bool _dirty;
    private bool _accepting = true;

    public WorkerFileLoggerProvider(WorkerLoggingOptions options)
    {
        this._options = options;
        Directory.CreateDirectory(options.Directory);
        var path = Path.Combine(options.Directory, $"log-worker-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Environment.ProcessId}.txt");
        foreach (var file in Directory.EnumerateFiles(options.Directory, "log-worker-*.txt")
            .OrderDescending(StringComparer.Ordinal).Skip(15))
        {
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        this._writer = new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), LogEncoding, 8192);
        this._flushTimer = new(static state => ((WorkerFileLoggerProvider)state!).Flush(), this,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
        lock (this._gate) { this.CloseUnderLock(); }
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        lock (this._gate)
        {
            if (!this._accepting)
            {
                return;
            }

            try
            {
                var line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:O} [{level}] [{Environment.ProcessId}] {category}: {message} {exception}");
                var content = line.AsSpan(0, Math.Min(line.Length, 16384));
                var bytes = LogEncoding.GetByteCount(content) + Environment.NewLine.Length;
                if (this._writtenBytes + bytes > MaximumBytes)
                {
                    this.CloseUnderLock();
                    return;
                }
                this._writer!.WriteLine(content);
                this._writtenBytes += bytes;
                this._dirty = true;
                if (level >= LogLevel.Warning) { this.FlushUnderLock(); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { this.CloseUnderLock(); }
        }
    }

    private void Flush()
    {
        lock (this._gate)
        {
            try { this.FlushUnderLock(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { this.CloseUnderLock(); }
        }
    }

    private void FlushUnderLock()
    {
        if (!this._dirty || this._writer is null) { return; }
        this._writer.Flush();
        this._dirty = false;
    }

    private void CloseUnderLock()
    {
        Volatile.Write(ref this._accepting, false);
        this._flushTimer.Dispose();
        try { this._writer?.Dispose(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { this._writer = null; }
    }

    private sealed class Logger(WorkerFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => Volatile.Read(ref provider._accepting) && logLevel != LogLevel.None &&
            (provider._options.Detailed || logLevel >= LogLevel.Warning);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (this.IsEnabled(logLevel))
            {
                provider.Write(category, logLevel, formatter(state, exception), exception);
            }
        }
    }
}