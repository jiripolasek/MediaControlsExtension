using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.MediaHost;
using Microsoft.Extensions.Logging;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal static class WorkerApplicationTests
{
    private static readonly Action<ILogger, string, Exception?> SelectedFactory = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(1, "TestFactory"), "Selected {BackendId} factory.");

    public static IReadOnlyDictionary<string, WorkerBackendFactory> Factories { get; } =
        new Dictionary<string, WorkerBackendFactory>(StringComparer.Ordinal)
        {
            ["application-activation"] = static (context, logging) =>
            {
                SelectedFactory(logging.CreateLogger("Factory"), "application-activation", null);
                return new SyntheticBackend("activation", context);
            },
            ["application-plain"] = static (_, logging) =>
            {
                SelectedFactory(logging.CreateLogger("Factory"), "application-plain", null);
                return new PlainBackend();
            },
        };

    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string directory) =>
    [
        ("Worker application rejects invalid arguments and unknown factories", InvalidLaunchAsync),
        ("Worker application does not construct a backend before a valid handshake", InvalidHandshakeAsync),
        ("Worker application dispatches an activation backend and preserves owner lifetime", () => DispatchAsync("application-activation", directory)),
        ("Worker application dispatches a plain backend without activation", () => DispatchAsync("application-plain", directory)),
    ];

    private static async Task InvalidLaunchAsync()
    {
        var owner = Guid.NewGuid().ToString("D");
        var process = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        string[][] arguments =
        [
            [],
            ["--worker"],
            ["--other", "pipe", owner, process, "application-plain"],
            ["--worker", " ", owner, process, "application-plain"],
            ["--worker", "pipe", "invalid", process, "application-plain"],
            ["--worker", "pipe", owner, "0", "application-plain"],
            ["--worker", "pipe", owner, "-1", "application-plain"],
            ["--worker", "pipe", owner, "999999999999", "application-plain"],
            ["--worker", "pipe", owner, process, "unknown"],
            ["--worker", "pipe", owner, process, "APPLICATION-PLAIN"],
            ["--worker", "pipe", owner, process, "application-plain", "extra"],
        ];
        foreach (var args in arguments)
        {
            var code = await WorkerApplication.RunAsync(args, Factories).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            HostingTests.Check(code == 3, "An invalid worker launch did not return the startup-failure code.");
        }
    }

    private static async Task InvalidHandshakeAsync()
    {
        var name = $"MediaHost-application-test-{Guid.NewGuid():N}";
        var owner = Guid.NewGuid();
        var created = 0;
        var factories = new Dictionary<string, WorkerBackendFactory>(StringComparer.Ordinal)
        {
            ["known"] = (_, _) => { Interlocked.Increment(ref created); return new PlainBackend(); },
        };
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var run = WorkerApplication.RunAsync(["--worker", name, owner.ToString("D"),
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "known"], factories);
        await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        using var protocol = new PipeProtocol(pipe);
        await protocol.WriteAsync(new(MessageKind.Hello, owner, Guid.Empty)
        {
            BackendId = "different",
            Policy = SourcePolicyMessage.FromPolicy(MediaBackendSourcePolicy.Empty),
        }, CancellationToken.None).ConfigureAwait(false);
        HostingTests.Check(await run.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false) == 3 && created == 0,
            "A rejected handshake constructed a backend or returned success.");
    }

    private static async Task DispatchAsync(string id, string directory)
    {
        var logDirectory = Path.Combine(directory, $"{id}-{Guid.NewGuid():N}");
        var activations = 0;
        await using var owner = new MediaWorkerOwner();
        var backend = new OutOfProcessMediaBackend(Program.Options(id) with
        {
            Logging = new(logDirectory, false),
            ActivateSource = (request, _) =>
            {
                HostingTests.Check(request.ApplicationId == "spike.player", "The factory lost the source activation context.");
                Interlocked.Increment(ref activations);
                return Task.FromResult(true);
            },
        }, owner);
        await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
        var snapshot = await HostingTests.WaitForSnapshotAsync(backend).ConfigureAwait(false);
        var canActivate = id == "application-activation";
        HostingTests.Check(snapshot.Sessions[0].Capabilities.HasFlag(MediaCapabilities.ActivateSource) == canActivate,
            "The worker did not dispatch the requested backend factory.");
        HostingTests.Check((await backend.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.Play), CancellationToken.None).ConfigureAwait(false)).Status == MediaBackendCommandStatus.Completed,
            "The selected worker backend could not play.");
        HostingTests.Check(await backend.GetArtworkAsync(snapshot.Sessions[0].MediaProperties.Artwork!.Value, CancellationToken.None).ConfigureAwait(false) is not null,
            "The selected worker backend lost its artwork.");
        if (canActivate)
        {
            HostingTests.Check((await backend.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.ActivateSource), CancellationToken.None).ConfigureAwait(false)).Status == MediaBackendCommandStatus.Completed && activations == 1,
                "The factory did not receive a usable owner callback.");
        }

        using var worker = Process.GetProcessById(backend.WorkerProcessId);
        _ = worker.Handle;
        await owner.DisposeAsync().ConfigureAwait(false);
        HostingTests.Check(worker.HasExited && worker.ExitCode == 0, "The worker application did not exit gracefully with its owner scope.");
        HostingTests.Check(Directory.GetFiles(logDirectory, "log-worker-*.txt").Any(file => File.ReadAllText(file).Contains($"Selected {id} factory.", StringComparison.Ordinal)),
            "The selected factory did not receive the configured logger factory.");
    }
}