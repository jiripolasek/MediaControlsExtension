using System.Text.Json.Serialization;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting;

/// <summary>Managed activation values associated with one admitted command and worker binding.</summary>
public sealed record HostedSourceActivation(long CommandId, MediaBackendSessionTarget Target, string ApplicationId, string MediaTitle)
{
    /// <summary>Gets the worker-reported path copied by the owner from the current binding's snapshot, not the activation payload.</summary>
    [JsonIgnore]
    public string? ExecutablePath { get; init; }
}

/// <summary>Worker logging configuration supplied by the owner.</summary>
public sealed record WorkerLoggingOptions(string Directory, bool Detailed);

/// <summary>Owner callbacks available to a compiled backend factory.</summary>
public sealed class HostedBackendContext
{
    private readonly AsyncLocal<Invocation?> _invocation = new();
    private readonly Func<HostedSourceActivation, CancellationToken, Task<bool>> _activate;

    internal HostedBackendContext(bool canActivate, WorkerLoggingOptions? logging,
        Func<HostedSourceActivation, CancellationToken, Task<bool>> activate)
    {
        this.CanActivateSource = canActivate;
        this.Logging = logging;
        this._activate = activate;
    }

    /// <summary>Gets whether this connection negotiated an owner activation callback.</summary>
    public bool CanActivateSource { get; }
    /// <summary>Gets optional logging settings supplied by the owning extension.</summary>
    public WorkerLoggingOptions? Logging { get; }

    /// <summary>Requests activation for the current command; valid only during its backend ExecuteAsync call.</summary>
    public Task<bool> TryActivateSourceAsync(string applicationId, string mediaTitle, CancellationToken cancellationToken)
    {
        var invocation = this._invocation.Value;
        if (!this.CanActivateSource || invocation is null || invocation.Command.Operation != MediaOperation.ActivateSource ||
            Volatile.Read(ref invocation.Finished) != 0 || Interlocked.Exchange(ref invocation.CallbackUsed, 1) != 0)
        {
            throw new InvalidOperationException("No admitted source activation is available.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return this._activate(new(invocation.Id, new(invocation.Command.SessionId, invocation.Command.BindingGeneration),
            applicationId, mediaTitle), cancellationToken);
    }

    internal async Task<MediaBackendCommandResult> ExecuteAsync(IMediaBackend backend, long id, MediaBackendCommand command,
        CancellationToken cancellationToken)
    {
        var invocation = new Invocation(id, command);
        this._invocation.Value = invocation;
        try
        {
            return await backend.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref invocation.Finished, 1);
            this._invocation.Value = null;
        }
    }

    private sealed class Invocation(long id, MediaBackendCommand command)
    {
        public long Id { get; } = id;
        public MediaBackendCommand Command { get; } = command;
        public int CallbackUsed;
        public int Finished;
    }
}