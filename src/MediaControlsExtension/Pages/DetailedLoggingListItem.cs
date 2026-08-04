// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed partial class DetailedLoggingListItem : ListItemBase
{
    private readonly ITag[] _enabledTags;
    private readonly ITag[] _disabledTags;

    public DetailedLoggingListItem()
        : this(new ToggleDetailedLoggingCommand())
    {
    }

    private DetailedLoggingListItem(
        ToggleDetailedLoggingCommand command)
        : base(command)
    {
        this._enabledTags =
        [
            CreateStateTag(
                Strings.ReportProblem_DetailedLogging_Enabled_Label!,
                Icons.DetailedLoggingEnabled,
                ColorHelpers.FromRgb(16, 124, 16)),
        ];
        this._disabledTags =
        [
            CreateStateTag(
                Strings.ReportProblem_DetailedLogging_Disabled_Label!,
                Icons.DetailedLoggingDisabled,
                ColorHelpers.FromRgb(96, 94, 92)),
        ];

        command.SetStateChangedHandler(this.UpdatePresentation);
        this.UpdatePresentation(DetailedLoggingMode.IsEnabled);
    }

    private void UpdatePresentation(bool enabled)
    {
        this.Title = Strings.ReportProblem_DetailedLogging_Title!;
        this.Subtitle = enabled
            ? Strings.ReportProblem_DetailedLogging_Enabled_Subtitle!
            : Strings.ReportProblem_DetailedLogging_Disabled_Subtitle!;
        this.Icon = enabled
            ? Icons.DetailedLoggingEnabled
            : Icons.DetailedLoggingDisabled;
        this.Tags = enabled ? this._enabledTags : this._disabledTags;
    }

    private static Tag CreateStateTag(string text, IconInfo icon, OptionalColor background)
    {
        return new Tag(text)
        {
            Icon = icon,
            Foreground = ColorHelpers.FromRgb(255, 255, 255),
            Background = background,
            ToolTip = text,
        };
    }
}
