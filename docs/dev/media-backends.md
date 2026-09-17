# Media backends

A backend discovers players, publishes their state, and executes media commands.
The extension combines those backends into one session list. The UI talks to
`MediaService`; it does not need to know how a player is connected.

Start here when adding a provider or changing shared session behavior. For other
work, use the focused guides:

- [Playback and command scheduling](media-playback.md): admission, predictions,
  pause-others, GSMTC confirmation, and failure feedback.
- [Worker hosting](media-backend-hosting.md): running a backend in another process,
  switching hosting modes, transport, and recovery.
- [VLC setup](../user/vlc-backend.md) and [Dummy media](dummy-media.md): provider setup.

## Where the code lives

| Component | Responsibility |
| --- | --- |
| [Media core](../../src/MediaControlsExtension.Media) | Public contracts, `MediaService`, session selection, predictions, and command scheduling. |
| [CompositeMediaBackend](../../src/MediaControlsExtension.Media/Infrastructure/CompositeMediaBackend.cs) | Provider lifetimes, public session IDs, routing, source exclusions, and cross-provider pauses. |
| [GSMTC](../../src/MediaControlsExtension.Media.Gsmtc) | Windows media discovery, native objects, controls, and artwork. |
| [Hosting](../../src/MediaControlsExtension.Media.Hosting) | Worker proxy, transport, process ownership, and recovery. |
| [MediaBackendCatalog](../../src/MediaControlsExtension/Helpers/MediaBackendCatalog.cs) | Application registrations, localized provider labels, configuration pages, and activation adapters. |

Windows media uses the isolated GSMTC worker by default. In-process GSMTC is an
alternative, and the two modes are mutually exclusive. VLC is optional. New
integrations belong in separate projects that reference the media core and own
their dependencies.

## Add a provider

1. Implement [IMediaBackend](../../src/MediaControlsExtension.Media/Infrastructure/IMediaBackend.cs)
   in the new provider project. Keep construction cheap; open connections and
   native resources in `StartAsync`.
2. Register a factory in `MediaBackendCatalog.CreateRegistry`. Each invocation
   must return a fresh instance. Explicit factories support NativeAOT.
3. Give it a stable, case-sensitive ID. Saved enablement uses
   `jpsoftworks.mediacontrols.MediaBackends.<id>.Enabled`; changing the ID loses
   that association.
4. Add optional configuration pages in the application's UI catalog. Keep CmdPal
   page types out of the registry and media core.
