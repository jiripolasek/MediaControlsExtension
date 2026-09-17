# Out-of-process media backend hosting

Worker hosting runs an `IMediaBackend` in its own process. Production GSMTC uses
this mode by default; the same `GsmtcBackend` can also run inside the extension.
The worker executable ships with the extension package.

Use this guide when adding a worker, changing hosting modes, or investigating
transport and lifetime problems. Start with [Media backends](media-backends.md)
for provider contracts, or [Playback](media-playback.md) for command ordering and
GSMTC confirmation.

Jump to [adding a worker](#adding-a-worker-backend),
[switching modes](#provider-selection-and-mutual-exclusion),
[timeouts and capacity](#timeouts-and-capacity), or
[recovery](#recovery-and-diagnostics).

## What moves into the worker

| Stays in the extension | Runs in the worker |
| --- | --- |
| `MediaService`, selection, predictions, and UI | One backend instance and its native dependencies |
| Composite routing and cross-provider coordination | Native discovery, subscriptions, control gates, and artwork reads |
| Process ownership, proxy, and recovery | Backend startup, observations, execution, and cleanup |
| Source application activation | A validated callback to request activation |

Only managed values and artwork bytes cross the boundary. Each worker belongs to
one extension instance and one connection lifetime. Multiple hosted providers get
separate processes. Hosting a local player elsewhere does not make it remote:
`TreatAsLocal` remains true.

The [hosting library](../../src/MediaControlsExtension.Media.Hosting) owns transport,
processes, and recovery. The [worker executable](../../src/MediaControlsExtension.MediaHost)
supplies compiled factories. No assembly path or arbitrary type comes from the wire.

## Adding a worker backend

1. Reference the implementation project from the worker executable.
2. Add a factory under `MediaHost/Backends` matching `WorkerBackendFactory`. It
   receives `HostedBackendContext` and `ILoggerFactory` and returns a fresh backend.
   Initialize resources in `StartAsync`; the backend owns native threading and
   subscriptions, and the host owns disposal.
3. Register its stable factory ID in `WorkerBackendCatalog.Factories`.
4. Register `OutOfProcessMediaBackend` in the extension, supplying that factory ID,
   the worker executable path, and the shared `MediaWorkerOwner`.
5. Configure defaults, exclusive groups, source claims, and any owner activation
   callback in the extension catalog.

`Program` only invokes `WorkerApplication`. Neither needs changes for another
factory. Catalog lookup does not construct a backend; construction waits until
the owner handshake is validated.

Use [GsmtcBackendFactory](../../src/MediaControlsExtension.MediaHost/Backends/GsmtcBackendFactory.cs)
for the activation adapter, or [DummyBackendFactory](../../src/MediaControlsExtension.MediaHost/Backends/DummyBackendFactory.cs)
for a backend without native dependencies. The compiled dummy factory is exposed
as `dummy.worker` in debug/feature-flag builds, disabled by default and outside the
GSMTC group. It can run alongside GSMTC. Both registrations share executable-layout
and logging options. See [Dummy media](dummy-media.md) for behavior.

## Provider selection and mutual exclusion

| Registration | Implementation | Default | Exclusive group |
| --- | --- | --- | --- |
| `gsmtc` | In-process `GsmtcBackend` | Disabled | `windows-media-sessions` |
| `gsmtc.worker` | Worker proxy, factory ID `gsmtc` | Enabled | `windows-media-sessions` |

An exclusive group permits zero or one selected member. It switches whole
providers; [source claims](media-backends.md#source-ownership) replace individual
application identities. Connection failure never automatically chooses a peer.

Switching modes follows this order:

1. Withdraw outgoing routes and stop admitting new work.
2. Cancel and drain work, dispose the backend, and confirm worker process exit.
3. If cleanup cannot be confirmed, keep the group blocked and report the failure.
4. Recheck the latest selection, apply its source policy, and start the incoming provider.

The incoming factory must not run while its predecessor can still operate, even
if the desired selection temporarily becomes empty. Other groups remain independent.
Cancellation after acceptance only cancels the caller's wait.

`SetEnabledBackendsAsync` applies a complete selection atomically; `SetEnabledAsync`
uses the same group rules. Startup rejects conflicting selections before factories
run. Explicit enable or `retryBackendId` retries one faulted provider. Unrelated
settings and policy changes do not retry it. Complete-selection calls wait for all
group transitions, including work already in progress.

### Settings migration and recovery

Choosing a mode saves all affected flags once, publishes one change event, and
rolls back the selection if saving fails. Existing explicit mode choices survive
loading. Without a worker-mode setting, the legacy `gsmtc` flag becomes Windows-media
enablement for the worker; an old off choice stays off. Loading does not rewrite
the file. The next explicit mode change saves both flags.

| Settings condition | Behavior |
| --- | --- |
| Missing file or malformed JSON | Use registration defaults. An explicit save backs up malformed content as `.invalid-*` before repairing it. |
| Invalid/conflicting flags in valid JSON | Keep the affected group inactive and report the error. Load and save share validation, including duplicate keys. |
| I/O or access failure | Keep providers disabled with a diagnostic; never replace a saved off choice with defaults. |
| Explicit toggle while unreadable | Attempt to recover the saved selection first; fail promptly if it remains unreadable. |

Provider settings retry failed reads in the background, backing off from 100 ms to
5 seconds. Recovery restores the complete selection, validates groups, and reapplies
migration without forcing faulted providers to restart. Reads and retry waits do
not hold settings locks; notifications run outside both locks. Disposal cancels
retries, and a late read cannot overwrite a newer explicit choice. The production
binding subscribes before reconciling the latest selection to cover startup recovery.

Media sources subscribes only while it has ItemsChanged listeners. Otherwise
`GetItems` and toggle actions refresh state on demand, including exclusive peers.

## Keep the worker within its owner's lifetime

[MediaWorkerOwner](../../src/MediaControlsExtension.Media.Hosting/MediaWorkerOwner.cs)
owns workers for one concrete extension instance, including instances sharing a
process. Every launch uses a fresh unpredictable pipe name and owner token; the
worker returns a fresh epoch. A PID or backend ID alone cannot identify a lifetime.

On Windows, [OwnedWorkerProcess](../../src/MediaControlsExtension.Media.Hosting/OwnedWorkerProcess.cs)
creates each worker in an unnamed job with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`.
The extension holds the only, non-inheritable job handle. Assignment occurs during
process creation through `PROC_THREAD_ATTRIBUTE_JOB_LIST`, avoiding an orphan window
between creation and assignment. Launch fails if containment cannot be established.

Normal shutdown stops retries and admission, cancels work, requests worker shutdown,
and allows bounded cleanup. If needed, the owner closes the job and waits for
process exit. A broken pipe or failed handshake is terminal to that worker; it
must exit rather than reconnect to another owner. Owner death also terminates the
worker, including during startup or a hung native call. Forced termination cannot
run cleanup.

Failure to confirm process exit fails disposal and blocks exclusive switching.
Logging or swallowing an exception does not establish successful cleanup. Source
application activation runs in the extension so media applications never inherit
a worker job.

Process creation happens outside the owner lock, followed by registration under
it. If shutdown won the race, close the new job and reject registration. Exit waits
own duplicated process handles and unregister on completion/cancellation, so
process disposal cannot invalidate an outstanding wait.

## Protocol and request flow

[PipeProtocol](../../src/MediaControlsExtension.Media.Hosting/PipeProtocol.cs) is
**version 7**. Owner and worker must ship together. Version 7 carries native toggle
capability and the Unconfirmed command result; older peers are rejected.

The local duplex pipe is restricted to the current user/elevation level. Both
peers check the pipe's peer PID against the expected owner/child, then validate
version, owner token, and epoch within the startup deadline. This is a private
trusted-child protocol, not a plugin sandbox.

StreamJsonRpc handles JSON-RPC dispatch, replies, and cancellation. Nerdbank.Streams
multiplexes RPC and artwork streams. RPC frames have a four-byte big-endian length
prefix, validated before payload allocation. No native work runs on the reader.
Generated proxies and DTO metadata keep the protocol compatible with NativeAOT;
contract changes need an AOT test run as well as a managed build.
The worker validates request IDs and method admission. Cancellation reuses the
library's `RequestId` converter without enabling reflection-based DTO serialization.

### Messages

See [WorkerRpcContracts](../../src/MediaControlsExtension.Media.Hosting/WorkerRpcContracts.cs)
for exact signatures.

| Message | Purpose |
| --- | --- |
| `InitializeAsync`, then `Begin` | Validate identity, factory, initial policy, capabilities, logging, and budgets. Install the returned epoch before starting backend work. |
| `Snapshot` / `SnapshotFailed` | Publish complete state or a recoverable read failure with its applied policy revision. |
| `ExecuteAsync` | Execute captured binding, operation, and semantic command ID; return a typed result. |
| `ApplyPolicyAsync` | Apply a complete source policy and return its acknowledged revision. |
| `InvalidateAsync` | Coalesce observation requests in the worker's session namespace. |
| `CopyArtworkAsync` | Copy an exact versioned image to an out-of-band stream and return metadata. |
| `ActivateSourceAsync` | Reverse callback for one admitted activation command, epoch, and binding. |
| `$/cancelRequest` | Cancel waiting/work without undoing side effects or immediately releasing admission. |
| `Shutdown` / `ReportFaultAsync` | End the lifetime; ReportFault acknowledges a diagnostic before termination. |

The worker serializes full snapshot reads and coalesces signals. The proxy caches
translated snapshots, so its `ReadSnapshotAsync` needs no blocking IPC round trip.
Commands, policies, and invalidations request observation refreshes; artwork reads
do not. Commands and artwork remain independent of snapshot reads.

### Timeouts and capacity

Defaults live in [WorkerOptions](../../src/MediaControlsExtension.Media.Hosting/OutOfProcessMediaBackend.cs).
All budgets must be positive; providers with longer operations must configure them.

| Budget | Default | Covers |
| --- | --- | --- |
| `StartupTimeout` | 30 s | Launch, handshake, policy, and first accepted snapshot. |
| `RequestTimeout` | 5 s | Commands, invalidations, and frame queue/write budgets, including cancellation frames. |
| `ObservationTimeout` | 30 s | Snapshot reads, consecutive read-failure recovery, and artwork. |
| `PolicyTimeout` | 45 s | Policy changes, including a preceding read and binding cleanup. |
| `ShutdownTimeout` | 5 s | Graceful process-exit wait; the owner also enforces its own shutdown deadline. |

During startup, requests use the startup budget; worker request deadlines begin
after backend initialization. Afterwards, admission and completion each get the
selected budget. Completion timing starts after the frame is sent. Writer queueing
and actual writing also get separate budgets.

| Capacity | Limit |
| --- | --- |
| Owner requests, including those waiting for admission | 64 |
| Admitted RPC requests | 32 on each side |
| Admitted artwork requests | 2 on each side; includes stream consumption |
| Concurrent artwork payload transfers | 1; up to 2 backend reads may overlap |
| RPC frame / encoded artwork / artwork write chunk | 16 MiB / 32 MiB / 64 KiB |

When all owner slots are occupied, coalesced invalidations wait for capacity before
admission; other requests fail as unavailable. An unsent admission/queue timeout
leaves the connection usable. A completion timeout after sending retires it because
the outcome may be unknown. A timeout during a partial frame write closes the pipe.
Worker watchdogs enforce deadlines independently and close before logging, so a
blocked diagnostic sink cannot extend the lifetime.

### Cancellation and artwork

Caller cancellation returns promptly, but transport admission remains held until
the terminal reply. Cancellation can stop a frame while queued; after writing
starts, the frame finishes under connection deadlines. This preserves framing for
other requests. Late activation callbacks are ignored; active callbacks must match
the command, correlation, epoch, and binding and use the original command deadline.

Artwork uses a separate one-way binary stream, never JSON/base64. Its four-byte
big-endian length precedes exactly that many bytes and EOF. The owner reads while
the RPC runs, checks length before allocation, then checks EOF and returned metadata.
Invalid lengths, truncated successful transfers, trailing bytes, or mismatched
metadata retire the connection. Cancellation and failures close stream endpoints;
artwork backpressure and cancellation do not corrupt RPC framing.

## Recovery and diagnostics

A disconnect immediately withdraws routes, marks state unavailable, fails pending
requests, and invalidates old artwork. Never replay commands, including those whose
replies were lost: native work may already have taken effect.

On restart, translate worker-local IDs into fresh proxy-local IDs. Preserve binding
generations within an epoch and translate hints, artwork, invalidations, commands,
and secondary results consistently. Proxy revisions never reset. Do not identify
replacement sessions by title or application ID.

Reapply the latest source policy before accepting discovery. A connection is ready
only after a complete snapshot acknowledges that policy; an empty snapshot is valid.
Live exclusions withdraw routes immediately while preserving unaffected sessions.
Re-inclusion waits for a fresh observation and assigns fresh identity. Claims remain
in force while their owner is enabled, even if disconnected or faulted.

| Failure | Recovery |
| --- | --- |
| Snapshot read exception | Report `SnapshotFailed`; retry after 500 ms, 1 s, then 2 s. Keep sessions unavailable. A successful read resets backoff and the recovery window. |
| Consecutive read failures exceed `ObservationTimeout` | Attempt `ReportFaultAsync` with the last error, then close. Fault delivery is bounded to one second. |
| Hung operation, startup timeout, broken transport, or terminal backend fault | Close the connection and retire the worker. Confirm exit before replacement. |
| Worker restart | Default delay starts at 200 ms, doubles to a 2 s cap, with at most three replacements. |
| Thirty continuous healthy seconds | Reset the restart budget only while snapshots are connected, available, and acknowledge the current policy. Read failures and pending policy changes interrupt the interval. |
| Restart budget exhausted | Fault `WatchAsync`; an explicit enable can create a new provider instance. |

Recoverable failures keep `WatchAsync` alive. Errors from obsolete policies and
replies from obsolete epochs cannot change current state. The first disconnect
cause is preserved in the supervisor snapshot and logs. Mark the connection closed
before canceling requests or disposing RPC: inline completions must not mistake a
terminal timeout for an unsent request that can be retried.

### Logs and memory

Each worker has one buffered UTF-8 log writer. It flushes low-level messages every
second and warnings/errors immediately. Clean disposal flushes the remainder;
forced termination can lose buffered messages. A 4 MiB limit or I/O failure disables
the writer. Startup retains up to sixteen logs, subject to files still in use.

Both processes import [MediaMemory.props](../../eng/MediaMemory.props), setting
`System.GC.ConserveMemory=9` for managed and NativeAOT builds. Use the build property
`MediaGcConserveMemory` for comparisons; this is a GC preference, not a memory cap.

[ProcessMemoryMaintenance](../../src/MediaControlsExtension.Media.Hosting/ProcessMemoryMaintenance.cs)
checks every 30 seconds. After 30 seconds without user input, it can trim the working
set when private committed bytes and total working set both meet the threshold:
32 MiB in the extension, 16 MiB in the worker. Attempts are at least five minutes
apart, including failed trims. Disposal cancels the timer. Input age uses Windows
`GetTickCount`, with wraparound handling, to share the input timestamp's clock.

Trimming releases resident pages; it does not reduce private committed memory or
discard live state. Later access can fault pages back in. Production code does not
periodically force a full garbage collection.

## Validate a hosting change

The [hosting test README](../../tests/MediaControlsExtension.Media.Hosting.Tests/README.md)
is the command reference for managed/AOT subprocess tests, package checks, controlled
native playback, and memory/soak tests. Start with:

```powershell
./tests/MediaControlsExtension.Media.Hosting.Tests/Run-Tests.ps1
./tests/MediaControlsExtension.Media.Hosting.Tests/Run-Tests.ps1 -NativeAot
```

Match validation to the change:

- **Transport:** real pipes/processes, round trips, malformed frames, identity/version
  mismatches, cancellation, capacity, and artwork stream failures.
- **Lifetime/recovery:** owner death, broken pipe during hung work, startup/cleanup
  hangs, no orphan worker, fresh replacement identities, and no command replay.
- **Selection/policy:** core tests for exclusive switching, blocked cleanup,
  independent providers, and policy acknowledgement before discovery.
- **Deployment:** packaged owner/worker identity and `globalMediaControl`, production
  activation/disposal commands, and real GSMTC behavior. ARM64 compilation alone
  does not establish behavior on ARM64 hardware.

Synthetic transport tests, controlled native sessions, production COM command
checks, and interactive WinUI checks provide different evidence. Keep them separate
when reporting results; sleep/resume and real source-window activation need
interactive validation.

For transport build changes, retain generated serialization metadata and the
projects' `DisableTransitiveFrameworkReferences` setting. It prevents a transitive
Windows threading asset from adding WPF; it does not replace direct/SDK references
or require substituted package assemblies.
