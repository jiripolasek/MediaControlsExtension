# Playback and command scheduling

This guide explains what happens between pressing Play and seeing confirmed
playback. For provider setup and identity rules, start with [Media backends](media-backends.md).
For process and transport timeouts, see [Worker hosting](media-backend-hosting.md).

## Follow a Play request

1. **Capture the intent.** A labeled control submits its displayed Play, Pause, or
   Stop for the session it represents. Relative `TogglePlayback` resolves once at
   admission. Later UI changes cannot retarget an accepted command.
2. **Capture dependencies.** The service records the primary ID and binding
   generation, plus any sessions that must be paused first.
3. **Publish the prediction.** An accepted Play selects its target and predicts
   Playing before execution starts. This lets the UI respond immediately.
4. **Run in order.** The scheduler waits for earlier work on shared bindings. The
   composite pauses the captured secondary sessions, then rechecks and plays the
   primary. Each provider validates its live binding at execution.
5. **Publish the outcome.** The service reconciles the prediction and reports
   primary and secondary results separately. Native playback confirmation is the
   provider's responsibility; GSMTC's rules are [below](#gsmtc-playback-transitions).

## Order work by binding

[MediaCommandScheduler](../../src/MediaControlsExtension.Media/MediaCommandScheduler.cs)
orders commands by every `(session ID, binding generation)` they touch. Shared
bindings run in admission order, including earlier commands still waiting on other
dependencies. Disjoint bindings can progress independently, even within one
provider. Replacement generations have separate scheduling keys, while execution
checks still reject stale commands.

| Limit or replacement rule | Behavior |
| --- | --- |
| Outstanding commands | At most two per primary binding and 64 across the service; other admissions return Busy. |
| New Play or Pause | Replaces the last queued command for that primary only if it is also Play or Pause. The old request completes as Superseded. |
| Replacement dependencies | Use the new command's captured targets and admission position. |
| Active or non-playback work | Never replaced by the playback replacement rule. |
| Shutdown | Cancels accepted completions, including queued work, and drains active calls before backend disposal. |

Playback has no input cooldown; session navigation retains its throttle. Providers
still enforce their own native concurrency rules. The composite also keeps a guard
for each executing provider binding: a caller timeout cannot permit another call
on that binding until the underlying operation actually finishes.

## Decide which other sessions to pause

`PauseOtherSessionsOnPlay` enables the secondary pause phase. By default, both
primary and secondary sessions must be treated as local. The remote-session option
includes remote sessions in both roles. Unavailable sessions are excluded.

Under the admission lock, include a secondary binding when either:

- Its confirmed or predicted state is Playing and Pause is admissible.
- It has a queued or executing Play or Pause, even if its snapshot or latest
  prediction no longer shows Playing or usable Pause controls.

The second rule prevents a subtle ordering bug: A can have Play running and Pause
queued, so its latest prediction says Paused. Play on B must still wait for A's
work and recheck whether A needs pausing. The dependency also survives while A's
Pause runs after Play completes. This can make B wait behind an unrelated Pause,
up to the GSMTC transition deadline.

The composite pauses sessions sequentially within each provider and runs different
providers' pause phases in parallel. This avoids making multiple GSMTC pauses
compete for one native control gate. It removes duplicate targets and the primary,
checks each captured binding when its turn starts, and passes leaf backends an
empty `SessionsToPause` list.

Pauses are best effort. Their failure does not turn a successful primary Play into
failure or roll back its prediction. `PauseResults` on the backend result becomes
`PauseOutcomes` with public IDs on the service outcome. A failed or rebound primary
retains completed pause results; cancellation can interrupt the pause phase.
Late completions cannot change a returned result or its captured identity.

## Predictions and displayed actions

[MediaStateStore](../../src/MediaControlsExtension.Media/State/MediaStateStore.cs)
keeps the latest prediction while its command is queued or running. An earlier
matching observation cannot confirm a queued inverse command. After success,
the prediction clears if the confirmed state satisfies it; otherwise its expiry
countdown starts. Stopped satisfies a successful Pause prediction. Normal failure
and abandonment handling clears the affected prediction.

[MediaCapabilityPolicy](../../src/MediaControlsExtension.Media/Infrastructure/MediaCapabilityPolicy.cs)
owns admission and the primary action:

- A pending Play offers Pause, and a pending Pause offers Play, even when reported
  directional controls lag. This does not alter the reported capability flags.
- Direct commands, relative toggles, session cycling, and automatic pauses use
  that admission policy. The provider makes the final control check.
- Confirmed Playing offers Stop when Pause/toggle is unsupported and Stop exists.
  The pending-intent exception applies to Play/Pause, not Stop.

Published UI commands retain an immutable session and action. Repeated invocations
of the same command request the same action; owners publish a replacement when its
presentation changes. Metadata-only changes reuse it, and disposing the owner
disables all its published instances. Immediate feedback describes the submitted
action, even if another command changes the prediction.

Playback, availability, and binding changes bypass the controls' normal debounce.
The dock heading bypasses it only for availability and binding changes. Updates
still use the same serialized path; metadata and artwork remain debounced.

## GSMTC playback transitions

[GsmtcPlaybackController](../../src/MediaControlsExtension.Media.Gsmtc/GsmtcPlaybackController.cs)
shares one **three-second deadline** across readiness, control-gate acquisition,
the native send, and confirmation. New input and retries do not extend it.
Each transition sends at most one primary native command.

### Choose a native operation

Each attempt drains any shared playback read, then reserves the reader while
waiting for the control gate. Drain failures do not fail the command; cancellation
and retirement still stop it. Revalidate state and controls inside the gate
immediately before sending. Cached state is only a readiness hint. Apply these
rules in order:

| Observation | Action |
| --- | --- |
| Pause requested, source Stopped | Skip the native call and confirm Stopped, even if Pause is enabled. |
| Requested directional control enabled | Send that absolute Play or Pause, including when state already matches. |
| State already matches, directional control disabled | Skip the native call and confirm. Ancillary pauses still run. |
| Toggle enabled, state is the known opposite Playing/Paused | Send the native toggle as fallback. |
| No usable control | Wait for readiness within the same deadline. |

For a playing source with Stop enabled but neither Pause nor toggle, require two
consecutive observations 50 ms apart before returning Unsupported. A different
observation resets that check. If the first such observation occurs under the
control gate, release the gate before waiting and reading again. Never substitute
Stop for Pause. Two reads tolerate brief control lag; they do not prove permanent
capabilities, so a later-arriving Pause control can still be missed.

### Confirm the result

| Intent | Successful observation |
| --- | --- |
| Play | Playing. |
| Pause | Paused, or two consecutive Stopped observations after the send/skip decision, at least 50 ms apart. |

Unknown, Changing, Opened, and Closed do not confirm Pause. An intervening state
resets the Stopped count.

Matching Playing/Paused with the directional control disabled completes from the
gated read. A skipped Stopped Pause still needs two later observations. If playback
changes before any mutation, return to readiness. Primary sends and ancillary
pauses are never replayed within a request.

### Share playback observations

[GsmtcPlaybackObservations](../../src/MediaControlsExtension.Media.Gsmtc/GsmtcPlaybackObservations.cs)
shares one outstanding playback read per binding between snapshots and confirmation.
Events only mark changes and wake readers. Successful reads update cached scalars;
newer events remain dirty, and older reads cannot overwrite newer observations.
Failed snapshots retain the cache without repeating the read or warning for the
same event. A transient event-read failure can still use the remaining fallback.

Confirmation uses reads started after the send/skip decision. Retirement wakes
waiters with SessionGone. Native leases and separate RCW root lanes are preserved.
Committed observations satisfy later snapshots without another playback read.

### Bound missing-event reads

Each request has one 500 ms fallback: from request start for readiness, or from
send completion (the decision for skipped Stopped) if unused. Forced readiness
revalidation consumes it. Only the Stopped/stop-only rule adds a scheduled 50 ms
read. Events never extend the three-second deadline or replenish the budget.

Typical playback read counts are two for normal confirmation, one for already
satisfied Playing/Paused, and three for Stopped confirmation. Stable stop-only
takes two; unchanged snapshots take none. Later silent changes may remain
Unconfirmed or Unavailable. Native CFG crashes remain possible.

Snapshots wait one active second plus two seconds of resume grace. On timeout,
they keep cached data and defer that binding's timeline/media reads until recovery,
freeing the global observation gate. Hung playback reads keep their native lease.
Each read logs slowness at two active seconds, a stall at three, and recovery on
return. Timeline, media, or artwork hangs can still block global observations.
Commands retain the control gate's 500 ms acquisition timeout.

## Outcomes and user feedback

| Outcome | Meaning and feedback |
| --- | --- |
| Completed | The provider reported success. A primary Play can still include failed secondary pauses. |
| Failed | The provider rejected the operation or execution failed. Primary failures produce a warning. |
| Unavailable | No native mutation started before the GSMTC deadline, or controls are otherwise unavailable. |
| Unconfirmed | Native mutation started but playback was not confirmed. Do not replay it. |
| Unsupported / SessionGone | Unsupported action or obsolete binding. Secondary pauses with these results are skipped without a pause warning. |
| Abandoned | Deferred Play/Pause depended on an unconfirmed primary or secondary binding and was dropped unsent. Clear its prediction and warn the user. |
| Superseded | Newer playback input replaced queued work. Silent. |
| Canceled | Shutdown cancellation. Silent. |

Abandonment and admission share a lock, so work admitted before unconfirmed
completion is processed still depends on that transition. Independent bindings
and replacement generations remain usable.

Primary failures and abandoned/unconfirmed requests produce localized warnings.
For a successful primary, failed, unavailable, or unconfirmed secondary pauses
produce a pause warning. Waiting commands append it to their result; optimistic
controls observe completion in the background. If completion is already available,
failure feedback replaces the optimistic success toast. All paths respect Show
toast messages. Disposing notification observers does not cancel media commands.

## Validation

The [media tests](../../tests/MediaControlsExtension.Media.Tests) cover scheduling,
predictions, partial pause outcomes, and GSMTC readiness, deadlines, read budgets,
event races, retirement, and snapshot recovery using controlled native delegates.

The [presentation tests](../../tests/MediaControlsExtension.Presentation.Tests)
cover captured button intent, inverse actions, settled Stop fallback, notifications,
and delayed feedback. Run commands are in the [main guide](media-backends.md#validate-a-change).
These tests do not replace real-player and packaged CmdPal checks.
