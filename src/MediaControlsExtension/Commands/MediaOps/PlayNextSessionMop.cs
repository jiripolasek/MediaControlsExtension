// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Globalization;
using System.Text;
using JPSoftworks.MediaControlsExtension.Media;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal abstract class PlayOtherSessionMop : MediaSessionOp
{
    private static readonly CompositeFormat s_switchedToFormat = CompositeFormat.Parse(Strings.Toast_SwitchedTo!);
    private readonly MediaSessionViewModelCache _viewModels;

    protected PlayOtherSessionMop(MediaSessionViewModelCache viewModels)
    {
        this._viewModels = viewModels ?? throw new ArgumentNullException(nameof(viewModels));
    }

    protected override async ValueTask<string> GetSuccessMessageAsync(
        IMediaService mediaService,
        MediaCommandOutcome outcome,
        CancellationToken cancellationToken)
    {
        var session = outcome.SessionId is { } sessionId
            ? mediaService.Sessions
                .FirstOrDefault(candidate => candidate.Id == sessionId)
            : null;
        var sourceName = session is null
            ? null
            : await this._viewModels
                .GetOrCreate(session)
                .GetSourceNameAsync(cancellationToken)
                .ConfigureAwait(false);
        sourceName = string.IsNullOrWhiteSpace(sourceName)
            ? "next session"
            : sourceName;
        return $"\U0001F504\uFE0F {string.Format(CultureInfo.CurrentCulture, s_switchedToFormat, sourceName, Strings.Toast_Playing)}";
    }

    protected override string GetFailureMessage(object status) => $"🚫 {Strings.Toast_NoOtherSessions}";
}

internal sealed class PlayNextSessionMop(MediaSessionViewModelCache viewModels)
    : PlayOtherSessionMop(viewModels)
{
    public override MediaOperation Operation => MediaOperation.SwitchNextSession;
}

internal sealed class PlayPreviousSessionMop(MediaSessionViewModelCache viewModels)
    : PlayOtherSessionMop(viewModels)
{
    public override MediaOperation Operation => MediaOperation.SwitchPreviousSession;
}