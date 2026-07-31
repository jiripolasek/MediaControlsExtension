// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media;
namespace JPSoftworks.MediaControlsExtension.Commands;

internal partial class StandaloneCurrentSessionCommand : MediaInvokableCommand
{
    private readonly IMediaService _mediaService;
    private readonly Task _initialization;
    private readonly MediaSessionOp _mediaSessionOp;

    protected StandaloneCurrentSessionCommand(
        IMediaService mediaService,
        Task initialization,
        MediaSessionOp mediaSessionOp,
        MediaCommandResultFactory resultFactory,
        ILoggerFactory loggerFactory)
        : base(resultFactory, loggerFactory)
    {
        this._mediaService = mediaService ?? throw new ArgumentNullException(nameof(mediaService));
        this._initialization = initialization ?? throw new ArgumentNullException(nameof(initialization));
        this._mediaSessionOp = mediaSessionOp ?? throw new ArgumentNullException(nameof(mediaSessionOp));
    }

    protected override async Task<ICommandResult> InvokeMediaAsync(CancellationToken cancellationToken)
    {
        using var diagnostics = new ExtensionOperationDiagnostics(
            $"fallback media command {this._mediaSessionOp.GetType().Name}",
            this.Logger);
        diagnostics.SetStage("awaiting media-service initialization");
        await this._initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        diagnostics.SetStage($"submitting {this._mediaSessionOp.Operation}");

        var message = await this._mediaSessionOp.InvokeAsync(
            this._mediaService,
            MediaCommandTarget.CurrentSession,
            cancellationToken).ConfigureAwait(false);
        diagnostics.SetStage("creating fallback command result");
        return this.CreateMediaCommandResult(message);
    }
}
