// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using System.Globalization;
using JPSoftworks.MediaControlsExtension.Media;
using JPSoftworks.MediaControlsExtension.Resources;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Microsoft.Extensions.Logging;
using Windows.Foundation;

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed partial class MediaSourcesPage : Page, IListPage, IDisposable
{
    private static readonly Action<ILogger, Exception?> ReportFailure =
        LoggerMessage.Define(LogLevel.Error, new EventId(1, "MediaSourcesPage"), "Failed to update the media sources page.");

    private readonly Lock _stateLock = new();
    private readonly IMediaService _mediaService;
    private readonly ILogger _logger;
    private readonly Action<string, bool> _setEnabled;
    private readonly Func<string, string?>? _getConfigurationDiagnostic;
    private readonly IReadOnlyDictionary<string, ICommand> _configurationPages;
    private readonly IReadOnlyDictionary<string, string> _sourceDescriptions;
    private readonly Dictionary<string, MediaSourceStatusItem> _rows = new(StringComparer.Ordinal);
    private TypedEventHandler<object, IItemsChangedEventArgs>? _itemsChanged;
    private IListItem[] _items = [];
    private bool _monitoring;
    private bool _disposed;
    private long _epoch;
    private long _nextRefresh;
    private long _lastAppliedRefresh;

    public MediaSourcesPage(IMediaService mediaService, Action<string, bool> setEnabled,
        IReadOnlyDictionary<string, ICommand> configurationPages, ILoggerFactory loggerFactory,
        Func<string, string?>? getConfigurationDiagnostic = null, IReadOnlyDictionary<string, string>? sourceDescriptions = null)
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(setEnabled);
        ArgumentNullException.ThrowIfNull(configurationPages);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this._mediaService = mediaService;
        this._getConfigurationDiagnostic = getConfigurationDiagnostic;
        this._setEnabled = setEnabled;
        this._configurationPages = configurationPages;
        this._sourceDescriptions = sourceDescriptions ?? new Dictionary<string, string>();
        this._logger = loggerFactory.CreateLogger<MediaSourcesPage>();
        this.Id = "com.jpsoftworks.cmdpal.mediacontrols.sources";
        this.Name = Text("Title");
        this.Title = Text("Title");
        this.Icon = new IconInfo("\uE8F9");
        this.PlaceholderText = Text("Search");
        this.EmptyContent = new CommandItem(new NoOpCommand()) { Title = Text("Empty"), Subtitle = Text("ManageProviders") };
    }

    public event TypedEventHandler<object, IItemsChangedEventArgs> ItemsChanged
    {
        add
        {
            if (value is null)
            {
                return;
            }

            var start = false;
            lock (this._stateLock)
            {
                if (this._disposed)
                {
                    return;
                }

                this._itemsChanged += value;
                if (!this._monitoring)
                {
                    this._monitoring = true;
                    this._epoch++;
                    this._mediaService.BackendsChanged += this.BackendsOnChanged;
                    start = true;
                }
            }

            if (start)
            {
                this.Refresh(allowInactive: false, forceItemsChanged: true);
            }
        }
        remove
        {
            lock (this._stateLock)
            {
                this._itemsChanged -= value;
                if (this._monitoring && this._itemsChanged is null)
                {
                    this.StopMonitoringUnderLock();
                }
            }
        }
    }

    public string PlaceholderText { get; set; }
    public string SearchText { get; set => this.SetProperty(ref field, value); } = string.Empty;
    public bool ShowDetails => true;
    public bool HasMoreItems => false;
    public IFilters? Filters => null;
    public IGridProperties? GridProperties => null;
    public ICommandItem? EmptyContent { get; }

    public IListItem[] GetItems()
    {
        this.Refresh(allowInactive: true, forceItemsChanged: false);
        lock (this._stateLock)
        {
            return this._items;
        }
    }

    public void LoadMore()
    {
    }

    public void Dispose()
    {
        lock (this._stateLock)
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            this.StopMonitoringUnderLock();
            this._itemsChanged = null;
            this._rows.Clear();
            this._items = [];
        }
    }

    private void StopMonitoringUnderLock()
    {
        this._epoch++;
        if (this._monitoring)
        {
            this._monitoring = false;
            this._mediaService.BackendsChanged -= this.BackendsOnChanged;
        }
    }

    private void BackendsOnChanged(object? sender, EventArgs args) => this.Refresh(allowInactive: false, forceItemsChanged: false);

    public void RefreshConfiguration() => this.Refresh(allowInactive: false, forceItemsChanged: false);

    private void RefreshInactiveState()
    {
        lock (this._stateLock)
        {
            if (this._monitoring || this._disposed) { return; }
        }

        this.Refresh(allowInactive: true, forceItemsChanged: false);
    }

    private void Refresh(bool allowInactive, bool forceItemsChanged)
    {
        long epoch;
        long refresh;
        lock (this._stateLock)
        {
            if (this._disposed || (!allowInactive && !this._monitoring))
            {
                return;
            }

            epoch = this._epoch;
            refresh = ++this._nextRefresh;
        }

        try
        {
            var states = this._mediaService.Backends.Select(state => this._getConfigurationDiagnostic?.Invoke(state.Id) is { } diagnostic
                ? state with { DiagnosticMessage = diagnostic, Status = MediaBackendLifecycleStatus.Faulted }
                : state).OrderBy(static state => MediaSourceStatusPresentation.SectionOrder(state.Id)).ToImmutableArray();
            var presentations = states.Select(state => MediaSourceStatusPresentation.FromState(state,
                this._sourceDescriptions.GetValueOrDefault(state.Id, Text("ManageProviders")))).ToArray();
            List<(MediaSourceStatusItem Item, MediaSourceStatusChanges Changes)> changedRows = [];
            bool itemsChanged;
            lock (this._stateLock)
            {
                if (this._disposed || epoch != this._epoch || refresh < this._lastAppliedRefresh)
                {
                    return;
                }

                this._lastAppliedRefresh = refresh;
                var next = new List<IListItem>(states.Length);
                var retained = new HashSet<string>(StringComparer.Ordinal);
                for (var index = 0; index < states.Length; index++)
                {
                    var id = states[index].Id;
                    retained.Add(id);
                    if (!this._rows.TryGetValue(id, out var row))
                    {
                        this._configurationPages.TryGetValue(id, out var configurationPage);
                        row = new MediaSourceStatusItem(states[index], presentations[index], this._setEnabled,
                            this.RefreshInactiveState, configurationPage, this.Icon, this._logger);
                        this._rows.Add(id, row);
                    }
                    else
                    {
                        var changes = row.Apply(presentations[index], states[index].IsEnabled);
                        if (changes != MediaSourceStatusChanges.None)
                        {
                            changedRows.Add((row, changes));
                        }
                    }

                    next.Add(row);
                }

                foreach (var id in this._rows.Keys.Where(id => !retained.Contains(id)).ToArray())
                {
                    this._rows.Remove(id);
                }

                itemsChanged = !this._items.SequenceEqual(next);
                if (itemsChanged)
                {
                    this._items = [.. next];
                }
            }

            foreach (var (item, changes) in changedRows)
            {
                if (!this.IsCurrent(epoch))
                {
                    return;
                }

                item.RaiseChanges(changes);
            }

            if (itemsChanged || forceItemsChanged)
            {
                this.RaiseItemsChanged(epoch);
            }
        }
        catch (Exception ex)
        {
            ReportFailure(this._logger, ex);
        }
    }

    private bool IsCurrent(long epoch)
    {
        lock (this._stateLock)
        {
            return !this._disposed && epoch == this._epoch;
        }
    }

    private void RaiseItemsChanged(long epoch)
    {
        TypedEventHandler<object, IItemsChangedEventArgs>? handlers;
        int count;
        lock (this._stateLock)
        {
            if (!this.IsCurrent(epoch))
            {
                return;
            }

            handlers = this._itemsChanged;
            count = this._items.Length;
        }

        if (handlers is null)
        {
            return;
        }

        var args = new ItemsChangedEventArgs(count);
        foreach (TypedEventHandler<object, IItemsChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                ReportFailure(this._logger, ex);
            }
        }
    }

    internal static string Text(string key) => Strings.ResourceManager.GetString($"MediaSources_{key}", Strings.Culture) ?? key;
}

