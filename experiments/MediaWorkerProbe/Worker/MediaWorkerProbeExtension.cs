// ------------------------------------------------------------
//
// Copyright (c) Jiri Polasek. All rights reserved.
//
// ------------------------------------------------------------

using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CommandPalette.Extensions;
using Microsoft.Extensions.Logging;
using Windows.Media.Control;

namespace JPSoftworks.MediaControlsExtension.MediaWorkerProbe;

/// <summary>Validates packaged GSMTC activation and recovery in a separate experimental executable.</summary>
[Guid("f47a961b-a78e-4523-9c9e-ef9bf5e88706")]
public sealed partial class MediaWorkerProbeExtension : IExtension, IDisposable
{
    internal const string PipeName = @"LOCAL\JPSoftworks.MediaControlsExtension.MediaWorkerProbe";

    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorSuccess = 0;
    private const int ProbeTimeoutSeconds = 15;

    private readonly ManualResetEvent _extensionDisposedEvent;
    private readonly ILogger _logger;
    private readonly Guid _workerEpoch = Guid.NewGuid();

    public MediaWorkerProbeExtension(
        ManualResetEvent extensionDisposedEvent,
        ILogger<MediaWorkerProbeExtension> logger)
    {
        this._extensionDisposedEvent = extensionDisposedEvent
            ?? throw new ArgumentNullException(nameof(extensionDisposedEvent));
        this._logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ = this.RunProbeAsync();
    }

    public object? GetProvider(ProviderType providerType) => null;

    public void Dispose()
    {
        this._extensionDisposedEvent.Set();
    }

    private async Task RunProbeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(ProbeTimeoutSeconds));
        using var pipe = new NamedPipeClientStream(
            ".",
            PipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(timeout.Token);

            var packageFullName = ReadIdentityString(GetCurrentPackageFullName);
            var appUserModelId = ReadIdentityString(GetCurrentApplicationUserModelId);

            GlobalSystemMediaTransportControlsSessionManager? manager = null;
            try
            {
                manager = await AcquireManagerAsync().WaitAsync(timeout.Token);
                var sessionCount = manager.GetSessions().Count;
                await WriteResultAsync(
                    pipe,
                    new MediaWorkerProbeResult(
                        ProtocolVersion: 1,
                        WorkerEpoch: this._workerEpoch,
                        WorkerProcessId: Environment.ProcessId,
                        PackageFullName: packageFullName,
                        AppUserModelId: appUserModelId,
                        GsmtcManagerAcquired: true,
                        SessionCount: sessionCount,
                        Error: null),
                    timeout.Token);
            }
            finally
            {
                GC.KeepAlive(manager);
            }
        }
        catch (Exception ex)
        {
            LogProbeError(this._logger, "The packaged media-worker probe failed.", ex);

            if (pipe.IsConnected)
            {
                try
                {
                    await WriteResultAsync(
                        pipe,
                        new MediaWorkerProbeResult(
                            ProtocolVersion: 1,
                            WorkerEpoch: this._workerEpoch,
                            WorkerProcessId: Environment.ProcessId,
                            PackageFullName: TryReadIdentityString(GetCurrentPackageFullName),
                            AppUserModelId: TryReadIdentityString(GetCurrentApplicationUserModelId),
                            GsmtcManagerAcquired: false,
                            SessionCount: null,
                            Error: $"{ex.GetType().Name}: {ex.Message}"),
                        CancellationToken.None);
                }
                catch (Exception reportException)
                {
                    LogProbeError(
                        this._logger,
                        "The packaged media-worker probe could not report its failure.",
                        reportException);
                }
            }

            this._extensionDisposedEvent.Set();
        }
    }

    private static async Task<GlobalSystemMediaTransportControlsSessionManager> AcquireManagerAsync()
    {
        return await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
    }

    private static async Task WriteResultAsync(
        Stream stream,
        MediaWorkerProbeResult result,
        CancellationToken cancellationToken)
    {
        await JsonSerializer.SerializeAsync(
            stream,
            result,
            MediaWorkerProbeJsonContext.Default.MediaWorkerProbeResult,
            cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static string? TryReadIdentityString(IdentityReader reader)
    {
        try
        {
            return ReadIdentityString(reader);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadIdentityString(IdentityReader reader)
    {
        uint length = 0;
        var result = reader(ref length, IntPtr.Zero);
        if (result != ErrorInsufficientBuffer)
        {
            return result == ErrorSuccess ? string.Empty : null;
        }

        var buffer = Marshal.AllocHGlobal(checked((int)length * sizeof(char)));
        try
        {
            result = reader(ref length, buffer);
            if (result != ErrorSuccess)
            {
                throw new Win32Exception(result);
            }

            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private delegate int IdentityReader(ref uint length, IntPtr value);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Message}")]
    private static partial void LogProbeError(ILogger logger, string message, Exception exception);

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentApplicationUserModelId(ref uint applicationUserModelIdLength, IntPtr applicationUserModelId);
}

internal sealed record MediaWorkerProbeResult(
    int ProtocolVersion,
    Guid WorkerEpoch,
    int WorkerProcessId,
    string? PackageFullName,
    string? AppUserModelId,
    bool GsmtcManagerAcquired,
    int? SessionCount,
    string? Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MediaWorkerProbeResult))]
internal sealed partial class MediaWorkerProbeJsonContext : JsonSerializerContext
{
}