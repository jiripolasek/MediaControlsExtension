using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting;

/// <summary>Occasionally releases resident pages above a soft threshold while the user is idle.</summary>
public sealed partial class ProcessMemoryMaintenance : IAsyncDisposable
{
    private static readonly Action<ILogger, long, long, long, Exception?> Trimmed = LoggerMessage.Define<long, long, long>(
        LogLevel.Debug, new EventId(1, "MemoryTrim"), "Working set trimmed from {BeforeBytes} to {AfterBytes} bytes; private commit {PrivateBytes} bytes");
    private static readonly Action<ILogger, Exception?> Failed = LoggerMessage.Define(
        LogLevel.Debug, new EventId(2, "MemoryTrimFailed"), "Working-set maintenance failed");
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ILogger _logger;
    private readonly long _threshold;
    private readonly Task _run;
    private Task? _disposal;

    /// <summary>Checks every thirty seconds; trims at most once per five minutes after thirty seconds without user input.</summary>
    public ProcessMemoryMaintenance(long privateMemoryThreshold, ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(privateMemoryThreshold);
        ArgumentNullException.ThrowIfNull(logger);
        this._threshold = privateMemoryThreshold;
        this._logger = logger;
        this._run = Task.Run(this.RunAsync);
    }

    public ValueTask DisposeAsync()
    {
        lock (this._gate) { return new(this._disposal ??= this.StopAsync()); }
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        using var process = Process.GetCurrentProcess();
        long lastAttempt = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(this._stop.Token).ConfigureAwait(false))
            {
                if ((lastAttempt != 0 && Stopwatch.GetElapsedTime(lastAttempt) < TimeSpan.FromMinutes(5)) || !IsUserIdle()) { continue; }
                try
                {
                    process.Refresh();
                    var before = process.WorkingSet64;
                    var privateBytes = process.PrivateMemorySize64;
                    if (before < this._threshold || privateBytes < this._threshold) { continue; }
                    lastAttempt = Stopwatch.GetTimestamp();
                    TrimCurrentProcess();
                    process.Refresh();
                    Trimmed(this._logger, before, process.WorkingSet64, privateBytes, null);
                }
                catch (Win32Exception ex)
                {
                    Failed(this._logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (this._stop.IsCancellationRequested)
        {
        }
    }

    private async Task StopAsync()
    {
        try
        {
            await this._stop.CancelAsync().ConfigureAwait(false);
            await this._run.ConfigureAwait(false);
        }
        finally
        {
            this._stop.Dispose();
        }
    }

    internal static void TrimCurrentProcess()
    {
        if (EmptyWorkingSet(new nint(-1)) == 0) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
    }

    private static bool IsUserIdle()
    {
        var input = new LastInputInfo { Size = 8 };
        return GetLastInputInfo(ref input) != 0 && IsUserIdle(input.Time, GetTickCount());
    }

    internal static bool IsUserIdle(uint lastInputTick, uint currentTick) => unchecked((int)(currentTick - lastInputTick)) >= 30000;

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [LibraryImport("psapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int EmptyWorkingSet(nint process);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetLastInputInfo(ref LastInputInfo input);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetTickCount();
}