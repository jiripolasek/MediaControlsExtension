# Media backends

`MediaControlsExtension.Media` owns the public session and backend contracts,
the composite, and `MediaService`. Provider projects reference this project.
`MediaControlsExtension.Media.Gsmtc` owns Windows media discovery, native object
lifetimes, and GSMTC diagnostics. New integrations should have their own projects
with their own dependencies and reference the media core.

The original direct GSMTC provider and optional [VLC provider](../user/vlc-backend.md) are registered. The isolated
worker remains a separate [proof of concept](../../experiments/MediaWorkerProbe/README.md)
with its own executable, package, and solution. It is not referenced by the
production extension or exposed in media-source settings.

The current scope follows the existing UI: session discovery, metadata, artwork,
playback controls, and source activation. More detailed control of individual
applications is a future direction to explore together with a suitable UI. It is
not a prerequisite for the composite split or initial iTunes and browser providers.
Future features should have typed requests, observable state, and explicit
capabilities; provider support can extend beyond the operations GSMTC offers.

Register providers in `MediaBackendCatalog.CreateRegistry`. The registry contains
descriptors and factories; `MediaBackendSettings` preserves each descriptor's saved
enablement. Media sources provides enable/disable actions and optional configuration
pages supplied by the extension's UI catalog. Configuration remains accessible
while disabled, and saving it does not enable the provider. General Settings owns
only shared preferences, with a text hint pointing to Media sources. All forms use
one settings store and merge only their owned keys into the existing settings file.
The registry and media core remain independent of CmdPal page types. Explicit
factories work with the extension's NativeAOT build.

```csharp
registry.Register(new MediaBackendRegistration(
    "my-player",
    "My player",
    "Discover and control My player.",
    static loggerFactory => new MyPlayerBackend(loggerFactory),
    EnabledByDefault: false));
```

The provider ID is a stable, case-sensitive settings key. Keep it unchanged across
releases. Provider names and descriptions can come from localized resources in
the application composition root. The GSMTC worker is enabled by default; internal
GSMTC and other providers default to disabled unless their registration specifies otherwise.

## Runtime lifecycle

One `CompositeMediaBackend` stays attached to one `MediaService`. The service owns
and disposes the composite. To change a provider selection, call:

```csharp
await composite.SetEnabledAsync("my-player", true, cancellationToken);
await composite.SetEnabledAsync("my-player", false, cancellationToken);
```

- Factories must return a fresh backend for every enable. Constructors should be
  lightweight; initialize external resources in `StartAsync`.
- Composite startup schedules enabled providers independently. Initial session
  discovery arrives through the normal snapshot notifications.
- Enabling awaits initialization and the initial snapshot. Disabling immediately
  withdraws that provider's sessions and cancels its lifetime token, then waits
  for startup, observation, notification, command, and artwork work to drain
  before calling `DisposeAsync` once.
- Transitions are serialized per provider. Rapid changes converge on the latest
  requested selection. A replacement cannot overlap the previous instance.
- Cancellation of `SetEnabledAsync` cancels the caller's wait after the selection
  has been accepted. Use another selection change to stop initialization.
- `Backends` exposes desired enablement, lifecycle status, and a diagnostic
  message. Provider failures are logged and isolated. Check this state after a
  transition; an initialization failure does not fault the whole composite.
- A failed provider can be retried by enabling it again. If disposal fails, its
  retired instance remains faulted and blocks replacements until extension reload.

Providers must observe cancellation and unsubscribe notifications during disposal.
If an operation ignores cancellation, teardown waits for the actual operation to
return. Other providers remain independent. Command and artwork callers have a
configurable timeout, defaulting to ten seconds; the composite retains the backend
until a timed-out underlying call really finishes.

## Observable provider state

Consumers read `IMediaService.Backends` and subscribe to `BackendsChanged`.
Each immutable entry contains the registration ID and display name, saved
`IsEnabled` choice, lifecycle `Status`, lifecycle diagnostic, `Connection`,
and `AvailableSessionCount`. Disabled registrations remain in the list.
The connection and lifecycle types live in the public media namespace.

Connection status is separate from lifecycle and control availability:

- `Connecting` means startup or an explicitly reported connection attempt.
- `Connected` means the provider reports a live connection, even with no players.
- `Disconnected` means no active connection or a retired/disabled provider.
- `Unknown` means the provider has not reported its connection state, or its
  observation failed. It is never inferred from an empty session list.

