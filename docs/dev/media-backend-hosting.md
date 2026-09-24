# Out-of-process media backend hosting

Status: implemented as a production MVP with GSMTC hosted in a worker by default.

## Purpose and boundary

Run an existing `IMediaBackend` in a dedicated worker process. Keep `MediaService`,
the composite, session presentation, predictions, and cross-provider coordination
in the extension. Reuse `GsmtcBackend` in both hosting modes. GSMTC native objects,
subscriptions, operation gates, retention, and artwork streams stay in the worker.
Only managed values cross the process boundary.

The generic hosting library owns the proxy, transport, server, process launcher,
and recovery. A small executable supplies explicit backend factories. Each process
hosts one backend instance for one extension owner. Factories are compiled into
the executable; the protocol does not accept assembly paths or arbitrary types.
Out-of-process local playback retains `TreatAsLocal = true`.

## Adding a worker backend

`MediaHost.Program` only starts `WorkerApplication` and exits the process with its
result. `WorkerApplication` handles arguments, factory selection, logging, memory
maintenance and cleanup. `WorkerBackendCatalog` lists the compiled factories by
ordinal backend ID; looking up an ID does not construct its backend. Factory
invocation happens only after the owner handshake is validated.

To add a backend:

1. Reference its implementation project from the worker executable.
2. Add a factory under `MediaHost/Backends` matching `WorkerBackendFactory`.
   It receives `HostedBackendContext` and `ILoggerFactory`, and returns a fresh
   `IMediaBackend`. Initialize native work in `StartAsync`; the backend owns its
   native threading, event subscriptions and cleanup. The host owns its disposal.
3. Add the factory to `WorkerBackendCatalog.Factories` under a stable ID.
4. Register an `OutOfProcessMediaBackend` in the extension with the same factory
   ID and worker executable path. Configure provider selection and source claims
   in the extension's catalog.

Adding a factory does not require changing `Program` or `WorkerApplication`.
`Backends/GsmtcBackendFactory` demonstrates adapting the generic owner activation
callback to a backend-specific interface. Factories may ignore capabilities they
do not need. Each enabled hosted backend still gets its own process.

`Backends/ITunesBackendFactory` creates the native desktop iTunes backend. Its
dedicated dispatcher thread, COM objects, and event sink all remain inside the
worker, while immutable snapshots and artwork cross the existing protocol. The
factory forwards the negotiated owner activation callback. Activation is advertised
only when that callback is available and the iTunes executable path is known.

`Backends/DummyBackendFactory` is another compiled factory. It creates the
provider in `MediaControlsExtension.Media.Dummy` without owner activation or
native dependencies. The extension registers it as `dummy.worker`, disabled by
default and outside the GSMTC exclusive group. It can run alongside GSMTC in a
separate worker without changing `Program`, `WorkerApplication`, or the protocol.
See [Dummy media](dummy-media.md) for its simulated behavior.

All worker registrations use one options helper for the executable layout and
logging policy. GSMTC and iTunes add their owner activation callbacks to those
common options; the dummy factory uses them directly. The iTunes callback focuses
an existing window by executable path and does not launch the application.

## Provider selection and mutual exclusion

The production registrations are:

| ID | Factory | Default | ExclusiveGroup |
| --- | --- | --- | --- |
| `gsmtc` | Existing `GsmtcBackend` | Disabled | `windows-media-sessions` |
| `gsmtc.worker` | Proxy hosting `GsmtcBackend` | Enabled | `windows-media-sessions` |

`ExclusiveGroup` is optional, ordinal registration metadata. A group allows zero
or one selected member. It excludes whole providers and is independent of
application-scoped `ReplacesSources` claims. Disabled, disconnected, and faulted
states do not automatically select another group member.

