// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using Microsoft.Extensions.Logging;

namespace JPSoftworks.MediaControlsExtension.Services;

internal static partial class ExtensionLog
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Error,
        Message = "An unexpected extension error occurred.")]
    public static partial void UnexpectedError(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Error,
        Message = "{Message}")]
    public static partial void Error(ILogger logger, string message, Exception exception);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "{Message}")]
    public static partial void Warning(ILogger logger, string message);

    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Information,
        Message = "Creating a diagnostic log archive.")]
    public static partial void CreatingDiagnosticLogArchive(ILogger logger);

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Information,
        Message = "Diagnostic log archive saved to '{ArchivePath}' with {LogFileCount} log file(s).")]
    public static partial void DiagnosticLogArchiveCreated(
        ILogger logger,
        string archivePath,
        int logFileCount);

    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Error,
        Message = "Failed to create the diagnostic log archive.")]
    public static partial void DiagnosticLogArchiveFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1103,
        Level = LogLevel.Warning,
        Message = "Windows rejected the request to open the GitHub issues page '{IssuesUri}'.")]
    public static partial void GitHubIssuesLaunchRejected(ILogger logger, Uri issuesUri);

    [LoggerMessage(
        EventId = 1104,
        Level = LogLevel.Error,
        Message = "Failed to open the GitHub issues page '{IssuesUri}'.")]
    public static partial void GitHubIssuesLaunchFailed(
        ILogger logger,
        Uri issuesUri,
        Exception exception);
}