A ready backend can remain enabled and listen for reconnection while disconnected.
A connected backend can have unavailable controls. The existing snapshot
`Availability` and session `IsAvailable` remain authoritative for command
admission; providers must update them when connections or sessions become unusable.
The available-session count reflects those same published availability checks,
including source exclusions and retained unavailable sessions.

Providers publish connection changes in the same revisioned snapshot as sessions:

```csharp
return new MediaBackendSnapshot(revision, sessions, hints, availability)
{
    Connection = new(MediaConnectionStatus.Connected)
    {
        Connections =
        [
            new("work", "Work profile", MediaConnectionStatus.Connected),
            new("personal", "Personal profile", MediaConnectionStatus.Disconnected),
        ],
    },
};
```

The optional connection list uses IDs unique within that registered provider.
These are presentation identities, not command targets or transport epochs.
The provider owns aggregate connection status, labels, and diagnostics. Connection
lists must be initialized and contain valid states and distinct, nonempty IDs.
An invalid snapshot faults its owner without affecting healthy providers.

Emit `MediaBackendSignal.BackendsChanged` when connection state, profile details,
or diagnostics change, even with no sessions. The composite carries these states
through `MediaService`; consumers do not need a concrete-composite cast. State is
published before notifications. Notifications are coalesced on the existing
background pump, so handlers read the latest snapshot and dispatch to their UI
thread if needed. Equal state contents do not generate duplicate events, including
when a provider creates new arrays. Subscriber exceptions are isolated.

Startup publishes connecting before waiting for a provider. Disabling immediately
clears connection details and available-session counts while lifecycle status
remains stopping until work drains. Read/watch failures mark retained connection
details unknown; recovery replaces them and clears the error. A disposal failure
also reports unknown rather than claiming cleanup succeeded. Retired instances and
obsolete revisions cannot republish old connection details. Disposing the service
clears its published provider list.

A successful GSMTC snapshot explicitly reports connected, including when there are
no sessions or its control circuit is open. Browser transport states and profile
updates still need to be supplied by the future browser backend. Connection changes
do not alter saved enablement or release its GSMTC source exclusions.

## Media sources management page

The extension's context menu includes **Media sources** beside Settings. The page
lists every published provider, including disabled ones, with its saved enablement,
lifecycle, explicit connection state, and available-session count. A connected
provider with zero sessions is shown separately from a disconnected provider.
Selecting a row shows provider/connection diagnostics and optional per-connection
details. Recovered or disabled snapshots remove obsolete diagnostics and profiles.

Each row has an **Enable** or **Disable** action that saves the existing provider
setting and requests the composite's normal lifecycle transition. Providers with
configuration pages open them as their primary action and offer **Configure** in
the command menu. Other providers use enablement as their primary action. Opening
Media sources or a configuration page does not initialize providers. Configuration
saves remain on their page; changing options does not change saved enablement.

The page subscribes to `BackendsChanged` when CmdPal attaches its first
`IListPage.ItemsChanged` listener and detaches when the last listener is removed
or the extension is disposed. This follows the host's loaded page lifetime, not
foreground-window visibility. `GetItems` can fetch a current snapshot without
starting a persistent subscription. The first listener triggers another refresh,
covering the host's initial fetch-before-subscribe ordering.

Rows retain identity by registration ID. Status and detail changes publish row
properties; only added, removed, or reordered providers replace the item array
and raise `ItemsChanged`. Initial subscription also raises it to close the
initial-fetch race. Snapshot publication is serialized, while host callbacks run
outside the page lock. Reads started before close/disposal or superseded by a
newer read cannot overwrite current rows.

The presentation test project compiles the production page against the actual
CmdPal SDK. Tests cover prefetch/open/close/reopen, multiple listeners, state-only
updates, stable rows, profile diagnostics and recovery, equivalent snapshots,
provider actions and configuration routing, reentrant callbacks, subscriber failure,
and delayed reads. Persistence tests cover old keys, separate forms, concurrent
saves, failed-write rollback, and passwords remaining outside the settings file.
These are managed SDK object tests; they do not launch the CmdPal UI host.

```powershell
dotnet test tests/MediaControlsExtension.Presentation.Tests/JPSoftworks.MediaControlsExtension.Presentation.Tests.csproj -p:Platform=x64
```