Selecting a member deselects its peers in the same desired-state transaction.
Settings save all affected choices once, roll the entire change back on save
failure, and publish one change event. Startup validates the complete selection
before invoking any factory. An explicitly supplied conflicting set is invalid;
the settings loader must surface a corrupt saved group selection without silently
choosing in-process GSMTC. When no worker-mode setting exists, the legacy `gsmtc`
flag is interpreted as Windows-media enablement and transferred to the worker.
Disabled Windows media stays disabled. Existing explicit hosting-mode choices are
preserved. Loading does not rewrite settings; the next explicit mode change saves
both flags through the shared writer.

Missing files and malformed JSON use registration defaults. Each store read
makes one attempt. I/O and access failures keep providers disabled with a visible
diagnostic; an I/O failure must not override a saved off choice. Provider settings
then re-read on their own background task, with exponential delays from 100 ms
to 5 seconds. Delays hold neither settings lock, and recovery reads occur outside
the provider settings lock. Successful recovery restores all saved providers,
validates exclusivity, and reapplies legacy Windows-mode migration. An explicit
toggle first attempts to restore the saved selection, then changes its group and
publishes once; an unreadable file fails the toggle promptly. Recovery itself
does not request a fresh instance of a faulted provider. The store has no recovery
callbacks, so unrelated reads and saves cannot trigger provider reconciliation
or inherit its errors. Recovery notifications run outside both locks. Disposal
cancels retries, and a late read cannot overwrite a newer explicit choice.
The extension subscribes before reconciling the latest selection to cover
recovery that finishes during construction.
Invalid or conflicting flags inside valid settings keep the affected group
inactive. Load and save share one parser, including duplicate-key validation.
An explicit save repairs malformed JSON after preserving its original contents
in a unique `.invalid-*` backup. I/O failures fail the save and roll back selection.

Runtime reconciliation is serialized within each exclusive group:

1. Withdraw outgoing routes and stop admitting new work immediately.
2. Cancel and drain outgoing calls, then await disposal and worker process exit.
3. If shutdown fails, retain the group as blocked and report a diagnostic.
4. Recheck the latest desired selection, apply its source policy, and start it.

No incoming factory may run while an outgoing group member can still operate.
Requests converge on the latest accepted selection. Independent groups/providers
continue independently. Cancellation after acceptance cancels only the caller's
wait. A group remains reserved while stopping, even if its desired selection is
empty. Group enforcement belongs in the core API, not only the settings UI.

The registry and composite enforce this contract directly. `SetEnabledBackendsAsync`
accepts a complete selection atomically. `SetEnabledAsync` replaces exclusive
peers through the same implementation. Unchanged faulted providers are not retried
by unrelated selections or source-policy updates. An explicit enable or the
complete-selection API's `retryBackendId` requests a fresh instance for that
provider only. Settings change events carry that intent alongside the latest
complete selection. Recoverable read failures still accept source-policy updates
on their existing run; a successful read clears pending retry intent. Complete
selection calls await all groups, including transitions already in progress,
whether or not a retry provider was specified.

The sources page observes published backend state while it has an ItemsChanged
subscriber. Without a subscriber it stays pull-based: GetItems and toggle
invocations refresh published state, including changes to exclusive peers.

## Owner lifetime

A worker belongs to one concrete extension instance and one connection lifetime.
Instances within the same process have independent owner scopes.
It must not outlive that owner or reconnect to a replacement extension instance.
The owner creates a fresh unpredictable pipe name and owner token per launch.
The worker generates a fresh epoch. Neither a PID nor a backend ID alone identifies
a connection. Retain native process handles while controlling a process.

Normal disable/shutdown stops retries first, sends shutdown, closes admission,
cancels work, and permits a bounded cleanup period. The owner then terminates any
remaining worker and waits for its process handle to signal. A broken pipe is
terminal to that worker: cancel, attempt bounded cleanup, and exit. A failed
initial connection or handshake also has a deadline.
Unconfirmed termination keeps exclusive switching blocked. The extension's
top-level shutdown logs owner cleanup failures instead of throwing them out of
Main; this does not turn an unconfirmed exit into a successful teardown.

On Windows, each worker is assigned to an unnamed job configured with
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. The extension holds the only job handle and
does not make it inheritable. Owner termination therefore terminates its worker,
including during startup or an unresponsive native call. Normal owner shutdown
allows cleanup before closing the job. Forced termination does not run cleanup.

