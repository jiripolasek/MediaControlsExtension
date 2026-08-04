// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed partial class ReportProblemPage : ListPage
{
    private readonly IListItem[] _items;

    public ReportProblemPage(
        DiagnosticLogArchiveService archiveService,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(archiveService);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        this.Name = Strings.ReportProblem_Title!;
        this.Title = Strings.ReportProblem_Title!;
        this.Icon = Icons.ReportProblem;
        this.Id = "com.jpsoftworks.cmdpal.mediacontrols.reportproblem";
        this.ShowDetails = true;

        var instructions = new Details
        {
            Title = Strings.ReportProblem_Title!,
            Body = Strings.ReportProblem_Instructions!,
        };

        this._items =
        [
            new ListItem(new SaveDiagnosticLogsCommand(archiveService, loggerFactory))
            {
                Title = Strings.ReportProblem_SaveLogs_Title!,
                Subtitle = Strings.ReportProblem_SaveLogs_Subtitle!,
                Details = instructions,
            },
            new ListItem(new OpenGitHubIssuesCommand(loggerFactory))
            {
                Title = Strings.ReportProblem_OpenGitHub_Title!,
                Subtitle = Strings.ReportProblem_OpenGitHub_Subtitle!,
                Details = instructions,
            },
            new Separator(),
            new DetailedLoggingListItem()
            {
                Details = instructions,
            },
        ];
    }

    public override IListItem[] GetItems() => this._items;
}