The packaged smoke check is to open Media sources, inspect GSMTC status and count,
toggle it from its command menu, observe the result, then close/reopen the page.
Also open VLC configuration, save unchanged values, and test the saved connection.
General Settings should contain shared preferences and the Media sources hint,
with no provider inputs. Browser/profile transitions remain covered by injected
provider states until a browser implementation is registered.

## Session origin and treatment

`MediaBackendSessionSnapshot.Origin` carries a `MediaSessionOrigin` through the
service to `MediaSession.Origin`. `ConnectionId` is a stable provider-local
configuration identity, independent of names and binding generations. The default
is `local`; VLC uses its normalized, credential-free server URL. Providers with remote targets must report their
effective treatment explicitly. A connection ID change requires a new binding
generation, and commands keep their existing captured binding checks.

`TreatAsLocal` controls grouping, automatic selection, cycling, and default
automatic-pause participation. It does not establish a local native application
identity or grant source replacement claims. No remote device labels are required
in the first UI: the list has a Remote sessions group, and player labels carry a
Remote marker outside that group. Current system-volume controls remain local.

Origin changes raise `MediaSessionChanges.Origin` and a service `SessionsChanged`
notification so an existing row can move groups without replacing its session
object. This metadata is separate from source display details and is never parsed
from presentation text.

## Source presentation

Session metadata uses `MediaPropertiesSnapshot.Source` instead of a required
native application snapshot. `MediaSourceSnapshot` carries:

- Optional `DisplayName` and `IconPath` chosen by the provider.
- Optional `NativeApplication` with a Windows application ID and optional
  executable path. Omit it for a source without a native identity; do not invent
  an application ID or use an empty string.
- `Provider`, assigned from the owning registration by the composite. Leaf
  providers leave it unset. The composite replaces any supplied value with the
  registered ID and localized display name without changing the leaf snapshot.
- Ordered, immutable `Details` containing localized labels and text values.
  Browser, profile, site, and page information can be presented here without
  adding browser-specific fields to the core. Empty values are hidden in the UI.

```csharp
var source = new MediaSourceSnapshot("Music site", "site-icon.png")
{
    Details =
    [
        new("Browser", "Edge"),
        new("Profile", "Personal"),
        new("Page", "My playlist"),
    ],
};
var properties = MediaPropertiesSnapshot.Empty(source) with { Title = "Track" };
```

The view model gives nonblank provider-supplied source names and icons precedence,
independently for each field. If a field is missing and a native identity exists, it resolves
Windows application information in the background to fill that field. A source
with an explicit name and icon needs no native lookup. The name fallback is the
native application ID, then the provider display name, then the UI's existing
empty-value handling. GSMTC supplies its native identity and leaves presentation
fields unset, retaining native name/icon enrichment.

Source resolution publishes immutable presentation snapshots. A later source
change or disposal rejects obsolete lookup results, including a change away from
and back to the same application ID. A pending name request follows the current
source and returns an explicit provider name without waiting for icon enrichment.
Cancellation of one waiter does not cancel shared enrichment.

Lists, dock items, navigation commands, and playback notifications use the resolved
source name/icon. The details panel and full metadata page also show the registered
provider and optional source details. The native application ID section is shown
only when an identity is present.

Presentation does not identify command targets or establish source ownership.
The existing session IDs and binding generations still route operations.
Source-policy exclusions use only `Source.NativeApplication.ApplicationId`;
labels, provider names, and detail values cannot create exclusions. Browser
tab/frame/document/player identifiers stay inside the browser backend.

Source changes raise `MediaSessionChanges.MediaProperties` on the existing
session. Source equality compares detail contents and order, so rebuilding an
equivalent detail array does not trigger duplicate notifications or replace
published properties. Invalid source snapshots fault only the owning provider:
the source and detail array must exist, detail labels must be nonblank, values
must be nonnull, and any declared native ID must be nonblank.

The source tests cover composition ownership, metadata notifications, equivalent
details, source-policy separation, invalid snapshots, native enrichment precedence,
delayed lookups, replacement, disposal, and cancellation. Presentation tests compile
the production presentation helper into the media test assembly and inject native
lookup results. They do not exercise Windows app lookup or the CmdPal UI host.

## Provider contract

Implement the public `IMediaBackend` contract in the
`JPSoftworks.MediaControlsExtension.Media.Infrastructure` namespace:

- `StartAsync` initializes one instance. It can succeed with no running application
  and an available, empty snapshot. `DisposeAsync` releases that instance.
- `WatchAsync` yields invalidation signals throughout its lifetime. It must retain
  changes occurring between startup and enumeration. Normal completion before
  cancellation is treated as a provider failure. Recoverable disconnections must
  update snapshots while keeping this stream alive.
- `ReadSnapshotAsync` returns a complete, ordered snapshot. Revisions must not
  decrease or reset on reconnection. A failed read retains previous sessions as
  unavailable until a later signal leads to a successful read.
- Session IDs are local to an instance. Do not reuse an ID for another logical
  session. Increment `BindingGeneration` when replacing its native binding, and
  reject commands for old generations at execution, including after an internal
  queue or other asynchronous wait. Never replay queued commands on a replacement
  connection. Translate connection epochs and remote IDs inside the provider.
- Artwork keys use the local session ID and a version identifying the image.
  Do not recycle versions for replacement bindings. `GetArtworkAsync` must reject
  obsolete keys and never substitute the current image for the requested image.
- Advertise only supported capabilities. Keep unavailable sessions explicitly
  unavailable when retaining them across a transient disconnect.
- `CurrentSessionHints` contains provider-local candidates for automatic selection,
  or an empty array when there is no hint. GSMTC supplies its Windows current
  session. The composite translates and forwards every available hint to the service.
- Commands, artwork, invalidations, and observation can overlap. The provider
  owns any native scheduling or concurrency restrictions. Disposal begins after
  the composite has drained those calls.
- A backend that accepts replacements implements `IMediaSourcePolicyBackend`.
  Apply the supplied policy before discovery and validate it at actual execution,
  including after internal waits. Stamp snapshots with the `SourcePolicyRevision`
  used to produce them. Policy updates can overlap observation and commands.

The composite assigns public session IDs that are never reused during its own
lifetime. It preserves binding generations and translates session IDs, artwork
keys, and observation requests to the owning backend. Re-enabling a provider gives its
sessions fresh public identities, fencing old commands and cached artwork.

The service captures both ID and binding generation for every command target,
including each `MediaBackendSessionTarget` in `SessionsToPause`. The composite
validates these bindings before dispatch and checks the primary target again
after pausing other providers. A provider must still validate against its own
current connection at execution; its cached snapshot may lag behind a reconnect.
Artwork completions are discarded if their public key is no longer current.

## Composition policy

Sessions preserve registry order and each provider's snapshot order. The service
owns current-session selection; the composite supplies sessions and provider hints.
Declared source claims determine which application identities another provider replaces.

An accepted play command selects its target immediately, including a playback
toggle that resolves to play and next/previous session commands. The selection is
published before execution and survives metadata refreshes, provider hint changes,
playback changes, and rebinding of the same available logical session. Pausing or
controlling another session does not select it. Rejected commands leave selection
unchanged. A failed play rolls back its playback prediction but keeps the user's
selection; an older completion cannot replace a newer choice.

When the selected session disappears or becomes unavailable, selection returns to
automatic mode. Disabling its provider has the same effect. The old explicit choice
is forgotten; a reconnect can be selected by normal automatic rules but does not
restore that choice. Selection is local to this service lifetime.

Automatic selection considers only available sessions treated as local, in this order: playing provider
hints, other playing sessions, other provider hints, then remaining sessions. It
uses confirmed playback state. Equal candidates keep the current session; without
a current candidate at that priority, registry and provider snapshot order break
the tie. Missing, unavailable, or remote hints are ignored. With no eligible sessions,
there is no current session.

Next/previous session commands wrap through the published order, skipping
unavailable sessions, sessions treated as remote, and sessions without play support. They return `Unsupported`
if there is no other playable session. Accepted commands retain their captured
target even when a later command or snapshot changes selection.

The service admits automatic pauses only when `PauseOtherSessionsOnPlay` is on
and both the primary and secondary session participate. Participation defaults to
sessions treated as local; `IncludeRemoteSessionsInPauseOthers` includes remote
sessions in both roles. It does not change grouping or automatic selection.
An explicitly selected remote session remains selected while available.

The composite handles `SessionsToPause` across providers before a play command.
Within that operation, pauses run sequentially per provider so sessions sharing
a native control lane do not compete for its queue timeout. Different providers
pause in parallel. Each pause revalidates its captured binding when its turn starts.
The composite skips unavailable or replaced bindings and sessions without pause
support, treats other pause failures as best effort, and gives leaf backends
commands with an empty `SessionsToPause` list.