internal enum MediaSourceStatusTone
{
    Disabled,
    Enabled,
    Attention,
}

internal sealed record MediaSourceStatusPresentation(string Title, string Subtitle, string Status, MediaSourceStatusTone Tone,
    string Section, string Guidance, bool CanRetry, ImmutableArray<(string Label, string Value)> Facts)
{
    public static int SectionOrder(string id) => id switch
    {
        "gsmtc.worker" => 0,
        "gsmtc" or "dummy.worker" => 2,
        _ => 1,
    };

    public static MediaSourceStatusPresentation FromState(MediaBackendState state, string description)
    {
        var enabled = MediaSourcesPage.Text(state.IsEnabled ? "Enabled" : "Disabled");
        var lifecycle = MediaSourcesPage.Text($"Lifecycle_{state.Status}");
        var connection = MediaSourcesPage.Text($"Connection_{state.Connection.Status}");
        var count = state.AvailableSessionCount.ToString("N0", CultureInfo.CurrentCulture);
        var sessions = string.Format(CultureInfo.CurrentCulture,
            MediaSourcesPage.Text(state.AvailableSessionCount == 1 ? "OneSession" : "SessionCount"), count);
        var facts = ImmutableArray.CreateBuilder<(string, string)>();
        facts.Add((MediaSourcesPage.Text("Enablement"), enabled));
        facts.Add((MediaSourcesPage.Text("Lifecycle"), lifecycle));
        facts.Add((MediaSourcesPage.Text("Connection"), connection));
        facts.Add((MediaSourcesPage.Text("AvailableSessions"), count));
        if (!string.IsNullOrWhiteSpace(state.DiagnosticMessage))
        {
            facts.Add((MediaSourcesPage.Text("ProviderMessage"), state.DiagnosticMessage));
        }

        if (!string.IsNullOrWhiteSpace(state.Connection.DiagnosticMessage) &&
            state.Connection.DiagnosticMessage != state.DiagnosticMessage)
        {
            facts.Add((MediaSourcesPage.Text("ConnectionMessage"), state.Connection.DiagnosticMessage));
        }

        foreach (var profile in state.Connection.Connections)
        {
            var value = MediaSourcesPage.Text($"Connection_{profile.Status}");
            if (!string.IsNullOrWhiteSpace(profile.DiagnosticMessage))
            {
                value = $"{value}\n{profile.DiagnosticMessage}";
            }

            facts.Add((profile.DisplayName, value));
        }

        var status = state switch
        {
            { Status: MediaBackendLifecycleStatus.Faulted } => MediaSourcesPage.Text("NeedsAttention"),
            { Status: MediaBackendLifecycleStatus.Stopping } => MediaSourcesPage.Text("Lifecycle_Stopping"),
            { IsEnabled: false } => MediaSourcesPage.Text("Off"),
            { Status: MediaBackendLifecycleStatus.Starting } => MediaSourcesPage.Text("Lifecycle_Starting"),
            { Connection.Status: MediaConnectionStatus.Connecting } => MediaSourcesPage.Text("Connection_Connecting"),
            { Id: "itunes", Connection.Status: MediaConnectionStatus.Disconnected } => MediaSourcesPage.Text("WaitingForITunes"),
            { Connection.Status: MediaConnectionStatus.Disconnected } => MediaSourcesPage.Text("NotConnected"),
            { AvailableSessionCount: > 0 } => sessions,
            _ => MediaSourcesPage.Text("WaitingForPlayback"),
        };
        var section = MediaSourcesPage.Text(SectionOrder(state.Id) switch
        {
            0 => "Recommended",
            2 => "Troubleshooting",
            _ => "OptionalPlayers",
        });
        var guidance = MediaSourcesPage.Text(state.Id switch
        {
            "gsmtc.worker" => "WindowsGuidance",
            "gsmtc" => "CompatibilityGuidance",
            "itunes" => "ITunesGuidance",
            "vlc" => "VlcGuidance",
            "dummy.worker" => "DummyGuidance",
            _ => "ManageProviders",
        });
        var canRetry = state.IsEnabled && state.Status == MediaBackendLifecycleStatus.Faulted;
        if (state.Status == MediaBackendLifecycleStatus.Faulted)
        {
            guidance = $"{guidance}\n\n{MediaSourcesPage.Text(canRetry ? "RetryGuidance" : "EnableGuidance")}";
        }

        var tone = state switch
        {
            { Status: MediaBackendLifecycleStatus.Faulted } => MediaSourceStatusTone.Attention,
            { IsEnabled: false } => MediaSourceStatusTone.Disabled,
            { Status: MediaBackendLifecycleStatus.Ready, Id: not "itunes", Connection.Status: MediaConnectionStatus.Disconnected }
                => MediaSourceStatusTone.Attention,
            _ => MediaSourceStatusTone.Enabled,
        };

        return new(state.DisplayName, description, status, tone, section, guidance, canRetry, facts.ToImmutable());
    }
}

