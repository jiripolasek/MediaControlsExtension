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
    private readonly ICommand _settingsCommand;
    private readonly IListItem _settingsItem;
    private readonly Separator _separator = new();
    private readonly Dictionary<string, MediaSourceStatusItem> _rows = new(StringComparer.Ordinal);
    private TypedEventHandler<object, IItemsChangedEventArgs>? _itemsChanged;
    private IListItem[] _items;
    private bool _monitoring;
    private bool _disposed;
    private long _epoch;
    private long _nextRefresh;
    private long _lastAppliedRefresh;

    public MediaSourcesPage(IMediaService mediaService, ICommand settingsCommand, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(settingsCommand);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this._mediaService = mediaService;
        this._settingsCommand = settingsCommand;
        this._logger = loggerFactory.CreateLogger<MediaSourcesPage>();
        this.Id = "com.jpsoftworks.cmdpal.mediacontrols.sources";
        this.Name = Text("Title");
        this.Title = Text("Title");
        this.Icon = new IconInfo("\uE8F9");
        this.PlaceholderText = Text("Search");
        this.EmptyContent = new CommandItem(settingsCommand) { Title = Text("Empty"), Subtitle = Text("Manage") };
        this._settingsItem = new ListItem(settingsCommand) { Title = Text("OpenSettings"), Subtitle = Text("Manage") };
        this._items = [this._settingsItem];
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
            var states = this._mediaService.Backends;
            var presentations = states.Select(MediaSourceStatusPresentation.FromState).ToArray();
            List<(MediaSourceStatusItem Item, MediaSourceStatusChanges Changes)> changedRows = [];
            bool itemsChanged;
            lock (this._stateLock)
            {
                if (this._disposed || epoch != this._epoch || refresh < this._lastAppliedRefresh)
                {
                    return;
                }

                this._lastAppliedRefresh = refresh;
                var next = new List<IListItem>(states.Length + 2);
                var retained = new HashSet<string>(StringComparer.Ordinal);
                for (var index = 0; index < states.Length; index++)
                {
                    var id = states[index].Id;
                    retained.Add(id);
                    if (!this._rows.TryGetValue(id, out var row))
                    {
                        row = new MediaSourceStatusItem(presentations[index], this._settingsCommand, this.Icon);
                        this._rows.Add(id, row);
                    }
                    else
                    {
                        var changes = row.Apply(presentations[index]);
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

                if (next.Count > 0)
                {
                    next.Add(this._separator);
                }

                next.Add(this._settingsItem);
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

internal sealed record MediaSourceStatusPresentation(string Title, string Subtitle, ImmutableArray<(string Label, string Value)> Facts)
{
    public static MediaSourceStatusPresentation FromState(MediaBackendState state)
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

        return new(state.DisplayName, $"{enabled} | {lifecycle} | {connection} | {sessions}", facts.ToImmutable());
    }
}

[Flags]
internal enum MediaSourceStatusChanges
{
    None = 0,
    Title = 1,
    Subtitle = 2,
    Details = 4,
}

internal sealed partial class MediaSourceStatusItem : CommandItem, IListItem
{
    private sealed record State(MediaSourceStatusPresentation Presentation, IDetails Details);
    private State _state;

    public MediaSourceStatusItem(MediaSourceStatusPresentation presentation, ICommand settingsCommand, IIconInfo? icon)
        : base(new NoOpCommand())
    {
        this._state = new(presentation, CreateDetails(presentation));
        this.Icon = icon;
        this.MoreCommands = [new CommandContextItem(settingsCommand) { Title = MediaSourcesPage.Text("OpenSettings") }];
    }

    // CommandItem reads Title before the derived constructor initializes the state.
    public override string Title => Volatile.Read(ref this._state)?.Presentation.Title ?? string.Empty;
    public override string Subtitle => Volatile.Read(ref this._state).Presentation.Subtitle;
    public IDetails? Details => Volatile.Read(ref this._state).Details;
    public ITag[] Tags => [];
    public string Section => string.Empty;
    public string TextToSuggest => string.Empty;

    public MediaSourceStatusChanges Apply(MediaSourceStatusPresentation presentation)
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

        var detailsChanged = changes.HasFlag(MediaSourceStatusChanges.Title) ||
            !presentation.Facts.AsSpan().SequenceEqual(previous.Presentation.Facts.AsSpan());
        if (detailsChanged)
        {
            changes |= MediaSourceStatusChanges.Details;
        }

        if (changes != MediaSourceStatusChanges.None)
        {
            Volatile.Write(ref this._state, new(presentation, detailsChanged ? CreateDetails(presentation) : previous.Details));
        }

        return changes;
    }

    public void RaiseChanges(MediaSourceStatusChanges changes)
    {
        if (changes.HasFlag(MediaSourceStatusChanges.Title))
        {
            this.OnPropertyChanged(nameof(this.Title));
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

    private static Details CreateDetails(MediaSourceStatusPresentation presentation) => new()
    {
        Title = presentation.Title,
        Metadata = [.. presentation.Facts.Select(static fact => new DetailsElement
        {
            Key = fact.Label,
            Data = new DetailsLink { Text = fact.Value },
        })],
    };
}