`MediaBackendCommandResult.PauseResults` reports the secondary pause phase using
captured session IDs and binding generations. `MediaCommandOutcome.PauseOutcomes`
exposes the corresponding public identities, statuses, and diagnostics. The primary
`Status` and `DiagnosticMessage` remain separate: failed pauses cannot turn a
successful Play into failure or roll back its predicted playback state. A failed
or rebound primary retains the results of an already completed pause phase.
Cancellation can end the command without a completed pause phase.

Duplicate targets are paused once and the primary is excluded. Missing or replaced
bindings report `SessionGone`; bindings without pause support report `Unsupported`
without dispatch. Failed or timed-out pauses report `Failed` or `Unavailable`.
A late provider completion cannot change a returned outcome or its captured identity.

Waiting commands append a localized warning to the successful primary message when
any pauses failed or were unavailable. Unsupported and vanished sessions do not
produce warnings. Immediate playback toggles keep their admission feedback and
observe completion in the background; late failures use a warning status message
through the CmdPal host. Both paths respect the Show toast messages setting.
Disposing the command provider stops pending notification observers without
canceling the underlying media commands.

Healthy providers keep the composite available when another provider fails.
With every provider disabled, the service is ready with an empty session list.
Observation reads run independently after the service admits a refresh; cached
snapshots remain readable while a provider is slow. Updates are coalesced through
the existing service refresh regulator.

Provider settings use `jpsoftworks.mediacontrols.MediaBackends.<id>.Enabled` and
apply without recreating pages, commands, dock bands, or the media service.

## Source activation

The existing "Switch to application" UI submits `MediaOperation.ActivateSource`.
A provider advertises `MediaCapabilities.ActivateSource` when it can activate the
owning source. This capability is independent of playback controls and native
application metadata; an activation-only browser player needs no `AppInfo`.

Activation uses normal command admission, captures the target ID and binding
generation, and routes through the composite to that provider. A queued current-
session request keeps its original target even if the current session changes.
Removal, replacement, disablement, and source exclusions invalidate stale targets.
The provider must recheck its connection/document binding after transport waits.
Already executing work may finish; it must never be replayed on another source.

Activation does not select a media session, predict playback, pause other sessions,
or trigger playback observation/settle refreshes. It uses the same bounded command
scheduling, timeout, result, and lifetime handling as other session commands. A
provider should signal any real state changes caused by its own activation.

GSMTC accepts an optional `IGsmtcSourceActivator`. The application registry supplies
an adapter that resolves native app information and preserves the existing PWA,
desktop-window, and packaged-app activation paths. GSMTC checks and holds the
session binding while activation executes, without occupying its native playback
control lane. Without an adapter it does not advertise source activation. Packaged
app launch is awaited so completion reflects its result.

Activation tests cover owner routing with colliding local IDs, activation-only
sources without native app identity, failure results, captured current targets,
stale queued targets, source-policy handoff, and independent playback. The user
verified window switching in their setup. Exact browser tab activation still needs
runtime validation when the browser provider is implemented.

## Source ownership

A provider registration can declare the exact sources it replaces:

```csharp
registry.Register(new MediaBackendRegistration(
    "my-companion", "My companion", "Control My browser.",
    static loggerFactory => new MyCompanionBackend(loggerFactory))
{
    ReplacesSources = [new("gsmtc", "exact-source-application-id")],
});
```

The target backend ID and application ID are case-sensitive. GSMTC application IDs
are `SourceAppUserModelId` values. Claims must name registered, distinct backends;
two enabled providers cannot claim the same source from the same backend. A
conflicting enable request is rejected before changing runtime enablement. Multiple
profiles sharing an application ID are replaced together; other identities need
their own mappings.

Exclusions derive from saved provider enablement, independently of connection
health. Enabling a companion excludes its declared GSMTC sources even while empty,
disconnected, or faulted. Disabling it withdraws its sessions and restores GSMTC
coverage. Other GSMTC sessions retain their identities and command routes. The
production registry contains GSMTC and optional VLC; browser companion setup and
identity mappings belong to their future integration projects.

