using System.Diagnostics;
using System.Globalization;
using JPSoftworks.MediaControlsExtension.Media;
using JPSoftworks.MediaControlsExtension.Media.Hosting;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.MediaHost;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting.Tests;

internal static class Program
{
    [MTAThread]
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["--wait-for-release", var releaseName])
            {
                using var release = EventWaitHandle.OpenExisting(releaseName);
                return release.WaitOne(TimeSpan.FromSeconds(30)) ? 0 : 2;
            }

            if (args is ["--dummy-check", var dummyResults, var dummyWorker])
            {
                Directory.CreateDirectory(dummyResults);
                try
                {
                    await DummyBackendTests.WorkerAsync(dummyWorker).ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(dummyResults, "dummy-result.txt"),
                        "PASS production dummy worker: three sessions, controls, artwork, package identity and owner lifetime.").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await File.WriteAllTextAsync(Path.Combine(dummyResults, "dummy-result.txt"), $"FAIL {ex}").ConfigureAwait(false);
                    throw;
                }
                return 0;
            }

            if (args is ["--memory-check", var memoryResults, var memoryCycles])
            {
                await NativeChecks.RunMemoryAsync(memoryResults, int.Parse(memoryCycles, CultureInfo.InvariantCulture)).ConfigureAwait(false);
                return 0;
            }

            if (args is ["--native-player", var title])
            {
                await NativeChecks.RunPlayerAsync(title).ConfigureAwait(false);
                return 0;
            }

            if (args is ["--native-check", var results, var workerExecutable, var minutes, var cycles, .. var interval] && interval.Length <= 1)
            {
                try
                {
                    await NativeChecks.RunAsync(results, workerExecutable, int.Parse(minutes, CultureInfo.InvariantCulture),
                        int.Parse(cycles, CultureInfo.InvariantCulture), interval.Length == 0 ? 15 : int.Parse(interval[0], CultureInfo.InvariantCulture)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Directory.CreateDirectory(results);
                    await File.WriteAllTextAsync(Path.Combine(results, "native-result.txt"), $"FAIL {ex}").ConfigureAwait(false);
                    throw;
                }
                return 0;
            }

            if (args is ["--worker", var pipe, var owner, var ownerPid, var backendId])
            {
                if (backendId.StartsWith("review-partial-request-", StringComparison.Ordinal))
                {
                    await ReviewRecoveryTests.RunPartialRequestPeerAsync(pipe, Guid.Parse(owner),
                        int.Parse(ownerPid, CultureInfo.InvariantCulture), backendId).ConfigureAwait(false);
                    Environment.Exit(0);
                }
                if (backendId == "review-admission")
                {
                    await ReviewAdmissionPeer.RunAsync(pipe, Guid.Parse(owner), int.Parse(ownerPid, CultureInfo.InvariantCulture)).ConfigureAwait(false);
                    Environment.Exit(0);
                }
                if (backendId == "review-late-activation")
                {
                    await ReviewActivationPeer.RunAsync(pipe, Guid.Parse(owner), int.Parse(ownerPid, CultureInfo.InvariantCulture)).ConfigureAwait(false);
                    Environment.Exit(0);
                }
                if (WorkerBackendCatalog.Factories.ContainsKey(backendId))
                {
                    Environment.Exit(await WorkerApplication.RunAsync(args, WorkerBackendCatalog.Factories).ConfigureAwait(false));
                }

                if (WorkerApplicationTests.Factories.ContainsKey(backendId))
                {
                    Environment.Exit(await WorkerApplication.RunAsync(args, WorkerApplicationTests.Factories).ConfigureAwait(false));
                }

                var code = await MediaBackendHost.RunAsync(pipe, Guid.Parse(owner), int.Parse(ownerPid, CultureInfo.InvariantCulture),
                    backendId, context => CreateBackend(backendId, context), TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                Environment.Exit(code);
            }

            if (args is ["--lifetime-owner", var behavior, var report])
            {
                await using var backend = new OutOfProcessMediaBackend(Options(behavior));
                await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
                await HostingTests.EventuallyAsync(() => backend.WorkerProcessId != 0).ConfigureAwait(false);
                if (behavior != "hang-start")
                {
                    var snapshot = await HostingTests.WaitForSnapshotAsync(backend).ConfigureAwait(false);
                    if (behavior == "hang-command")
                    {
                        _ = backend.ExecuteAsync(HostingTests.Command(snapshot, MediaOperation.SkipNext), CancellationToken.None);
                        await HostingTests.EventuallyAsync(async () => (await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Sessions[0].MediaProperties.Title == "Next").ConfigureAwait(false);
                    }
                }

                await File.WriteAllTextAsync(report + ".tmp", backend.WorkerProcessId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                File.Move(report + ".tmp", report);
                await Task.Delay(Timeout.Infinite).ConfigureAwait(false);
                return 0;
            }

            if (args is ["--package-check", var directory])
            {
                Directory.CreateDirectory(directory);
                var result = "PASS";
                try
                {
                    if (OwnedWorkerProcess.CurrentPackageFullName is null)
                    {
                        throw new InvalidOperationException("The acceptance owner has no package identity.");
                    }

                    if (await HostingTests.RunAsync(directory).ConfigureAwait(false) != 0)
                    {
                        throw new InvalidOperationException("Packaged subprocess checks failed.");
                    }

                    await InspectGsmtcAsync(Path.Combine(directory, "gsmtc.txt")).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    result = $"FAIL {ex}";
                }

                await File.WriteAllTextAsync(Path.Combine(directory, "package-result.txt"), result).ConfigureAwait(false);
                return result == "PASS" ? 0 : 1;
            }

            if (args.Length >= 1 && args[0] == "--gsmtc")
            {
                await InspectGsmtcAsync(args.Length >= 2 ? args[1] : null).ConfigureAwait(false);
                return 0;
            }

            if (args.Length >= 1 && args[0] == "--self-test")
            {
                return await HostingTests.RunAsync(args.Length >= 2 ? args[1] : Path.Combine(AppContext.BaseDirectory, "TestResults")).ConfigureAwait(false);
            }

            Console.WriteLine("Use --self-test [results-directory] or --gsmtc [report-file].");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    internal static WorkerOptions Options(string backendId) => new(Environment.ProcessPath!, backendId);

    private static IMediaBackend CreateBackend(string backendId, HostedBackendContext context) => backendId switch
    {
        "plain" => new PlainBackend(),
        "synthetic" or "hang-start" or "hang-command" or "hang-cleanup" or "slow-artwork" or "cancel-command" or
            "large-artwork" or "oversized-artwork" or "activation" or "invalid-activation" or "delayed-activation" or
            "slow-start" or "slow-command" or "slow-policy" or "slow-cancel" or "quiet-artwork" or
            "transient-read-failure" or "startup-read-failure" or "persistent-read-failure" or "recurring-read-failure" or
            "short-read-failure" => new SyntheticBackend(backendId, context),
        _ => throw new ArgumentException("Unknown compiled backend factory.", nameof(backendId)),
    };

    private static async Task InspectGsmtcAsync(string? reportFile)
    {
        await using var backend = new OutOfProcessMediaBackend(Options("gsmtc"));
        await backend.StartAsync(CancellationToken.None).ConfigureAwait(false);
        MediaBackendSnapshot snapshot;
        try
        {
            snapshot = await HostingTests.WaitForSnapshotAsync(backend, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            var last = await backend.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException($"GSMTC did not become available: {last.Connection.Status}; {last.Connection.DiagnosticMessage}", ex);
        }

        if (OwnedWorkerProcess.CurrentPackageFullName != backend.WorkerPackageFullName)
        {
            throw new InvalidOperationException("The worker did not inherit its owner's package identity.");
        }
        var lines = new List<string>
        {
            $"Owner PID: {Environment.ProcessId}",
            $"Worker PID: {backend.WorkerProcessId}",
            $"Worker epoch: {backend.WorkerEpoch}",
            $"Owner package: {OwnedWorkerProcess.CurrentPackageFullName ?? "unpackaged"}",
            $"Worker package (kernel): {backend.WorkerPackageFullName ?? "unpackaged"}",
            $"Connection: {snapshot.Connection.Status}",
            $"Sessions: {snapshot.Sessions.Length}",
        };
        foreach (var session in snapshot.Sessions)
        {
            var artwork = session.MediaProperties.Artwork is { } key
                ? await backend.GetArtworkAsync(key, CancellationToken.None).ConfigureAwait(false) : null;
            lines.Add($"Session {session.Id.Value}: {session.MediaProperties.Source.NativeApplication?.ApplicationId}; {session.PlaybackState}; artwork bytes: {artwork?.Data.Length ?? 0}");
        }

        var text = string.Join(Environment.NewLine, lines);
        Console.WriteLine(text);
        if (reportFile is not null)
        {
            await File.WriteAllTextAsync(reportFile, text).ConfigureAwait(false);
        }
    }
}