5. If it replaces another provider's sessions, declare [source claims](#source-ownership).
   If it needs a worker, follow [Adding a worker backend](media-backend-hosting.md#adding-a-worker-backend).

```csharp
registry.Register(new MediaBackendRegistration(
    "my-player",
    "My player",
    "Discover and control My player.",
    static loggerFactory => new MyPlayerBackend(loggerFactory),
    EnabledByDefault: false));
```

The shared API covers discovery, metadata, artwork, playback, and source activation.
Application-specific features need typed requests, observable state, capabilities,
and a UI that can use them; they do not need to copy GSMTC's operation set.

## Provider contract

The XML documentation on `IMediaBackend` is the method-level reference. These are
the rules that matter across providers:

| Surface | Required behavior |
| --- | --- |
| `StartAsync` | Initialize one instance. An available empty snapshot is valid when no player is running. |
| `WatchAsync` | Yield invalidations until cancellation. Retain changes between startup and enumeration. Recoverable disconnects keep this stream alive; unexpected completion faults the provider. |
| `ReadSnapshotAsync` | Return a complete, ordered, immutable snapshot. Revisions never decrease or reset on reconnect. A failed read leaves previous sessions unavailable until recovery. |
| Commands | Advertise supported capabilities and check the captured binding again at execution, including after an internal queue or transport wait. |
| Artwork | Return the exact version requested, or no artwork. Never substitute a newer image for an obsolete key. |
| Concurrency | Commands, artwork, observations, and policy updates can overlap. The provider owns native serialization and threading. |
| `DisposeAsync` | Release resources and unsubscribe events. Honor cancellation so the composite can drain work before disposal. |

### Identity: a session is not its current connection

A session ID identifies a logical session within one backend instance. A binding
generation identifies the native or remote connection currently behind it.
For example, reconnecting the same player can retain its session ID but must
advance the generation, so an old queued Pause cannot reach the new connection.

- Never reuse a session ID for a different logical session.
- Advance `BindingGeneration` when the binding or origin connection changes.
- Give replacement artwork a new version; reject old keys at retrieval time.
- Never replay queued commands after reconnecting to a replacement binding.

The composite assigns its own public IDs and translates commands, hints,
invalidations, and artwork keys back to the provider. Re-enabling a provider or
restoring an excluded source gives it fresh public IDs. Artwork arriving after its
public key becomes obsolete is discarded.

### Snapshot validity and notifications

Initialize immutable arrays, even when empty. Connection detail IDs must be
nonempty and distinct within the provider. Source details need nonblank labels
and nonnull values; a declared native application ID must be nonblank. Invalid
snapshots fault their provider without taking healthy providers down.

Publish state before signaling it. Emit `BackendsChanged` for connection or
diagnostic changes even with no sessions. Notifications are coalesced background
notifications: consumers read the latest state and dispatch to their UI thread.
Equal contents do not generate duplicate events, and subscriber failures are isolated.

## Enable, disable, and observe a provider

One `MediaService` owns one composite for its lifetime. Changing provider selection
does not recreate pages, commands, or the service:

```csharp
await composite.SetEnabledAsync("my-player", true, cancellationToken);
await composite.SetEnabledAsync("my-player", false, cancellationToken);
```

Enabling waits for initialization and the initial snapshot. Disabling first
withdraws sessions and cancels the provider's lifetime token, then drains startup,
reads, notifications, commands, and artwork before disposing the instance once.
Rapid changes converge on the latest requested selection. A replacement instance
cannot start while its predecessor can still operate.

After a selection is accepted, canceling `SetEnabledAsync` only cancels that
caller's wait. Submit another selection to stop initialization. Providers progress
independently, so one slow provider does not block another. With every provider
disabled, the service is ready with an empty session list.

Command and artwork callers have a configurable timeout, ten seconds by default.
A timeout does not dispose a backend whose underlying call is still running.
Teardown waits for that call. A disposal failure blocks replacement until reload;
a startup failure can be retried by explicitly enabling the provider again.

### Enabled, connected, and available mean different things

Read `IMediaService.Backends` and subscribe to `BackendsChanged`. Disabled providers
remain listed. Each entry reports saved enablement, lifecycle status, diagnostics,
connection state, and the number of available sessions.

| State | Meaning |
| --- | --- |
| `IsEnabled` | The user's saved choice, independent of connection health. |
| Lifecycle `Status` | Whether the instance is starting, ready, stopping, or faulted. |
| Connection `Connecting` | Startup or an explicit connection attempt. |
| Connection `Connected` | A live connection, including one with zero players. |
| Connection `Disconnected` | No connection, or a disabled/retired provider. |
| Connection `Unknown` | No report, a failed observation, or unconfirmed cleanup. |
| Snapshot `Availability` and session `IsAvailable` | Whether commands may be admitted. These are authoritative. |

Providers report connection state in the same revisioned snapshot as sessions.
Optional per-connection details can describe browser profiles or server accounts;
those IDs are presentation identities, not command targets or worker epochs.
The provider supplies the aggregate connection status. An empty session list does
not imply disconnection.

Startup publishes Connecting. Disablement clears connection details and counts
immediately, while lifecycle status can remain Stopping during cleanup. Read/watch
failures mark retained connection details Unknown; recovery replaces them and
clears the diagnostic. Obsolete instances and revisions cannot restore old state.
Service disposal clears the published provider list.

## Select and group sessions

An accepted Play selects its target immediately. This includes a toggle resolved
to Play and next/previous-session commands. That choice survives refreshes, playback
changes, rebinding of the same available session, and even a failed Play. Pause and
other controls do not change selection. Rejected commands leave it unchanged.

If the chosen session disappears or becomes unavailable, the explicit choice is
forgotten. Automatic selection considers available sessions treated as local:

1. Playing sessions named by provider hints.
2. Other playing sessions.
3. Other provider hints.
4. Remaining sessions.

Selection uses confirmed playback state. Equal candidates retain the current
session; otherwise registry order, then provider snapshot order, breaks the tie.
No eligible session means no current session. Next/previous wraps through that
order, skips unavailable, remote, or non-playable sessions, and returns Unsupported
if there is no other candidate. Once accepted, a command keeps its captured target.

### Local and remote treatment

`MediaSessionOrigin.ConnectionId` is a stable provider-local configuration identity.
The default is `local`; VLC uses its normalized URL without credentials.
`TreatAsLocal` controls grouping, automatic selection, cycling, and default
pause-others participation. It grants neither native application identity nor
source ownership.

An explicitly chosen remote session stays selected while available.
`IncludeRemoteSessionsInPauseOthers` includes remote sessions as both primary and
secondary targets; it does not change grouping or automatic selection. System
volume controls remain local. Origin changes notify both the session and service,
so rows can move groups without replacing the session object.

## Describe and activate the source

`MediaPropertiesSnapshot.Source` describes where media comes from:

| Field | Usage |
| --- | --- |
| `DisplayName`, `IconPath` | Optional provider-supplied presentation. |
| `NativeApplication` | Optional Windows application ID and executable path. Omit it when there is no native identity. |
| `Provider` | Assigned by the composite from the registration; leaf providers leave it unset. |
| `Details` | Ordered label/value pairs, such as browser, profile, site, or page. Empty values are hidden. |

Explicit names and icons win independently. Missing fields can be filled by a
background Windows application lookup. Name fallback is native application ID,
then provider name, then the UI's empty-value handling. GSMTC relies on this native
enrichment. Source changes or disposal invalidate old lookup results; canceling
one waiter does not cancel shared enrichment. Equivalent detail arrays do not
cause new notifications.

These fields are for presentation. Commands use session IDs and generations;
source exclusions use only `NativeApplication.ApplicationId`. Keep provider-specific
routing IDs, such as browser tab or document IDs, inside the provider.

"Switch to application" submits `ActivateSource`. Advertise it only when activation
is implemented; it is independent of playback and can work without native app
metadata. Activation uses the captured binding and normal command lifetime, but
neither selects a session nor predicts playback, pauses others, or requests playback
settling. Revalidate after asynchronous waits and never replay activation on a
replacement source.

GSMTC uses the application's `IGsmtcSourceActivator` adapter and holds the binding
without occupying the playback control gate. Hosted GSMTC activates through the
extension callback so the source application does not inherit a worker lifetime.

## Source ownership

Use `ReplacesSources` when a provider supplies a better version of another
provider's sessions. For example, local VLC can replace its GSMTC duplicate while
leaving every other Windows player alone. This differs from an exclusive group,
which switches an entire provider, such as internal versus worker GSMTC.

```csharp
ReplacesSources =
[
    new("gsmtc", "exact-source-application-id"),
    new("gsmtc.worker", "exact-source-application-id"),
];
```

Claims use exact, case-sensitive backend and application IDs. GSMTC uses
`SourceAppUserModelId`. Name registered, distinct target backends, and cover both
GSMTC modes when replacing Windows discovery. Two enabled providers cannot claim
the same source from the same backend; conflicting changes are rejected.

Claims follow saved enablement. An enabled replacement keeps its claims while
empty, disconnected, or faulted. Disabling it restores the displaced provider's
coverage. `SetSourceClaimsAsync` replaces the complete claim list without restarting
the claiming provider; disabled providers retain the list for their next enable.
Local/remote treatment overrides do not grant claims.

The handoff must preserve these guarantees:

1. Newly excluded routes, retained sessions, and hints disappear immediately.
2. `IMediaSourcePolicyBackend` receives the initial policy before startup and checks
   it again at execution, including after internal waits.
3. A transition finishes only after policy application and a snapshot acknowledging
   its `SourcePolicyRevision`. Old reads cannot restore excluded routes.
4. Restored sources get fresh identities. Already executing calls may finish, but
   retired bindings drain before acknowledgement and cannot be reused.

Policy transitions serialize with the affected provider's lifecycle. Callers wait
for both lifecycle and affected policy work; cancellation only ends their wait.
Failures appear in `Backends` and keep exclusions in force. Other providers retain
independent progress. GSMTC bypasses retention grace for excluded bindings and
tracks failed cleanup through disposal.

## Settings and the Media sources page

Media sources owns provider enablement and configuration; General Settings owns
shared preferences. Configuration remains available while disabled, and saving it
does not enable the provider. All forms use the same settings store and merge only
the keys they own. Passwords remain outside the settings file.

The page reads `Backends`, keeps rows stable by registration ID, and updates row
properties for status or diagnostic changes. Only topology/order changes replace
the item array. Configuration pages are primary actions when supplied; other rows
use enablement. Opening a page does not initialize its provider.

The page subscribes while it has `IListPage.ItemsChanged` listeners, independently
of window visibility. The first listener refreshes and raises ItemsChanged to
cover fetch-before-subscribe; the last listener detaches. `GetItems` works without
starting a subscription. Publication is serialized, callbacks run outside the
page lock, and stale reads cannot overwrite a newer or closed page.

See [hosting-mode settings](media-backend-hosting.md#provider-selection-and-mutual-exclusion)
for exclusivity, migration, and settings recovery.

## Validate a change

Run the shared service and presentation suites from the repository root:

```powershell
dotnet test tests/MediaControlsExtension.Media.Tests/JPSoftworks.MediaControlsExtension.Media.Tests.csproj -p:Platform=x64
dotnet test tests/MediaControlsExtension.Presentation.Tests/JPSoftworks.MediaControlsExtension.Presentation.Tests.csproj -p:Platform=x64
```

Choose regression coverage by the boundary being changed:

| Change | Check |
| --- | --- |
| Provider lifecycle | Empty startup, disconnect/reconnect, disable during work, disposal failure, and independent providers. |
| Routing or ownership | Colliding local IDs, old generations, queued work across handoff, and obsolete artwork. |
| Selection | Refresh stability, ties, cycling, unavailable targets, and old completion after a newer choice. |
| Playback | [Scheduling and confirmation tests](media-playback.md#validation), including the real GSMTC control gate. |
| Presentation/settings | Production subscriptions, open/close/reopen, stable rows, delayed reads, concurrent saves, and rollback. |
| Worker or transport | [Hosting test guide](../../tests/MediaControlsExtension.Media.Hosting.Tests/README.md). |

Managed presentation tests use the CmdPal SDK but do not launch its WinUI host.
Provider fakes do not validate native discovery or a browser transport. For a
packaged smoke test, switch players, inspect/toggle Media sources, close/reopen it,
and save/test VLC configuration. Validate real source activation and native
retirement separately; future browser integrations need their own runtime checks.