`SetSourceClaimsAsync` replaces a provider's complete configured claim list while
preserving its enablement and instance. Updates use the same validation, immediate
route retirement, and serialized policy transitions as enablement changes. Disabled
providers retain the new claims for their next enable. VLC uses this when a saved
URL changes between loopback and remote; treatment overrides never grant claims.

The composite immediately withdraws newly excluded routes, retained sessions, and
current-session hints. It serializes policy updates with the affected backend's
lifecycle and applies the initial policy before startup. Other providers continue
independently. `SetEnabledAsync` waits for its lifecycle transition and affected
policy transitions, including an existing transition on a repeated request. A policy
transition completes after application and a snapshot acknowledging that revision;
provider failures are recorded in `Backends`. A failed transition keeps exclusions
in force. Caller cancellation only cancels waiting.

Reads started under an older policy cannot repopulate routes, including across a
rapid enable/disable cycle. Restored sessions receive fresh public IDs, so queued
primary and secondary commands and old artwork cannot follow the new owner. The
backend must also reject work queued inside its own native or transport boundary.
Already executing native calls may finish; policy application drains retired
bindings before acknowledging completion. A slow native call can therefore delay
the affected transition without blocking other providers.

GSMTC filters application IDs before creating bindings and immediately removes
excluded active, retained, and recently removed bindings. It bypasses retention
grace, rejects stale current-session and reconciliation results, and fences native
uses against the current binding and policy. Retired bindings cannot be reused when
coverage returns. Cleanup failures remain tracked through disposal.

## Command scheduling

`MediaService` schedules commands by the session bindings they affect. An ordinary
command claims its primary ID and generation. A play command with "pause others"
enabled also claims the available, pause-capable secondary bindings captured at
admission. Unsupported and unavailable secondary sessions do not create command
dependencies.

Commands sharing a binding run in admission order, including the whole pause/play
operation. Later commands cannot overtake an earlier waiting command on a shared
binding. Commands with disjoint binding sets run independently, including sessions
within the same provider. Replacement generations have separate scheduling keys;
the normal binding checks still reject old commands. Providers own any additional
native scheduling restrictions.

Admission permits at most two outstanding commands per primary binding and 64
across the service. `TrySubmit` returns `Busy` when either limit is reached. Existing
input throttles and the publication-before-execution barrier still apply. Failure
releases command dependencies. Shutdown cancels every accepted completion, including
work waiting for dependencies or publication, and drains active calls before backend
disposal.

The composite also keeps a command guard for each executing provider binding.
If a caller times out, another command for that binding returns `Unavailable` until
the underlying backend call finishes. Other bindings can continue. This prevents
a caller timeout from allowing overlapping controls on the same native binding.

## Lifetime validation

Tests use fake providers with delayed commands and artwork to verify that queued
play commands cannot pause a replacement binding, a target rebound during the
pause phase is rejected, and obsolete artwork completions are discarded. Metadata
updates that preserve the image and binding still allow an artwork read to finish.
Partial-outcome tests cover mixed provider results with colliding local IDs,
unsupported/missing bindings, duplicates, disablement, primary and secondary
rebinding, and outcomes retained after late native completion. Presentation tests
cover warning text, primary failures, delayed feedback, settings changes, and
notification disposal/failure. The user confirmed that the warning appears in the
packaged CmdPal host with an injected pause failure. Normal three-player switching
has been manually checked with Spotify, Windows Media Player, and Edge/YouTube
through GSMTC.
An enabled provider can remain empty, disconnect, and publish new sessions through
the same notification stream without being recreated. These are shared-boundary
tests; each native or browser provider still needs its own connection and execution
validation.

Scheduling tests cover cross-provider progress, independent sessions within one
provider, ordered shared play operations, dependency fairness, bounded admission,
replacement bindings, and cancellation of active and queued work. These tests use
fake providers; they do not establish native or browser transport behavior.

Selection tests cover an explicit switch surviving other providers' refreshes,
command routing after the switch, cycling and wraparound, automatic hints and
stable ties, disable/reconnect fallback, rejected commands, admission-time rebinding,
and delayed success or failure after a newer selection.

Source-policy tests cover saved startup enablement, exact identity matching,
conflicting owners, enable/disconnect/disable handoffs, retained sessions, independent
progress during policy application, rapid changes with an old read pending, queued
primary and secondary work, obsolete artwork, and policy failure. Fake providers
exercise handoffs; GSMTC policy setup is tested without starting the native manager.
Native session discovery and retirement still require packaged runtime validation.
