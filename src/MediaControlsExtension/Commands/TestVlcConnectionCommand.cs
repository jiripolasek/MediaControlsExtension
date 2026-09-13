// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Vlc;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class TestVlcConnectionCommand : AsyncInvokableCommand
{
    private readonly Func<VlcConnectionOptions> _getOptions;

    public TestVlcConnectionCommand(Func<VlcConnectionOptions> getOptions, ILoggerFactory loggerFactory)
        : base(loggerFactory)
    {
        this._getOptions = getOptions;
        this.Name = MediaSourcesPage.Text("TestConnection");
        this.Icon = new IconInfo("\uE9D9");
        this.Timeout = TimeSpan.FromSeconds(10);
    }

    protected override async Task<ICommandResult> InvokeAsync(CancellationToken cancellationToken)
    {
        var options = this._getOptions();
        await using var backend = new VlcBackend(() => options);
        await backend.StartAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await backend.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var message = this._getOptions() != options
            ? MediaSourcesPage.Text("TestSettingsChanged")
            : snapshot.Connection.Status == MediaConnectionStatus.Connected
                ? MediaSourcesPage.Text("TestConnected")
                : snapshot.Connection.DiagnosticMessage ?? MediaSourcesPage.Text("TestFailed");
        return ShowResult(message);
    }

    protected override ICommandResult CreateTimeoutResult() => ShowResult(MediaSourcesPage.Text("TestFailed"));

    private static CommandResult ShowResult(string message) => CommandResult.ShowToast(new ToastArgs
    {
        Message = message,
        Result = CommandResult.KeepOpen(),
    });
}