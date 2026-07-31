// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class SaveDiagnosticLogsCommand : AsyncInvokableCommand
{
    private readonly DiagnosticLogArchiveService _archiveService;

    public SaveDiagnosticLogsCommand(
        DiagnosticLogArchiveService archiveService,
        ILoggerFactory loggerFactory)
        : base(loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(archiveService);

        this._archiveService = archiveService;
        this.Name = Strings.ReportProblem_SaveLogs_Title!;
        this.Icon = Icons.Save;
    }

    protected override async Task<ICommandResult> InvokeAsync(CancellationToken cancellationToken)
    {
        try
        {
            ExtensionLog.CreatingDiagnosticLogArchive(this.Logger);
            var result = await this._archiveService
                .CreateArchiveOnDesktopAsync(cancellationToken)
                .ConfigureAwait(false);
            ExtensionLog.DiagnosticLogArchiveCreated(
                this.Logger,
                result.ArchivePath,
                result.LogFileCount);

            return Toast(
                Strings.ReportProblem_SaveLogs_Succeeded!.Replace(
                    "{0}",
                    Path.GetFileName(result.ArchivePath),
                    StringComparison.Ordinal));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ExtensionLog.DiagnosticLogArchiveFailed(this.Logger, ex);
            return Toast(Strings.ReportProblem_SaveLogs_Failed!);
        }
    }

    protected override ICommandResult CreateTimeoutResult()
        => Toast(Strings.ReportProblem_SaveLogs_Failed!);

    private static CommandResult Toast(string message)
    {
        return CommandResult.ShowToast(new ToastArgs
        {
            Message = message,
            Result = CommandResult.KeepOpen(),
        });
    }
}
