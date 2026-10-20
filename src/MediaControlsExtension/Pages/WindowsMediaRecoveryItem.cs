// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed partial class WindowsMediaRecoveryItem : ListItem
{
    private readonly IMediaService _mediaService;
    private readonly Func<bool> _isWindowsMediaEnabled;

    public WindowsMediaRecoveryItem(IMediaService mediaService, Func<bool> isWindowsMediaEnabled,
        Action enableWindowsMedia, ICommand mediaSourcesPage, ILoggerFactory loggerFactory)
    {
        this._mediaService = mediaService;
        this._isWindowsMediaEnabled = isWindowsMediaEnabled;
        this.Command = new EnableWindowsMediaCommand(this, enableWindowsMedia, loggerFactory.CreateLogger<WindowsMediaRecoveryItem>());
        this.Title = Text("Title");
        this.Subtitle = Text("Description");
        this.Icon = Icons.WindowsMediaSource;
        this.MoreCommands = [new CommandContextItem(mediaSourcesPage)];
        this.Details = new Details
        {
            Title = this.Title,
            Body = this.Subtitle,
            Metadata =
            [
                new DetailsElement
                {
                    Key = MediaSourcesPage.Text("Actions"),
                    Data = new DetailsCommands { Commands = [this.Command, mediaSourcesPage] },
                },
            ],
        };
    }

    public bool IsNeeded => this._mediaService.Status is not (MediaServiceStatus.Stopped or MediaServiceStatus.Starting) &&
        this._mediaService.Sessions.IsEmpty && !this._isWindowsMediaEnabled();

    private static string Text(string key) => Strings.ResourceManager.GetString($"WindowsMediaRecovery_{key}", Strings.Culture) ?? key;

    private sealed partial class EnableWindowsMediaCommand : InvokableCommand
    {
        private static readonly Action<ILogger, Exception?> SaveFailed = LoggerMessage.Define(
            LogLevel.Warning, new EventId(1, nameof(SaveFailed)), "Could not enable the recommended Windows media source.");
        private readonly WindowsMediaRecoveryItem _item;
        private readonly Action _enableWindowsMedia;
        private readonly ILogger _logger;

        public EnableWindowsMediaCommand(WindowsMediaRecoveryItem item, Action enableWindowsMedia, ILogger logger)
        {
            this._item = item;
            this._enableWindowsMedia = enableWindowsMedia;
            this._logger = logger;
            this.Id = "com.jpsoftworks.cmdpal.mediacontrols.enable-windows-media";
            this.Name = Text("Enable");
            this.Icon = Icons.EnableMediaSource;
        }

        public override ICommandResult Invoke()
        {
            try
            {
                if (this._item.IsNeeded)
                {
                    this._enableWindowsMedia();
                }

                return CommandResult.KeepOpen();
            }
            catch (Exception ex)
            {
                SaveFailed(this._logger, ex);
                return CommandResult.ShowToast(new ToastArgs
                {
                    Message = MediaSourcesPage.Text("SaveFailed"),
                    Result = CommandResult.KeepOpen(),
                });
            }
        }
    }
}