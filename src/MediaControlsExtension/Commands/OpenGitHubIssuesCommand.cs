// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using Windows.System;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class OpenGitHubIssuesCommand : AsyncInvokableCommand
{
    private static readonly Uri GitHubIssuesUri = new(
        "https://github.com/jiripolasek/MediaControlsExtension/issues/new");

    public OpenGitHubIssuesCommand(ILoggerFactory loggerFactory)
        : base(loggerFactory)
    {
        this.Name = Strings.ReportProblem_OpenGitHub_Title!;
        this.Icon = Icons.OpenInNewWindow;
    }

    protected override async Task<ICommandResult> InvokeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var launched = await Launcher
                .LaunchUriAsync(GitHubIssuesUri)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            if (!launched)
            {
                ExtensionLog.GitHubIssuesLaunchRejected(this.Logger, GitHubIssuesUri);
                return FailureToast();
            }

            return CommandResult.KeepOpen();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ExtensionLog.GitHubIssuesLaunchFailed(this.Logger, GitHubIssuesUri, ex);
            return FailureToast();
        }
    }

    protected override ICommandResult CreateTimeoutResult() => FailureToast();

    private static CommandResult FailureToast()
    {
        return CommandResult.ShowToast(new ToastArgs
        {
            Message = Strings.ReportProblem_OpenGitHub_Failed!,
            Result = CommandResult.KeepOpen(),
        });
    }
}