The direct launcher assigns the job at process creation with
`PROC_THREAD_ATTRIBUTE_JOB_LIST`. Creating a suspended process and assigning the
job in a later call still leaves a suspended-orphan race if the owner dies between
those calls. Launch fails if lifetime containment cannot be established.

The owner checks shutdown under its lock, creates the process outside the lock,
then registers it under the lock. If shutdown began during creation, it closes
the new process's job and rejects registration. Slow native creation cannot
block another worker or prevent the owner from initiating shutdown. Process-exit
waits use thread-pool registration on duplicated process handles; each wait owns
its handle and unregisters on completion or cancellation. Process disposal can
close the original handle while an outstanding wait still observes process exit.

Only worker processes belong to these jobs. Source application activation stays
in the extension so launched media applications do not inherit a worker's job.
The packaged launcher must preserve package identity/capabilities and pass the
same ownership tests. A COM activation lease alone is not an owner-lifetime guard.

## Protocol version 6

Use a local asynchronous duplex named pipe restricted to the current user and
elevation level. Verify the peer PID using the pipe handle against the process
created by the owner; the worker verifies the pipe server against its owner PID.
Validate protocol version, owner token, and worker epoch. Use a bounded handshake
and reject messages from another lifetime. This is a private trusted-child
protocol, not a general local service or an untrusted plugin sandbox.

Control messages use JSON-RPC 2.0 through StreamJsonRpc, with source-generated
proxies, target metadata and System.Text.Json DTO metadata for NativeAOT.
Nerdbank.Streams multiplexes the RPC channel and artwork streams over the pipe.
The RPC channel uses a four-byte big-endian length prefix. `WorkerMessageHandler`
limits each frame to 16 MiB before allocating incoming payloads and enforces
serialized writes, queue/write deadlines, duplicate IDs and worker admission.
StreamJsonRpc owns method dispatch, reply correlation and cancellation messages.
The reader must remain responsive while startup, commands, artwork, or cleanup
are pending. No native work executes inline on that reader.

| Message | Contract |
| --- | --- |
| InitializeAsync / Begin | Validate version, owner token, backend ID, initial policy, activation capability, logging and budgets; return a fresh epoch. Begin starts backend work after the owner installs that epoch. |
| Snapshot | Complete immutable snapshot; no deltas or native references. |
| SnapshotFailed | Recoverable read error and applied source-policy revision; keeps the same connection and session identities. |
| ExecuteAsync | Captured session and binding, operation, semantic command ID and typed result. |
| CopyArtworkAsync | Exact versioned key and a write-only out-of-band destination stream; returns content metadata and length after copying. |
| InvalidateAsync | Coalescible observation invalidations in the worker's session namespace. |
| ApplyPolicyAsync | Complete policy; returns the applied revision, which subsequent snapshots also carry. |
| ActivateSourceAsync | One reverse RPC tied to an admitted activation command, epoch and binding. |
| $/cancelRequest | Library cancellation of an admitted operation; admission remains occupied until its terminal reply. Does not undo side effects. |
| Shutdown | Terminates this connection and backend lifetime. |
| ReportFaultAsync | Acknowledged diagnostic followed by connection termination; no exception objects on the wire. |

Artwork is limited to 32 MiB of encoded image bytes and 64 KiB per stream write,
with one payload transfer at a time. Up to two backend reads may overlap so a slow
read does not hold the transfer lane; both peers cap admitted artwork requests at
two to bound retained image buffers. Each binary stream begins with a four-byte
big-endian payload length, followed by exactly that many bytes and EOF. A one-way
pipe lets the worker close its writing side and the owner observe EOF. The owner
reads concurrently with the RPC, validates the length before allocating, and
checks EOF and the returned metadata before closing the stream endpoints.
Invalid lengths, truncated successful transfers, trailing bytes and mismatched
metadata retire the connection and return unavailable artwork. It also closes the endpoints on
cancellation or failed reads. Binary stream backpressure does not block the RPC
channel. Artwork bytes are never encoded as JSON or base64.

