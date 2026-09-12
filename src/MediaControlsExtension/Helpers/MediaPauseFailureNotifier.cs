// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal sealed partial class MediaPauseFailureNotifier : IDisposable
{
    private static readonly Action<ILogger, Exception?> NotificationFailed = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1, nameof(NotificationFailed)), "Could not show secondary pause feedback.");

    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly Func<bool> _isEnabled;
    private readonly Action<string> _notify;
    private readonly ILogger _logger;
    private int _disposed;

    public MediaPauseFailureNotifier(Func<bool> isEnabled, Action<string> notify, ILogger? logger = null)
    {
        this._isEnabled = isEnabled;
        this._notify = notify;
        this._logger = logger ?? NullLogger.Instance;
        this._shutdownToken = this._shutdown.Token;
    }

    public async Task ObserveAsync(Task<MediaCommandOutcome> completion)
    {
        if (Volatile.Read(ref this._disposed) != 0)
        {
            return;
        }

        try
        {
            var outcome = await completion.WaitAsync(this._shutdownToken).ConfigureAwait(false);
            if (Volatile.Read(ref this._disposed) == 0 && this._isEnabled() &&
                MediaCommandFeedback.GetPauseWarning(outcome) is { } message)
            {
                this._notify(message);
            }
        }
        catch (OperationCanceledException) when (this._shutdownToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            NotificationFailed(this._logger, ex);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref this._disposed, 1) == 0)
        {
            this._shutdown.Cancel();
            this._shutdown.Dispose();
        }
    }
}