[Flags]
internal enum MediaSourceStatusChanges
{
    None = 0,
    Title = 1,
    Subtitle = 2,
    Details = 4,
    Enablement = 8,
    Tags = 16,
    Actions = 32,
}

internal sealed partial class MediaSourceStatusItem : CommandItem, IListItem
{
    private sealed record State(MediaSourceStatusPresentation Presentation, IDetails Details, ITag[] Tags, bool Enabled);
    private State _state;
    private readonly MediaSourceToggleCommand _toggle;
    private readonly MediaSourceRetryCommand _retry;
    private readonly ICommand? _configurationPage;

    public MediaSourceStatusItem(MediaBackendState backend, MediaSourceStatusPresentation presentation,
        Action<string, bool> setEnabled, Action refreshInactiveState, ICommand? configurationPage, IIconInfo? icon, ILogger logger)
    {
        this._toggle = new MediaSourceToggleCommand(backend.Id, backend.IsEnabled, setEnabled, refreshInactiveState, logger);
        this._retry = new MediaSourceRetryCommand(backend.Id, setEnabled, refreshInactiveState, this.CanRetry, logger);
        this._configurationPage = configurationPage;
        this._state = new(presentation, this.CreateDetails(presentation),
            [CreateStatusTag(presentation, backend.IsEnabled)], backend.IsEnabled);
        this.Command = configurationPage ?? this._toggle;
        this.Icon = backend.Id switch
        {
            "gsmtc" or "gsmtc.worker" => Icons.WindowsMediaSource,
            "itunes" => Icons.ITunesSource,
            "vlc" => Icons.VlcSource,
            "dummy.worker" => Icons.ReportProblem,
            _ => icon,
        };
        this.UpdateActions();
    }