The formatter uses only generated DTO metadata. A narrow metadata resolver reuses
StreamJsonRpc's own `RequestId` converter for cancellation messages; it does not
enable reflection serialization. Changes to contracts must be exercised in the
NativeAOT hosting suite as well as a managed build.

The extension, worker, hosting library, hosting tests and two RPC experiments set
the SDK's `DisableTransitiveFrameworkReferences` property in their project files.
This prevents the threading package's Windows asset from adding WPF to those
consumers. Other projects keep the SDK default. Direct and SDK framework
references still apply. The normal NuGet-selected assemblies are used; no
package fork or DLL substitution is needed.
The setting is implemented by the SDK's
[ResolvePackageAssets task](https://github.com/dotnet/sdk/blob/main/src/Tasks/Microsoft.NET.Build.Tasks/ResolvePackageAssets.cs).

The owner records the first available disconnect cause before teardown and
preserves it in the supervisor's snapshot and logs, including invalidation
failures. The handler retains fatal read/write errors before canceling transport
operations. A frame write deadline is reported as a timeout; ordinary caller
cancellation does not become a connection failure.
The connection publishes its closed state before canceling requests or disposing
the RPC endpoint. Request completions can run inline during disposal; policy
updates must already see the closed connection and absorb its terminal timeout,
and invalidations must not retry that timeout as an unsent request.

Worker operation and snapshot watchdogs close the connection at their negotiated
deadline, then report the expired operation and budget to the configured worker
log. A blocked or failing diagnostic sink cannot delay that closure. These hard
closures do not wait for an RPC fault acknowledgement, so the owner may only
receive a generic disconnect reason. Worker logging is best effort and requires
logging settings from the owner.

The worker coalesces backend signals and serializes complete snapshot reads.
The proxy caches translated snapshots and publishes local invalidations after
updating state. Its `ReadSnapshotAsync` does not make a blocking IPC round trip.
Control/artwork requests are independent of observation reads; backend-specific
scheduling remains owned by the leaf. Bulk transfer and admission must stay bounded.

Snapshot read exceptions send SnapshotFailure and retry with delays of 500 ms,
1 second, then 2 seconds, without holding the observation lane. The proxy logs
the error and reports a read failure, so the composite retains sessions as
unavailable until a fresh snapshot arrives. It discards errors from an obsolete
policy. The first exception starts an ObservationTimeout recovery window; a
successful read resets the window and backoff. If the window expires, the worker
attempts ReportFaultAsync containing the last read error, then closes the
connection and applies normal restart limits, even if another read or policy
call is stuck. The owner acknowledges recording the diagnostic before the worker
closes the multiplexed transport. Fault delivery has a one-second limit and always closes the
connection, including when the peer is not reading. If delivery fails, the owner
can still report a transport error; the earlier recoverable error remains logged.
Transient failures within the window preserve the worker and identities.
Hung observations, startup deadlines, transport failures and
terminal backend faults still close the connection. Owner and worker must be
published together for protocol v6. There is no v5 compatibility path.

The default owner startup budget is thirty seconds, covering launch, handshake,
initial policy and the first accepted snapshot. InitializeAsync carries the initial policy;
the owner sends another policy request only if it changed. A policy request queued
during startup uses the startup token on the owner. Worker request deadlines start
after backend initialization. The owner negotiates separate positive budgets:

| Budget | Default | Covers |
| --- | --- | --- |
| RequestTimeout | 5 seconds | Commands, invalidations, and each serialized frame write, including cancellation frames. |
| ObservationTimeout | 30 seconds | Complete snapshot reads, consecutive snapshot-failure recovery, and artwork requests, allowing GSMTC's native observation timeout to finish first. |
| PolicyTimeout | 45 seconds | Policy requests, including a preceding snapshot and native binding cleanup. |

Both peers apply the negotiated budget for each typed method. The defaults allow a
full observation plus cleanup within the policy budget.
Backends with longer operations must configure appropriate budgets explicitly.
Outside startup, owner requests have separate admission and completion budgets
of the selected duration. Completion timing starts after the request frame is
sent. When all 64 request slots are occupied, coalesced invalidations wait for a
slot before entering admission; a released slot or connection closure wakes the
waiter. Other requests report unavailable when the queue is full. An admission
timeout before a request is sent leaves the pipe and admitted peers usable;
invalidations remain coalesced for retry. Each frame also has a
bounded writer-queue wait and a fresh RequestTimeout once it acquires the writer;
queue time cannot shorten the frame's own write budget. A queue timeout alone
does not close the transport. A completion timeout after sending may have an
unknown outcome and retires the connection. Connection shutdown or a real
timeout during a partial write closes the pipe because its frame can no longer
be completed safely. Worker operation watchdogs remain independent.

Both peers admit at most thirty-two requests. Caller cancellation releases its
wait immediately, but retains its transport admission until the worker removes
the request and sends its final reply. Artwork admission also covers stream
consumption; the worker releases admission before writing the RPC reply. Late
activation callbacks for canceled or completed requests are ignored. Callbacks
for active requests still require matching operation, binding and correlation.
Caller cancellation can stop a frame while it waits for the writer. Once the
write starts, it finishes under the connection lifetime and negotiated deadlines;
caller cancellation cannot interrupt it. This applies to owner requests, worker
activation callbacks. Artwork cancellation closes its separate stream without
damaging RPC framing. A canceled owner caller returns immediately
while the frame finishes and transport admission remains held until the terminal
reply. The worker finishes its frame before acknowledging cancellation,
preserving framing for unrelated controls and observations.

Artwork reads do not force snapshot refreshes. Execute, policy and invalidation
requests do; backend observation signals remain independent. The proxy reports
observations, and the composite derives actual topology and provider-state changes.

## Identity, recovery, and operations

Proxy revisions never decrease or reset during an instance's lifetime. Translate
worker-local session IDs to fresh proxy-local IDs for every worker epoch. Preserve
binding generations within an epoch and translate every artwork key, selection
hint, invalidation, command target, and secondary result consistently. Do not match
sessions after restart by title or application ID. Old handles become obsolete.

On disconnect, immediately publish unavailable state and withdraw routes. Fail
pending requests, reject old replies, and invalidate old artwork keys. Keep
`WatchAsync` alive across recoverable failures. The owner may launch a replacement
with bounded exponential backoff and a restart budget. Thirty continuous seconds
of connected, available snapshots acknowledging the current policy reset the
failure count. Read failures and pending policy changes interrupt that interval;
time spent failing or shutting down cannot replenish the budget. Exhaustion
faults `WatchAsync`, allowing the composite to mark the provider Faulted and
recreate it on an explicit enable request.

Reapply the latest source policy before initial or replacement discovery. A pipe
connection alone is not evidence of usable media state; a complete snapshot must
acknowledge the current policy. An empty connected snapshot is valid. During a
live policy change, immediately filter excluded routes and preserve unaffected
sessions, identities and availability. Re-inclusion waits for a fresh worker
observation and assigns a new identity to a previously excluded session.

Never replay commands after disconnect, including commands whose replies were
lost. Native work may have taken effect before cancellation, timeout, or process
death; success requires an actual matching response. Refresh observed state rather
than guessing whether a skip/toggle happened. Timeout can retire an unresponsive
worker, but replacement must wait for the previous process to exit.

If graceful exit fails, close the worker job and wait again. Recoverable shutdown
failures are logged without faulting disposal. If process exit still cannot be
confirmed, fail disposal and keep the exclusive group blocked. A peer must never
start merely because a teardown exception was swallowed.

`IMediaSourcePolicyBackend` is part of the hosted contract. Apply exclusions before
startup, after reconnect, and at the native execution boundary. Keep claims while
their owner is enabled but disconnected/faulted. VLC and future browser claims
must target both `gsmtc` and `gsmtc.worker`; do not overload exclusivity metadata
to change source identity semantics.

Production source activation uses a bounded extension callback associated with
the admitted command and validated worker binding. The extension checks the
connection and command lifetime before activating a source. Callback support is
negotiated; do not advertise activation without an implementation. The callback
uses the original command deadline and runs off the pipe reader. An explicit
request context associates it with the command; titles are only window hints.
For iTunes, the owner copies the worker-reported executable path from the current
binding's snapshot into the callback request. The activation RPC cannot supply
or override that path. This validates the binding, not the executable's provenance.

## Worker logging

Each worker keeps one buffered UTF-8 writer open. A one-second timer flushes trace
and information messages; warnings and errors flush immediately. Clean
disposal flushes the final buffer, while forced termination can lose buffered
low-level messages. Encoded byte counts enforce a 4 MiB file limit without
per-line size queries or file opens. Reaching the limit or an I/O failure closes
and disables the writer. Startup retains up to sixteen worker logs, subject to
files still in use by other workers.

## Memory policy

The extension and worker import `eng/MediaMemory.props`, setting
`System.GC.ConserveMemory=9` in both managed and NativeAOT builds. This favors
smaller managed heaps over allocation throughput; it does not impose a memory
limit. Override `MediaGcConserveMemory` at build time for controlled comparisons.

Each process checks its memory every thirty seconds. After at least thirty
seconds without input in the user's Windows session, it calls `EmptyWorkingSet`
when both private committed bytes and total working-set bytes exceed its soft
threshold: 32 MiB for the extension and 16 MiB for the worker. Attempts are at
least five minutes apart, including failed attempts. Disposing the process-level
maintenance service cancels its timer immediately.

This releases resident pages, including shared pages, without discarding live
state or reducing private committed memory. Later use can fault those pages back
into RAM. It is a visible-footprint policy, not a hard cap or proof that allocations
are bounded. Production code does not periodically force full garbage collections.
Debug diagnostics record trims and counter failures. Input timestamp wraparound
is supported; future input timestamps are treated as recent activity. The idle
comparison uses Windows `GetTickCount` directly so both values use the same clock.
It does not depend on `Environment.TickCount`, whose Windows sleep-time behavior
[changes in .NET 11](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/11/environment-tickcount-windows-behavior).

## Acceptance

The hosting tests run real subprocesses for the transport and lifetime checks. A
synthetic backend provides deterministic snapshots, commands, artwork, exclusions,
slow reads, cancellation, startup hangs, and cleanup hangs. An optional inspection
command reads existing GSMTC sessions without changing playback. The native
acceptance runner publishes its own controlled session for playback and artwork
checks. A separate COM client exercises the production page commands and provider
disposal without automating the WinUI view.

Required checks:

- Snapshot/command/artwork/invalidation round trips and source-policy acknowledgement.
- Old session targets, artwork, and pending commands cannot affect a replacement worker.
- Broken pipe exits the worker even while backend work or cleanup ignores cancellation.
- Normal disposal and forced owner death leave no worker, including during startup.
- Worker termination leaves its owner alive; recovery creates a new process/epoch.
- Conflicting initial selections fail before factories run; group replacement waits
  for outgoing disposal and is blocked by failed cleanup; unrelated providers work.
- Invalid frames and handshake identity/version mismatches fail within a deadline.
- NativeAOT x64 validation; ARM64 build evidence is distinct from hardware execution.
- Packaged owner/worker identity and `globalMediaControl` activation are validated
  separately from unpackaged transport tests and existing probe evidence.

The original spike is superseded. Its launch scripts now use the production
hosting tests. Release acceptance includes
real GSMTC controls, package activation and hardware-dependent checks.

## Platform references

- [Windows job objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)
- [Process creation attributes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute)
- [Named-pipe peer PID](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid)
- [PipeOptions.CurrentUserOnly](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions)
- [System.Text.Json source generation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation)
- [.NET garbage-collector configuration](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector)
- [EmptyWorkingSet](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-emptyworkingset)
- [GetLastInputInfo](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getlastinputinfo)
