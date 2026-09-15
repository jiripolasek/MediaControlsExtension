using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;
using WinRT;

namespace JPSoftworks.MediaControlsExtension.Package.Tests;

internal static partial class Program
{
    private const string SourcesId = "com.jpsoftworks.cmdpal.mediacontrols.sources";

    [MTAThread]
    private static int Main(string[] args)
    {
        if (args is not [var results, var ownerPath, var workerPath])
        {
            return 2;
        }

        Directory.CreateDirectory(results);
        var evidence = new List<string>();
        try
        {
            RunAsync(Path.GetFullPath(ownerPath), Path.GetFullPath(workerPath), evidence).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            evidence.Add($"FAIL {ex}");
            return 1;
        }
        finally
        {
            File.WriteAllLines(Path.Combine(results, "commands.txt"), evidence);
        }
    }

    private static async Task RunAsync(string ownerPath, string workerPath, List<string> evidence)
    {
        var existingOwners = FindProcesses(ownerPath);
        foreach (var existing in existingOwners) { existing.Dispose(); }
        Check(existingOwners.Length == 0, "Stop the existing extension before this check.");
        var existingWorkers = FindProcesses(workerPath);
        var previousWorkers = existingWorkers.Select(static process => process.Id).ToHashSet();
        foreach (var existing in existingWorkers) { existing.Dispose(); }
        var clsid = new Guid("502f0b1d-b778-450c-9803-6c09cb0e6407");
        var iid = typeof(IExtension).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(in clsid, 0, 4, in iid, out var pointer));
        IExtension extension;
        try { extension = MarshalInterface<IExtension>.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
        try
        {
            var provider = (ICommandProvider)extension.GetProvider(ProviderType.Commands);
            var sources = (IListPage)provider.TopLevelCommands().SelectMany(static item => item.MoreCommands)
                .OfType<ICommandItem>().Single(static item => item.Command.Id == SourcesId).Command;
            var media = (IListPage)provider.TopLevelCommands().Single(static item => item.Command.Id == "com.jpsoftworks.cmdpal.mediacontrols").Command;
            sources.ItemsChanged += (_, _) => { };
            var internalRow = FindRow(sources, "gsmtc");
            var workerRow = FindRow(sources, "gsmtc.worker");
            var internalChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var workerChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            internalRow.Command.PropChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ICommand.Name)) { internalChanged.TrySetResult(); }
            };
            workerRow.Command.PropChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ICommand.Name)) { workerChanged.TrySetResult(); }
            };
            var enable = workerRow.Command.Name;
            var disable = internalRow.Command.Name;
            Check(enable != disable, "Start with internal GSMTC enabled and the worker disabled.");
            using var owner = FindProcesses(ownerPath).Single();
            _ = owner.Handle;
            Invoke(workerRow);
            using var firstWorker = await WaitWorkerAsync(workerPath, previousWorkers).ConfigureAwait(false);
            await Task.WhenAll(internalChanged.Task, workerChanged.Task).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Check(internalRow.Command.Name == enable && workerRow.Command.Name == disable,
                "The existing commands did not reflect the published selection.");
            await CheckRowsAsync(sources, enable, disable, workerSelected: true).ConfigureAwait(false);
            evidence.Add($"PASS live internal-to-worker command: owner {owner.Id}, worker {firstWorker.Id}; both existing commands notified and updated");

            previousWorkers.Add(firstWorker.Id);
            Check(FindRow(sources, "dummy.worker").Command.Name == enable, "Start with dummy media disabled.");
            Invoke(FindRow(sources, "dummy.worker"));
            using var firstDummy = await WaitWorkerAsync(workerPath, previousWorkers).ConfigureAwait(false);
            await CheckDummyRowsAsync(media, 3).ConfigureAwait(false);
            Check(!firstWorker.HasExited && FindRow(sources, "gsmtc.worker").Command.Name == disable,
                "Enabling dummy media disabled GSMTC.");
            Invoke(FindRow(sources, "dummy.worker"));
            await firstDummy.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)).ConfigureAwait(false);
            await CheckDummyRowsAsync(media, 0).ConfigureAwait(false);
            Check(!firstWorker.HasExited && !owner.HasExited, "Disabling dummy media terminated GSMTC or its owner.");
            previousWorkers.Add(firstDummy.Id);
            evidence.Add($"PASS dummy worker {firstDummy.Id} published three media rows alongside GSMTC; disabling it removed only its rows and worker");

            Invoke(FindRow(sources, "gsmtc"));
            await firstWorker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)).ConfigureAwait(false);
            Check(!owner.HasExited, "Switching modes terminated the extension process.");
            await CheckRowsAsync(sources, enable, disable, workerSelected: false).ConfigureAwait(false);
            evidence.Add("PASS live worker-to-internal command exited the worker while the extension remained alive");

            previousWorkers.Add(firstWorker.Id);
            Invoke(FindRow(sources, "gsmtc.worker"));
            using var secondWorker = await WaitWorkerAsync(workerPath, previousWorkers).ConfigureAwait(false);
            await CheckRowsAsync(sources, enable, disable, workerSelected: true).ConfigureAwait(false);
            previousWorkers.Add(secondWorker.Id);
            Invoke(FindRow(sources, "dummy.worker"));
            using var secondDummy = await WaitWorkerAsync(workerPath, previousWorkers).ConfigureAwait(false);
            await CheckDummyRowsAsync(media, 3).ConfigureAwait(false);
            provider.Dispose();
            await secondWorker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)).ConfigureAwait(false);
            await secondDummy.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)).ConfigureAwait(false);
            Check(!owner.HasExited && extension.GetProvider(ProviderType.Commands) is not null,
                "The extension process did not survive provider disposal.");
            evidence.Add($"PASS actual provider disposal exited worker {secondWorker.Id}; extension owner {owner.Id} remained alive and callable");
            evidence.Add($"PASS the same provider disposal also exited dummy worker {secondDummy.Id}");
        }
        finally
        {
            extension.Dispose();
        }
    }

    private static IListItem FindRow(IListPage sources, string id) => sources.GetItems()
        .Single(item => item.Command.Id == $"{SourcesId}.{id}.enablement");

    private static async Task CheckDummyRowsAsync(IListPage media, int count)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (media.GetItems().Count(static item => item.Subtitle.Contains("Dummy player ", StringComparison.Ordinal)) == count) { return; }
            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException($"The media page did not publish {count} dummy rows.");
    }

    private static async Task CheckRowsAsync(IListPage sources, string enable, string disable, bool workerSelected)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (FindRow(sources, "gsmtc").Command.Name == (workerSelected ? enable : disable) &&
                FindRow(sources, "gsmtc.worker").Command.Name == (workerSelected ? disable : enable))
            {
                return;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"The GSMTC rows did not settle: internal {FindRow(sources, "gsmtc").Subtitle}; worker {FindRow(sources, "gsmtc.worker").Subtitle}.");
    }

    private static void Invoke(IListItem row) => Check(((IInvokableCommand)row.Command).Invoke(row).Kind == CommandResultKind.KeepOpen,
        "The source toggle reported a save failure.");

    private static async Task<Process> WaitWorkerAsync(string path, HashSet<int> previous)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            var workers = FindProcesses(path);
            var current = workers.Where(process => !previous.Contains(process.Id)).ToArray();
            foreach (var worker in workers.Except(current)) { worker.Dispose(); }
            if (current.Length == 1)
            {
                _ = current[0].Handle;
                return current[0];
            }

            foreach (var worker in current) { worker.Dispose(); }
            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException("The live command did not start exactly one new worker.");
    }

    private static Process[] FindProcesses(string path)
    {
        var matches = new List<Process>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(process);
                    continue;
                }
            }
            catch (InvalidOperationException) { }
            process.Dispose();
        }

        return [.. matches];
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
}