    // CommandItem reads Title before the derived constructor initializes the state.
    public override string Title => Volatile.Read(ref this._state)?.Presentation.Title ?? string.Empty;
    public override string Subtitle => Volatile.Read(ref this._state).Presentation.Subtitle;
    public IDetails? Details => Volatile.Read(ref this._state).Details;
    public ITag[] Tags => Volatile.Read(ref this._state).Tags;
    public string Section => Volatile.Read(ref this._state).Presentation.Section;
    public string TextToSuggest => string.Empty;

    public MediaSourceStatusChanges Apply(MediaSourceStatusPresentation presentation, bool enabled)
    {
        var previous = Volatile.Read(ref this._state);
        var changes = MediaSourceStatusChanges.None;
        if (presentation.Title != previous.Presentation.Title)
        {
            changes |= MediaSourceStatusChanges.Title;
        }

        if (presentation.Subtitle != previous.Presentation.Subtitle)
        {
            changes |= MediaSourceStatusChanges.Subtitle;
        }

        if (presentation.Status != previous.Presentation.Status || presentation.Tone != previous.Presentation.Tone ||
            previous.Enabled != enabled)
        {
            changes |= MediaSourceStatusChanges.Tags;
        }

        if (presentation.CanRetry != previous.Presentation.CanRetry)
        {
            changes |= MediaSourceStatusChanges.Actions;
        }

        var detailsChanged = changes.HasFlag(MediaSourceStatusChanges.Title) ||
            changes.HasFlag(MediaSourceStatusChanges.Subtitle) || changes.HasFlag(MediaSourceStatusChanges.Actions) ||
            presentation.Guidance != previous.Presentation.Guidance ||
            !presentation.Facts.AsSpan().SequenceEqual(previous.Presentation.Facts.AsSpan());
        if (detailsChanged)
        {
            changes |= MediaSourceStatusChanges.Details;
        }

        if (previous.Enabled != enabled)
        {
            changes |= MediaSourceStatusChanges.Enablement;
        }

        if (changes != MediaSourceStatusChanges.None)
        {
            Volatile.Write(ref this._state, new(presentation,
                detailsChanged ? this.CreateDetails(presentation) : previous.Details,
                changes.HasFlag(MediaSourceStatusChanges.Tags) ? [CreateStatusTag(presentation, enabled)] : previous.Tags, enabled));
        }

        return changes;
    }

