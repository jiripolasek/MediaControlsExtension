// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media;
namespace JPSoftworks.MediaControlsExtension.Commands;

internal abstract class MediaSessionOp
{
    public abstract MediaOperation Operation { get; }

    public virtual bool CanExecute(MediaSession session) => true;

    public async Task<string?> InvokeAsync(
        IMediaService mediaService,
        MediaCommandTarget target,
        CancellationToken cancellationToken)
    {
        var submission = mediaService.TrySubmit(new(target, this.Operation));
        if (submission.Status != MediaCommandSubmissionStatus.Accepted ||
            submission.Completion is null)
        {
            if (submission.Status == MediaCommandSubmissionStatus.Busy)
            {
                return null;
            }

            if (RequiresRestart(mediaService))
            {
                return $"🚫 {Strings.Toast_MediaControlsUnavailable}";
            }

            return this.GetFailureMessage(submission.Status);
        }

        var outcome = await submission.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (outcome.Status == MediaCommandOutcomeStatus.Completed)
        {
            return await this.GetSuccessMessageAsync(
                mediaService,
                outcome,
                cancellationToken).ConfigureAwait(false);
        }

        return RequiresRestart(mediaService)
            ? $"🚫 {Strings.Toast_MediaControlsUnavailable}"
            : this.GetFailureMessage(outcome.Status);
    }

    protected abstract ValueTask<string> GetSuccessMessageAsync(
        IMediaService mediaService,
        MediaCommandOutcome outcome,
        CancellationToken cancellationToken);

    protected virtual string? GetFailureMessage(object status)
    {
        return status switch
        {
            MediaCommandSubmissionStatus.SessionGone or
            MediaCommandOutcomeStatus.SessionGone => $"😢 {Strings.Toast_NoCurrentSession}",
            MediaCommandSubmissionStatus.Unsupported or
            MediaCommandOutcomeStatus.Unsupported => $"🚫 {Strings.Toast_NothingHappened}",
            _ => $"😢 {Strings.Toast_NothingHappened}",
        };
    }

    private static bool RequiresRestart(IMediaService mediaService)
    {
        return mediaService.Availability == MediaControlAvailability.CircuitOpen ||
               mediaService.Status == MediaServiceStatus.Faulted;
    }
}
