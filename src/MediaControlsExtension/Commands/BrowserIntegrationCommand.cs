// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.BrowserIntegration;
using JPSoftworks.MediaControlsExtension.Pages;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class BrowserIntegrationCommand : AsyncInvokableCommand
{
    private readonly bool _remove;
    private readonly string _extensionIds;
    private readonly Action<bool> _setEnabled;
    private readonly Func<bool, string, CancellationToken, Task> _run;

    internal BrowserIntegrationCommand(bool remove, string extensionIds, Action<bool> setEnabled, ILoggerFactory loggerFactory,
        Func<bool, string, CancellationToken, Task>? run = null) : base(loggerFactory)
    {
        this._remove = remove;
        this._extensionIds = extensionIds;
        this._setEnabled = setEnabled;
        this._run = run ?? ((removeIntegration, ids, token) => PackagedBrowserIntegration.RunAsync(
            Path.Combine(AppContext.BaseDirectory, "BrowserCompanion"), removeIntegration, ids, token));
        this.Id = $"com.jpsoftworks.cmdpal.mediacontrols.browser.{(remove ? "remove" : "enable")}";
        this.Name = BrowserCompanionPage.Text(remove ? "Remove" : "Enable");
        this.Icon = new IconInfo(remove ? "\uE74D" : "\uE774");
        this.Timeout = TimeSpan.FromSeconds(25);
    }

    protected override async Task<ICommandResult> InvokeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await this._run(this._remove, this._extensionIds, cancellationToken).ConfigureAwait(false);
            this._setEnabled(!this._remove);
            return ShowResult(BrowserCompanionPage.Text(this._remove ? "Removed" : "Enabled"));
        }
        catch (OperationCanceledException)
        {
            return this.CreateTimeoutResult();
        }
        catch (Exception error)
        {
            ExtensionLog.UnexpectedError(this.Logger, error);
            return ShowResult(BrowserCompanionPage.Text("Failed") + " " + error.Message);
        }
    }

    protected override ICommandResult CreateTimeoutResult() => ShowResult(BrowserCompanionPage.Text("Timeout"));

    private static CommandResult ShowResult(string message) => CommandResult.ShowToast(new ToastArgs
    {
        Message = message,
        Result = CommandResult.KeepOpen(),
    });
}