    public void RaiseChanges(MediaSourceStatusChanges changes)
    {
        if (changes.HasFlag(MediaSourceStatusChanges.Enablement))
        {
            this._toggle.UpdatePresentation(Volatile.Read(ref this._state).Enabled);
        }

        if (changes.HasFlag(MediaSourceStatusChanges.Title))
        {
            this.OnPropertyChanged(nameof(this.Title));
        }

        if (changes.HasFlag(MediaSourceStatusChanges.Actions))
        {
            this.UpdateActions();
        }

        if (changes.HasFlag(MediaSourceStatusChanges.Tags))
        {
            this.OnPropertyChanged(nameof(this.Tags));
        }

        if (changes.HasFlag(MediaSourceStatusChanges.Subtitle))
        {
            this.OnPropertyChanged(nameof(this.Subtitle));
        }

        if (changes.HasFlag(MediaSourceStatusChanges.Details))
        {
            this.OnPropertyChanged(nameof(this.Details));
        }
    }

    private bool CanRetry() => Volatile.Read(ref this._state).Presentation.CanRetry;

    private static Tag CreateStatusTag(MediaSourceStatusPresentation presentation, bool enabled) => new(presentation.Status)
    {
        Foreground = ColorHelpers.FromRgb(255, 255, 255),
        Background = presentation.Tone switch
        {
            MediaSourceStatusTone.Enabled => ColorHelpers.FromRgb(16, 124, 16),
            MediaSourceStatusTone.Attention => ColorHelpers.FromRgb(138, 87, 0),
            _ => ColorHelpers.FromRgb(96, 94, 92),
        },
        ToolTip = $"{MediaSourcesPage.Text(enabled ? "Enabled" : "Disabled")}: {presentation.Status}",
    };

    // Command Palette inserts the primary command before MoreCommands.
    private void UpdateActions() => this.MoreCommands =
        [.. this.GetCommands(Volatile.Read(ref this._state).Presentation.CanRetry).Skip(1)
            .Select(static command => new CommandContextItem(command))];

    private ICommand[] GetCommands(bool canRetry)
    {
        var commands = new List<ICommand> { this._configurationPage ?? this._toggle };
        if (this._configurationPage is not null)
        {
            commands.Add(this._toggle);
        }

        if (canRetry)
        {
            commands.Add(this._retry);
        }

        return [.. commands];
    }

    private Details CreateDetails(MediaSourceStatusPresentation presentation) => new()
    {
        Title = presentation.Title,
        Body = $"{presentation.Subtitle}\n\n{presentation.Guidance}",
        Metadata =
        [
            new DetailsElement
            {
                Key = MediaSourcesPage.Text("Actions"),
                Data = new DetailsCommands { Commands = this.GetCommands(presentation.CanRetry) },
            },
            .. presentation.Facts.Select(static fact => new DetailsElement
            {
                Key = fact.Label,
                Data = new DetailsLink { Text = fact.Value },
            }),
        ],
    };
}

