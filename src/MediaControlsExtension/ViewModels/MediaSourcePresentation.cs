// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media;

namespace JPSoftworks.MediaControlsExtension.ViewModels;

internal sealed record MediaApplicationPresentation(string? DisplayName, string? IconPath);

internal sealed record MediaSourcePresentationSnapshot(MediaSourceSnapshot Source, string DisplayName, string? IconPath);

/// <summary>Enriches missing source presentation with asynchronously resolved native application details.</summary>
internal sealed partial class MediaSourcePresentation(
    Func<string, Task<MediaApplicationPresentation>> resolveApplication,
    Action<Exception> reportException) : IDisposable
{
    private readonly Lock _stateLock = new();
    private MediaSourcePresentationSnapshot _state = new(new(), string.Empty, null);
    private MediaNativeApplicationIdentity? _application;
    private MediaApplicationPresentation? _resolvedApplication;
    private long _resolution;
    private Task _resolutionTask = Task.CompletedTask;
    private TaskCompletionSource _resolutionChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public event EventHandler? Changed;

    public MediaSourcePresentationSnapshot State => Volatile.Read(ref this._state);

    public Task UpdateAsync(MediaSourceSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (this._stateLock)
        {
            if (this._disposed)
            {
                return Task.CompletedTask;
            }

            var application = Known(source.DisplayName) is null || Known(source.IconPath) is null
                ? source.NativeApplication
                : null;
            var resolutionChanged = application != this._application;
            if (resolutionChanged)
            {
                this._application = application;
                this._resolvedApplication = null;
                var resolution = ++this._resolution;
                this._resolutionTask = application is null
                    ? Task.CompletedTask
                    : Task.Run(() => this.ResolveAsync(application, resolution));
            }

            if (this.PublishUnderLock(source) || resolutionChanged)
            {
                this._resolutionChanged.TrySetResult();
                this._resolutionChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return this._resolutionTask;
        }
    }

    public async ValueTask<string> GetDisplayNameAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task resolutionTask;
            Task changedTask;
            lock (this._stateLock)
            {
                if (this._disposed || this._resolutionTask.IsCompleted || Known(this.State.Source.DisplayName) is not null)
                {
                    return this.State.DisplayName;
                }

                resolutionTask = this._resolutionTask;
                changedTask = this._resolutionChanged.Task;
            }

            await Task.WhenAny(resolutionTask, changedTask).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        lock (this._stateLock)
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            this._resolution++;
            this._resolutionChanged.TrySetResult();
            this.Changed = null;
        }
    }

    private async Task ResolveAsync(MediaNativeApplicationIdentity application, long resolution)
    {
        try
        {
            var resolved = await resolveApplication(application.ApplicationId).ConfigureAwait(false);
            bool changed;
            lock (this._stateLock)
            {
                if (this._disposed || resolution != this._resolution)
                {
                    return;
                }

                this._resolvedApplication = resolved;
                changed = this.PublishUnderLock(this.State.Source);
            }

            if (changed)
            {
                this.RaiseChanged();
            }
        }
        catch (Exception ex)
        {
            lock (this._stateLock)
            {
                if (this._disposed || resolution != this._resolution)
                {
                    return;
                }
            }

            reportException(ex);
        }
    }

    private bool PublishUnderLock(MediaSourceSnapshot source)
    {
        var next = new MediaSourcePresentationSnapshot(
            source,
            Known(source.DisplayName) ?? Known(this._resolvedApplication?.DisplayName) ??
                Known(source.NativeApplication?.ApplicationId) ?? source.Provider?.DisplayName ?? string.Empty,
            Known(source.IconPath) ?? Known(this._resolvedApplication?.IconPath));
        if (next == this.State)
        {
            return false;
        }

        Volatile.Write(ref this._state, next);
        return true;
    }

    private void RaiseChanged()
    {
        if (Volatile.Read(ref this._disposed) || this.Changed is not { } handler)
        {
            return;
        }

        foreach (EventHandler subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                reportException(ex);
            }
        }
    }

    private static string? Known(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}