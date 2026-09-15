using System.Collections.Immutable;
using System.Text.Json.Serialization;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using PolyType;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting;

internal sealed record SourcePolicyMessage(long Revision, string[] ExcludedApplicationIds)
{
    public static SourcePolicyMessage FromPolicy(MediaBackendSourcePolicy policy) =>
        new(policy.Revision, [.. policy.ExcludedApplicationIds]);

    public MediaBackendSourcePolicy ToPolicy() => new(this.Revision, this.ExcludedApplicationIds);
}

internal sealed record WorkerHello(
    int Version,
    Guid Owner,
    string BackendId,
    SourcePolicyMessage Policy,
    bool CanActivateSource,
    WorkerLoggingOptions? Logging,
    TimeSpan RequestTimeout,
    TimeSpan ObservationTimeout,
    TimeSpan PolicyTimeout);

internal sealed record WorkerWelcome(int Version, Guid Owner, Guid Epoch, bool CanActivateSource);

internal sealed record WorkerSnapshot(Guid Epoch, MediaBackendSnapshot Snapshot);

internal sealed record WorkerSnapshotFailure(Guid Epoch, long SourcePolicyRevision, string Error);

internal sealed record WorkerFault(Guid Epoch, string Error);

internal sealed record WorkerCommand(Guid Epoch, long CommandId, MediaBackendCommand Command);

internal sealed record WorkerActivation(Guid Epoch, HostedSourceActivation Activation);

internal sealed record WorkerArtwork(string ContentType, int Length, string? ContentHash);

[JsonRpcContract] [GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IWorkerRpc
{
    Task<WorkerWelcome> InitializeAsync(WorkerHello hello, CancellationToken cancellationToken);
    Task<MediaBackendCommandResult> ExecuteAsync(WorkerCommand request, CancellationToken cancellationToken);
    Task<long> ApplyPolicyAsync(Guid epoch, SourcePolicyMessage policy, CancellationToken cancellationToken);

    Task InvalidateAsync(
        Guid epoch,
        ImmutableArray<MediaBackendObservationRequest> requests,
        CancellationToken cancellationToken);

    Task<WorkerArtwork?> CopyArtworkAsync(
        Guid epoch,
        MediaArtworkKey key,
        Stream destination,
        CancellationToken cancellationToken);
}

[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IWorkerNotifications
{
    void Begin(Guid epoch);
    void Shutdown(Guid epoch);
}

[JsonRpcContract] [GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IOwnerRpc
{
    Task<bool> ActivateSourceAsync(WorkerActivation request, CancellationToken cancellationToken);
    Task ReportFaultAsync(WorkerFault fault, CancellationToken cancellationToken);
}

[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IOwnerNotifications
{
    void Snapshot(WorkerSnapshot notice);
    void SnapshotFailed(WorkerSnapshotFailure failure);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkerHello))]
[JsonSerializable(typeof(WorkerWelcome))]
[JsonSerializable(typeof(WorkerSnapshot))]
[JsonSerializable(typeof(WorkerSnapshotFailure))]
[JsonSerializable(typeof(WorkerFault))]
[JsonSerializable(typeof(WorkerCommand))]
[JsonSerializable(typeof(WorkerActivation))]
[JsonSerializable(typeof(WorkerArtwork))]
[JsonSerializable(typeof(SourcePolicyMessage))]
[JsonSerializable(typeof(MediaBackendCommandResult))]
[JsonSerializable(typeof(MediaArtworkKey))]
[JsonSerializable(typeof(ImmutableArray<MediaBackendObservationRequest>))]
[JsonSerializable(typeof(Stream))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(CommonErrorData))]
internal sealed partial class WorkerJsonContext : JsonSerializerContext;