internal sealed partial class MediaSourceRetryCommand : InvokableCommand
{
    private static readonly Action<ILogger, Exception?> RetryFailed = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1, nameof(RetryFailed)), "Could not retry the media source.");
    private readonly string _id;
    private readonly Action<string, bool> _setEnabled;
    private readonly Action _refreshInactiveState;
    private readonly Func<bool> _canRetry;
    private readonly ILogger _logger;

    public MediaSourceRetryCommand(string id, Action<string, bool> setEnabled, Action refreshInactiveState,
        Func<bool> canRetry, ILogger logger)
    {
        this._id = id;
        this._setEnabled = setEnabled;
        this._refreshInactiveState = refreshInactiveState;
        this._canRetry = canRetry;
        this._logger = logger;
        this.Id = $"com.jpsoftworks.cmdpal.mediacontrols.sources.{id}.retry";
        this.Name = MediaSourcesPage.Text("Retry");
        this.Icon = new IconInfo("\uE72C");
    }

    public override ICommandResult Invoke()
    {
        try
        {
            this._refreshInactiveState();
            if (this._canRetry())
            {
                this._setEnabled(this._id, true);
                this._refreshInactiveState();
            }

            return CommandResult.KeepOpen();
        }
        catch (Exception ex)
        {
            RetryFailed(this._logger, ex);
            return CommandResult.ShowToast(new ToastArgs
            {
                Message = MediaSourcesPage.Text("RetryFailed"),
                Result = CommandResult.KeepOpen(),
            });
        }
    }
}

internal sealed partial class MediaSourceToggleCommand : InvokableCommand
{
    private static readonly Action<ILogger, Exception?> SaveFailed = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1, nameof(SaveFailed)), "Could not change media source enablement.");
    private readonly string _id;
    private readonly Action<string, bool> _setEnabled;
    private readonly Action _refreshInactiveState;
    private readonly ILogger _logger;
    private bool _enabled;

    public MediaSourceToggleCommand(string id, bool enabled, Action<string, bool> setEnabled, Action refreshInactiveState, ILogger logger)
    {
        this._id = id;
        this._setEnabled = setEnabled;
        this._refreshInactiveState = refreshInactiveState;
        this._logger = logger;
        this.Id = $"com.jpsoftworks.cmdpal.mediacontrols.sources.{id}.enablement";
        this.UpdatePresentation(enabled);
    }

    public void UpdatePresentation(bool enabled)
    {
        Volatile.Write(ref this._enabled, enabled);
        this.Name = MediaSourcesPage.Text(enabled ? "Disable" : "Enable");
        this.Icon = enabled ? Icons.DisableMediaSource : Icons.EnableMediaSource;
    }

    public override ICommandResult Invoke() => this.ChangeEnablement(confirmedDisable: false);

    private CommandResult ChangeEnablement(bool confirmedDisable)
    {
        try
        {
            this._refreshInactiveState();
            var wasEnabled = Volatile.Read(ref this._enabled);
            if (confirmedDisable && !wasEnabled)
            {
                return CommandResult.KeepOpen();
            }

            var enabled = !wasEnabled;
            if (!enabled && !confirmedDisable && this._id is "gsmtc" or "gsmtc.worker")
            {
                return CommandResult.Confirm(new ConfirmationArgs
                {
                    Title = MediaSourcesPage.Text("DisableWindowsTitle"),
                    Description = MediaSourcesPage.Text("DisableWindowsDescription"),
                    PrimaryCommand = new ConfirmedDisableCommand(this),
                    IsPrimaryCommandCritical = true,
                });
            }

            this._setEnabled(this._id, enabled);
            this._refreshInactiveState();
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

    private sealed partial class ConfirmedDisableCommand : InvokableCommand
    {
        private readonly MediaSourceToggleCommand _toggle;

        public ConfirmedDisableCommand(MediaSourceToggleCommand toggle)
        {
            this._toggle = toggle;
            this.Id = $"{toggle.Id}.confirm-disable";
            this.Name = MediaSourcesPage.Text("DisableWindowsAction");
            this.Icon = Icons.DisableMediaSource;
        }

        public override ICommandResult Invoke() => this._toggle.ChangeEnablement(confirmedDisable: true);
    }
}