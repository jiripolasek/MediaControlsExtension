// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace JPSoftworks.MediaControlsExtension.Commands;

internal sealed partial class FallbackPlayCommandItem : FallbackCommandItem
{
    private readonly PlayPauseMediaCommand _playPauseCommand;

    public FallbackPlayCommandItem(ICommand command, string displayTitle) : base(command, displayTitle)
    {
        this._playPauseCommand = (PlayPauseMediaCommand)command;
        this._playPauseCommand.Name = "";
        this.Title = "";
    }

    public override void UpdateQuery(string query)
    {
        bool prefixSlash = query.StartsWith('/');
        if (prefixSlash)
        {
            query = query[1..];
        }

        if (query.StartsWith("pl", StringComparison.InvariantCultureIgnoreCase))
        {
            this._playPauseCommand.Name = "Play";
        }
        else if (query.StartsWith("pa", StringComparison.InvariantCultureIgnoreCase))
        {
            this._playPauseCommand.Name = "Pause";
        }
        else if (query.StartsWith("t", StringComparison.InvariantCultureIgnoreCase))
        {
            this._playPauseCommand.Name = this.Command!.Name!;
        }
        else
        {
            // hide the command if it doesn't match the query
            this._playPauseCommand.Name = "";
        }


        if (prefixSlash && !string.IsNullOrWhiteSpace(this._playPauseCommand.Name))
        {
            this._playPauseCommand.Name = "/" + this._playPauseCommand.Name;
        }

        this.Title = this._playPauseCommand.Name;
    }
}