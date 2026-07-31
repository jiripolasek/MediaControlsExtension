// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Globalization;
using System.IO.Compression;

namespace JPSoftworks.MediaControlsExtension.Services;

internal sealed class DiagnosticLogArchiveService
{
    private const string LogFilePattern = "log*.txt";

    private readonly string _logDirectoryPath;

    public DiagnosticLogArchiveService(string logDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectoryPath);
        this._logDirectoryPath = Path.GetFullPath(logDirectoryPath);
    }

    public Task<DiagnosticLogArchiveResult> CreateArchiveOnDesktopAsync(
        CancellationToken cancellationToken)
    {
        return this.CreateArchiveAsync(GetDesktopDirectoryPath(), cancellationToken);
    }

    internal async Task<DiagnosticLogArchiveResult> CreateArchiveAsync(
        string destinationDirectoryPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectoryPath);

        var destinationDirectory = Path.GetFullPath(destinationDirectoryPath);
        Directory.CreateDirectory(destinationDirectory);
        Directory.CreateDirectory(this._logDirectoryPath);

        var logFilePaths = Directory
            .EnumerateFiles(this._logDirectoryPath, LogFilePattern, SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var archiveFileName = string.Format(
            CultureInfo.InvariantCulture,
            "MediaControlsExtension-Logs-{0:yyyyMMdd-HHmmssfff}.zip",
            DateTimeOffset.Now);
        var archivePath = Path.Combine(destinationDirectory, archiveFileName);

        try
        {
            await using var archiveStream = new FileStream(
                archivePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create);

            foreach (var logFilePath in logFilePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entry = archive.CreateEntry(
                    Path.GetFileName(logFilePath),
                    CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await using var logStream = new FileStream(
                    logFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await logStream.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
            }

            return new DiagnosticLogArchiveResult(archivePath, logFilePaths.Length);
        }
        catch
        {
            TryDeleteIncompleteArchive(archivePath);
            throw;
        }
    }

    private static string GetDesktopDirectoryPath()
    {
        var desktop = Environment.GetFolderPath(
            Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolderOption.DoNotVerify);
        if (!string.IsNullOrWhiteSpace(desktop))
        {
            return desktop;
        }

        var userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify);
        return !string.IsNullOrWhiteSpace(userProfile)
            ? userProfile
            : throw new InvalidOperationException("The Desktop and user profile directories are unavailable.");
    }

    private static void TryDeleteIncompleteArchive(string archivePath)
    {
        try
        {
            File.Delete(archivePath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal readonly record struct DiagnosticLogArchiveResult(
    string ArchivePath,
    int LogFileCount);
