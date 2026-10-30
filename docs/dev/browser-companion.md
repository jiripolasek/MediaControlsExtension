# Browser companion

The companion combines a Manifest V3 extension for Edge, Chrome, and Firefox, a
native messaging bridge, and a separate .NET 10 relay. The optional Media Controls
backend publishes one primary player per tab and routes commands to its exact
browser document. See the [setup guide](../user/browser-companion.md) for loading,
registration, updating an existing installation, and automatic Windows fallback.

The bridge is included in the Media Controls package. Setup uses a helper running
outside package identity with the existing `runFullTrust` capability. Explicit
removal deletes owned registration, external manifests, and the Firefox launcher;
MSIX uninstall alone leaves these behind.

## Source layout

- `src/BrowserCompanion/extension`: unpacked browser extension and popup.
- `src/BrowserCompanion/host`: native messaging bridge, relay, and diagnostic CLI.
- `src/MediaControlsExtension.Media.Browser`: desktop backend and fallback policy.
- `tests/BrowserCompanion`: Node tests, media fixture, and popup preview.
- `eng/Register-BrowserCompanion.ps1`: current-user native-host registration.
- `eng/Build-BrowserCompanion.ps1`: self-contained release bundle and checksum.
- `src/BrowserCompanion/setup`: portable setup and shared registration logic.
- `eng/BrowserCompanion.targets`: isolated, self-contained bridge payload for the package.
- `src/BrowserCompanion/shared`: setup launcher shared by the desktop UI and bridge CLI.

## Packaged setup and removal

The package owns the hidden `BrowserBridge` application and console execution
alias `MediaControlsBrowserBridge.exe`. The Chromium native-host manifest points
to that stable alias. Firefox rejects execution-alias reparse points, so its
separate manifest points to a small `firefox-bridge.cmd` launcher that forwards
the original arguments and standard streams to the same alias. Its relative
path stays valid across upgrades and user directories with non-ASCII names.
Neither manifest contains a versioned package path.
The bridge and relay still start on demand; no startup task or Windows service
is installed.

The Media sources configuration page launches a packaged PowerShell script with
shell execution. The script verifies that it is running outside package identity
before writing real HKCU keys for Chrome, Edge, and Firefox. A current-user-only named pipe
returns the result, with a bounded timeout. Setup enables the Browser companion
source only after registration succeeds. Removal deletes only keys pointing to
the owned external manifests, deletes those manifests and the Firefox launcher, closes the installed bridge
processes, then disables the source. It can be run repeatedly.

Registration lives in `%LOCALAPPDATA%\JPSoftworks\MediaControls\BrowserCompanion`.
The manifests and Firefox launcher are outside MSIX ownership. Uninstall removes
the alias and package binaries but leaves these files and registry keys; users can remove integration
before uninstalling or reinstall Media Controls and run removal afterwards.

`setup/packaged-integration.json` contains the package name, alias, and the Chrome
and Edge store extension allowlist in `storeExtensionIds`. Normal setup uses this
allowlist and reuses the currently registered IDs. Unpacked Chromium extensions
require their own IDs under **Configure > Advanced > Developer setup**; saving
that form replaces previous developer IDs. The CLI also replaces them with its
supplied IDs. The store allowlist is always included.

Firefox uses the fixed add-on ID `media-controls-companion@jiripolasek.com`, from
`firefoxExtensionIds`, and its native manifest uses `allowed_extensions`.
Build its extension directory with `eng/Build-BrowserExtension.ps1 -Browser Firefox`.
The generated manifest selects a module background page instead of a Chromium
service worker and requires Firefox 153+, where exact document IDs are supported.
Firefox omits `documentLifecycle`; explicit non-active states and missing document
IDs are still rejected. Browser-owned document IDs remain mandatory for commands.

The installed alias supports `--register-browser-integration <comma-separated-ids>`
and `--remove-browser-integration` for diagnostics. Both exercise the same launcher
as the desktop setup commands. Portable developer builds omit the packaged
configuration and continue to use their adjacent `native-host.json`.

The host has its own `Directory.Build.props` and no external dependencies. It
builds independently of the Windows application packaging projects. Building and
testing do not register the native host or deploy Media Controls.

The native host name is `com.jiripolasek.media_controls`. Setup migrates the old
spike registration when it points to this installation's manifest. The pipe
prefix and `BROWSER_MEDIA_SPIKE_INSTANCE` variable retain their historical
identifiers for compatibility; the relay wire protocol is unchanged.

## Command Palette link

The popup's **Open Media Controls** link uses an `x-cmdpal://commands/` URI to open
the main Media Controls page without a native bridge connection. From that page,
open **Media sources > Browser companion > Configure** for setup.

The anchor is defined in `src/BrowserCompanion/extension/popup.html`. Keep its
package identity and command ID aligned with the package manifest and
`MediaControlsExtensionPage.Id` when either changes.

## What it discovers and controls

| Source | Discovery and control |
| --- | --- |
| HTML audio/video | One entry per element after unmuted playback at nonzero volume; retains paused players and includes matching frames and open shadow roots. Play, pause, seek within current seekable ranges, element volume/mute, and rate. |
| Script-created detached media | Elements observed through the patched `play()` method remain addressable while the page retains them. This is best-effort coverage. |
| Media Session API | One additional document entry when metadata or captured handlers exist. Publishes title/artist/album and calls the site's registered play/pause/next/previous handlers. |
| Source activation | Checks the captured document/player, activates its tab, resolves its current owning window, restores a minimized window, and requests foreground focus. |

HTML discovery ignores unplayed elements and muted or zero-volume autoplay
previews. Playback events preserve eligibility even if the player pauses or mutes
before the next snapshot. Eligibility resets when the source changes or the
element is emptied. A paused element with played ranges can also qualify when
first observed with sound enabled. Snapshots and commands use the same check.

Media Session represents a document's integration with browser/OS controls; it
does not identify the underlying HTML element. The document entry and an HTML
entry can represent the same playback. The popup groups them by browser tab ID,
including that tab's frames, while preserving all original player identities.
Different tabs with identical titles or URLs remain separate groups.

Source tags use the top-level page's `og:site_name` or `application-name`
metadata with the browser-provided tab favicon, including for embedded players.
Names are limited to 128 characters; the desktop backend falls back to the tab
hostname, then the browser name. Connection diagnostics retain the browser and
profile identifier. These labels do not affect identity, routing, or fallback.

The main controls use one primary entry, chosen in this order:

1. A Media Session entry with play/pause handlers in a document observed playing.
   That observation can come from the document entry or an HTML player in the
   same document; it does not establish an exact element-to-handler mapping.
2. A playing HTML player, including one inside an embedded frame.
3. An idle Media Session entry with play/pause handlers.
4. Another controllable HTML player, preferring one that has not ended.
5. An entry with only other capabilities, such as source activation.

Equal candidates retain the existing primary across snapshot reorderings; the
initial choice prefers the top-level frame. A playing embedded document takes
precedence over an idle top-level document. Metadata-only Media Session entries
do not hide an HTML player's playback controls. This is a presentation heuristic,
not proof that entries are duplicates; unrelated players in the same tab remain
available under the **players in this tab** list.

Expanded lists and unchanged control buttons survive periodic refreshes. Every
button captures one concrete player ID and connection epoch. If that target
disappears or reconnects, the operation fails; it is never redirected to the new
primary. Capabilities are not combined across players. `--list` still returns the
complete ungrouped catalog, and existing CLI target IDs retain their meaning.

After updating the popup, reload the unpacked extension and existing media tabs.
Changes limited to the popup do not require rebuilding or registering the native host.

"All media sessions" means all players observed by these adapters, not an
enumeration of Chromium's internal media-session service. There is no documented
general extension API for that enumeration. In particular:

- Web Audio-only players without Media Session handlers, inaccessible/closed
  shadow content not observed through `play()`, browser-internal pages, extension
  stores, and pages without granted site access are not covered.
- HTTP/HTTPS access is requested for discovery in inactive tabs and frames.
  File URLs and private/incognito browsing are excluded. Cross-origin frames
  require matching host access; related frames use `match_origin_as_fallback`.
- Existing tabs need a reload after install/update. A site that overwrites the
  hook or registered handlers before injection can defeat handler discovery.
- Autoplay policy, DRM/player state, frozen tabs, or site behavior can reject or
  stall controls. An audible tab alone is not treated as a controllable player.
- The main-world hook polls every two seconds, scans open shadow roots, and
  wraps `play()` and `setActionHandler()`. Performance and site compatibility
  need measurement before broader use. Limits are 128 players per document and
  256 entries per browser connection; oversized catalogs are outside the supported catalog size.
- Metadata comes from untrusted page code. A page can spoof its own observations
  or acknowledgements. It cannot supply native code, select another tab/frame,
  or send arbitrary privileged commands through the bridge.
- A successful HTML play/pause checks the immediate element state. A successful
  Media Session action means the handler returned, not that playback was later
  observed in the requested state. A timeout is `unconfirmed`; it is never retried.
- Browser API activation is not atomic with navigation or tab movement. Targets
  are checked after waits, but a final race or OS foreground policy can still
  prevent the desired visible result. Live Windows checks remain necessary.

## Process and ownership boundaries

```text
Page media / Media Session handlers (MAIN world)
    | page messages: observations and command results only
Isolated content script, one per document/frame
    | extension port; browser supplies tab/frame/document identity
Manifest V3 service worker
    | Native Messaging: length-prefixed UTF-8 JSON
Browser-launched JPSoftworks.MediaControlsExtension.BrowserCompanion.exe bridge
    | current-user-only Windows named pipe
JPSoftworks.MediaControlsExtension.BrowserCompanion.exe --service (independent catalog and command router)
    | same pipe, one request per CLI connection
JPSoftworks.MediaControlsExtension.BrowserCompanion.exe --list / --command
```

The browser launches one bridge per native port. The bridge owns no catalog and
does not start/stop the media service. EOF in either direction ends the bridge.
The service does not own or terminate browser processes. Native stdout contains
only framed protocol messages; diagnostics use stderr.

The named pipe is scoped by Windows user SID, uses `CurrentUserOnly`, and has a
single service owner. The native host verifies the caller against the adjacent
manifest's explicit `allowed_origins` or Firefox `allowed_extensions`. Firefox's
manifest argument must match the expected owned manifest path before it is read.
This is a local same-user integration, not
a security boundary against other processes running under that Windows account.
No network listener is used by the backend. The optional fixture listens only
on loopback. Metadata stays in memory; page URLs omit query strings/fragments,
but titles and URL paths may still contain private information.

## Protocol version 1

Both native messaging and the local pipe use a four-byte little-endian byte
length followed by a UTF-8 JSON object. A frame is limited to 900,000 bytes.
The native bridge first sends `type: browser` and its resolved `applicationId`
on the pipe, followed by the browser's messages. The identity comes from the
launching browser's executable path, never from a page or the extension's label.
Other pipe clients send `list` or `command` directly.

| Message | Fields and behavior |
| --- | --- |
| `hello` | `version: 1`, random connection `epoch`, stored random `profileId`, browser label, `heartbeatIntervalMs: 5000` for browser liveness. |
| `heartbeat` | Current `epoch`; browser worker sends every five seconds independently of page timers. Wrong-epoch messages are ignored. |
| `ready` | Service-generated `connectionId`; every transport gets a fresh ID, including cloned profiles. |
| `snapshot` | `epoch`, strictly increasing `revision`, complete `sessions` array. Older/wrong-epoch snapshots are ignored. |
| `list` | Returns `connections`, including connected-but-empty profiles and each complete session list. |
| CLI `command` | Captured `connectionId`, `sessionId`, `operation`, optional typed JSON `value`. |
| Browser `command` | Captured `epoch` and `sessionId`, generated `requestId`, operation/value, absolute three-second `deadline`. |
| `result` | Correlated `requestId`, `status`, optional bounded detail. |

Raw player targets are `(connectionId, documentId, playerId)`. Frame IDs and tab
IDs come from Chrome's message sender, never page messages. A new connection for
the same browser identity and profile retires its previous connection and pending
commands before publishing the replacement. Commands use
`tabs.sendMessage(..., { documentId })`; same-frame navigation cannot redirect a
command to the replacement document. Moving a tab does not change its identity.
Navigation, tab removal, frame-port loss, and bridge disconnection withdraw the
corresponding sessions. BFCache pagehide closes the port; pageshow reconnects it.

The service admits at most 32 pending commands per connection. The extension
admits one playback command per player at a time and rejects overlap. Activation
is independent, so a stalled page command cannot prevent switching to its tab. This is not the
production media scheduler; document and element entries may still refer to the
same physical playback. Disconnects complete pending calls as `session-gone`;
deadline expiry returns `unconfirmed`. Late results are discarded. No commands
are persisted or replayed on reconnect, and the new connection ID fences old CLI
targets. The service also accepts a versioned `watch` request and pushes complete
version 2 catalogs on changes, with a transport heartbeat every two seconds.
Catalog connections include `applicationId`, `heartbeatAgeMs` (-1 until the first
valid browser heartbeat), and `hasSnapshot`. Age is measured with the relay's
monotonic clock and changes even when the catalog revision stays the same.
They also include `browserExecutable`, resolved from the native host's launching
process. The host supplies the browser product name instead of relying on the
extension's Chromium brand detection. Executable paths distinguish unknown
browser installations that share a profile ID; they never create Windows source
claims. The shared configuration page combines these connections with Windows
browser registrations and displays connection and source ownership separately.
Slow watchers coalesce
updates. Disconnecting a watcher removes its subscription.

Browser liveness is separate from the relay's watch heartbeat. Connections opting
in through `heartbeatIntervalMs: 5000` expire after 20 seconds without a valid
browser heartbeat, checked once per second. Page snapshots and the relay's own
watch traffic do not renew the lease. Expiry withdraws sessions,
finishes pending requests, and closes the bridge. Recovery creates a new connection
ID, and cached documents expose only activation until fresh page snapshots arrive.
Legacy extension connections without this field remain visible to the raw CLI
but cannot take ownership from Windows. Update the extension, native bridge,
relay, and backend together.

Catalogs retain every raw `session` and additionally expose `tabs` with the
selected `sessionId` and a monotonic binding `generation`. Primary selection
uses the popup's ranking policy. Backend commands include `tabId`, `generation`,
and an absolute deadline. The service checks the captured binding before admission
and again after waiting for its write lock. A primary that changes away and back
still has a new generation. Existing raw CLI commands remain supported.

## CLI diagnostics

With the service running, use `--list` to inspect raw browser/player targets:

```powershell
$companion = '.\artifacts\BrowserCompanion\host\JPSoftworks.MediaControlsExtension.BrowserCompanion.exe'
$catalog = (& $companion --list | ConvertFrom-Json)
$catalog.connections | Select-Object connectionId, browser, applicationId, sessions
```

Choose the intended connection and player from that catalog, then use
`& $companion --command '<connectionId>' '<sessionId>' play` (or `pause` or
`activate`). The returned capabilities are authoritative. The CLI also supports
`seek`, `volume`, `mute`, and `rate` with a following JSON value. Failed commands
return a nonzero exit code and a JSON status. Retired targets are never redirected.

## Validation

From the repository root:

```powershell
dotnet build .\src\BrowserCompanion\host\JPSoftworks.MediaControlsExtension.BrowserCompanion.csproj
node --test .\tests\BrowserCompanion\*.test.mjs
node .\tests\BrowserCompanion\fixture\server.mjs
```

For visual checks, run `node .\tests\BrowserCompanion\preview\server.mjs` and open
`http://127.0.0.1:8766`. This loads the actual popup with simulated data and
controls. Use `?state=empty`, `disconnected`, `sleeping`, `long`, or `many` to check
those layouts, and the browser's color-scheme emulation to check both themes.
The preview does not establish live media, native messaging, or foreground focus.

The transport test starts real service/bridge processes and sends framed test
browser traffic; it does not require native-host registration or a real browser.
Each run copies the built runtime and its test manifest into a temporary folder
under `artifacts/BrowserCompanion/tests`. Child processes receive a unique
`BROWSER_MEDIA_SPIKE_INSTANCE` GUID, which scopes both the pipe and service mutex.
The test can run while the normal service is active, or alongside another test
run, without touching their catalogs or manifests. Leave that variable unset for
normal browser use. Startup failures include the service's stderr diagnostics.
It covers colliding profile session IDs, byte-fragmented frames, revisions,
correlation, stale connection IDs, disconnect during a command, timeout, and
oversized frame rejection. It also checks that separate service instances can
coexist and that duplicate startup reports the single-instance error. Routing
tests cover moved/minimized/maximized windows,
navigation fencing, capabilities, values, and untrusted routing fields.
Script harnesses also execute the actual service worker and page hook, covering
document-targeted routing, navigation withdrawal, preview filtering, per-element
control, detached Audio pause/resume, and Media Session handler removal. These harnesses substitute
browser/DOM APIs; they do not establish real browser compatibility.

Live acceptance checklist using `http://127.0.0.1:8765`:

1. Open the fixture twice, in separate browser windows with identical tab titles.
   Start each HTML player once using its page controls. Check each tab has its own
   group. Expand the **players in this tab** list and confirm each HTML player,
   shadow-root player, frame, and document session appears. Pausing or muting a
   started player should keep it listed. Unplayed and muted autoplay previews on
   search results pages should stay absent.
2. Play/pause each through the **service CLI** and confirm only its player changes.
   Invoke Next on the document entry and verify its metadata changes.
3. Move one tab to another window. Activate its captured service target and verify
   both the selected tab and foreground window. Repeat with a minimized window.
4. Navigate, remove a player, close a frame/tab, and verify its sessions disappear.
   Saved old targets must fail instead of operating the replacement document.
5. Stop/restart the service; reconnect and verify old connection IDs fail. Repeat
   across Edge and Chrome profiles with identical tabs.
6. Check a real streaming site, a cross-origin embedded player, denied site access,
   navigation back/forward, a frozen background tab, and the autoplay failure path.

## Media Controls backend

[BrowserBackend](../../src/MediaControlsExtension.Media.Browser/BrowserBackend.cs)
implements `IMediaBackend` and is registered as **Browser companion**,
disabled by default. Enable it on Media Controls' **Media sources** page after
deploying a build containing this provider. One provider accepts all connected
browser profiles. The native bridge starts a hidden relay on demand after
validating the extension origin. Concurrent launches share a per-user mutex.
The connection attempt is bounded to five seconds; the extension's existing
reconnect loop can retry a failed start. The relay exits after thirty seconds
without any connected clients. Explicit `--service` mode stays running for
development and rejects duplicate startup. The app does not install the helper.

Follow the [setup and update guide](../user/browser-companion.md) when changing
any component of the companion.

The backend publishes one primary player per tab, with site names and favicons,
page details, supported playback operations, and exact tab activation. All raw
players remain accessible from the companion popup and CLI. Play, pause, next,
previous, and activation are mapped to existing media-core operations. Artwork
and source logos use the existing UI contracts. The CLI's seek/volume/mute/rate
controls are not added to the CmdPal UI.

### Sleeping and unresponsive tabs

On Chromium 132+ the worker observes the browser-owned `tabs.Tab.frozen` state.
A frozen tab keeps its last metadata, **Switch to application**, and **Play** if
the captured player supports it. Play uses the same activation path as Switch to
application: select the tab, restore its current window if minimized, and focus
that window. It then requests a correlated fresh page snapshot.
It checks the original document, player, and capability again before sending Play
once, within the original command deadline. Play on an awake tab does not activate
the browser window. Other playback controls return with fresh observations.
A timeout before dispatch does not leave a queued Play.

The desktop provider transfers Windows foreground permission to the relay, which
passes it to the selected connection's native host, then to its browser process.
Switch to application waits for activation to finish before returning Dismiss to
CmdPal, keeping the host invocation alive during the foreground handoff.
The CmdPal host must also renew `CoAllowSetForegroundWindow` for each command,
including repeated commands from the same extension. Caching this grant by active
extension is insufficient: user input and later foreground transfers expire it.
Named-pipe peers and the native host's launcher are resolved by Windows; page data
cannot choose a process ID. Only Play and Switch to application request this
handoff. If the browser reports that focus was refused, the extension reports
unavailable and sends no Play. Windows keyboard focus still needs a live check.

The desktop details show that the tab is sleeping. Switch to application checks
the current frame's browser document ID and lifecycle before selecting the tab
and again before focusing its current window; it never waits for page script
execution. It wakes the tab without sending a playback command.

On older browsers, or when page JavaScript hangs without a freeze notification,
a page command times out at its existing deadline, releases the worker's busy
slot, and leaves activation available while waiting for new observations. Tab
responsiveness does not affect browser ownership; only browser heartbeat
health controls the delayed Windows fallback.
Background timer throttling by itself is not treated as a lost player.

Discarding unloads the document, so its media targets are retired. A later reload
creates fresh targets; old commands cannot wake and control a replacement player.
If the entire browser stops responding, the browser lease described above removes
its cached entries even when the separate relay is still running.

For a live check, pause media and use `edge://discards` or `chrome://discards` to
freeze the tab. Verify that Play restores/focuses the correct browser window,
wakes the tab, and plays the same media. Check that
browser ownership remains stable while an individual tab is sleeping or
unresponsive. Test discarding separately: its old target must disappear. These
browser/OS checks are additional to the automated worker and pipe tests.

For a foreground regression check, open CmdPal from another application, enter
Media Controls, change the selection, then invoke Switch to application. Press
Ctrl+L without clicking the browser. Repeat for media in a second browser window,
including a minimized window, and repeat commands within the same extension.

After changing the extension, use its Reload button on `edge://extensions` or
`chrome://extensions`, then refresh the media tabs to load the page scripts.
Rebuilding Media Controls or choosing Reconnect to desktop service does not reload the
browser extension's code. The foreground permission handoff
requires updated Media Controls, relay, and native-host binaries: stop the relay,
reload the extension to release the old native host, rebuild/register the host,
restart the relay, and deploy Media Controls. Then reload the extension and media
tabs. Updating only the extension cannot supply Windows foreground permission.

For local foreground diagnostics, create `foreground-trace.enabled` beside the
registered native host. `foreground-trace.log` records permission handoffs and the
Windows foreground owner for each command/result. Remove the marker to stop
tracing. The browser's `focused` flag alone does not prove Windows keyboard focus.

### Artwork and logos

Artwork comes from `MediaMetadata.artwork`; individual video players use their
poster, or page artwork when there is only one HTML player in the document. The
website favicon comes from browser-owned tab metadata. If that image is absent or
cannot be loaded, the source logo falls back to a bundled generic browser icon.

The backend downloads HTTP(S) image URLs without browser cookies or credentials.
Downloads have a three-second deadline, bounded redirects, and limits of 2 MiB
per cover and 256 KiB per favicon. PNG, JPEG, GIF, WebP, and AVIF are recognized by
their encoded signatures; icons also accept ICO. AVIF rendering requires a Windows
image codec that supports it. File, data, blob, URLs with embedded usernames/passwords,
and SVG images are omitted. Covers are
versioned across track changes and replacement bindings; late results are discarded.
Up to eight covers are cached in memory. Favicon paths point to private temporary
files, limited to 128 distinct files per provider lifetime and cleaned up on normal
disposal. Failed cover downloads become eligible for another UI request after
30 seconds. Playback uses a separate transport and does not wait for images.

Follow the installation update steps above, including the native bridge and
relay update, then refresh media tabs so the page hook runs. The local fixture
has a site icon and alternating covers: use Next to verify cover changes, then
clear its Media Session metadata to check withdrawal.

### Windows media session fallback

Keep one Windows media session backend enabled for automatic fallback. Enabling
the companion saves a preference; its current effective source is reported in
Media sources and can change without rewriting that preference.

- The extension worker sends a generic heartbeat every 5 seconds, independent
  of playback, tab state, and page timers. A connected relay alone is insufficient.
- Startup keeps Windows sessions visible until a complete catalog (including an
  empty one) and 10 seconds of continuous browser heartbeat evidence are available.
- A disconnect or 20 seconds without a browser heartbeat withdraws unusable tab
  bindings. Media sources reports reconnecting and keeps Windows excluded briefly.
- At 60 seconds total since the last valid browser heartbeat, that browser's
  source claims are released and Windows sessions are rediscovered. The diagnostic
  reports that Windows media sessions are being used because the companion is
  unavailable. Edge, Chrome, and Firefox fall back independently.
- Recovery after fallback again requires a full catalog and 10 stable seconds.
  The composite commits browser rows and exclusions together. Existing commands
  are never redirected or replayed, and retired targets/artwork cannot revive.
- A long desktop scheduling pause, including sleep, withdraws cached targets and
  starts a fresh 60-second grace period instead of treating sleep as an outage.

Claims target exact application identities in both GSMTC hosting modes. The
initial mapping recognizes standard Edge (`MSEdge`), Edge Dev (`MSEdgeDev`), and
Chrome (`Chrome`) installation paths. Firefox resolves the exact executable
directory through `Software\Mozilla\Firefox\TaskBarIDs` in HKLM, then HKCU, and
accepts only the registered 16-hex-digit identity. Other channels, portable
installations, and unknown identities stay on Windows; their raw companion players
remain available in the popup and CLI. Executable-name aliases and profile/PWA
identities are not guessed. Windows global controls are unaffected.

Profiles sharing an application identity switch as a group. One healthy profile
keeps companion ownership; missing profiles are reported as degraded. Consequently
every profile used for media in that browser should have the companion installed.
Windows cannot supply fallback for just one profile in that shared identity.
Sleeping tabs, paused media, and an empty player list do not cause fallback.

There is no metadata-based deduplication. Two tabs playing identical tracks remain
two tabs. Unrelated Windows application identities remain discoverable throughout.
For live acceptance, check Edge, Chrome, and Firefox separately, loss of one versus
all profiles, bridge/relay restarts, sleep/resume, and recovery while pause-others
is enabled. Check custom Firefox taskbar grouping and channels separately. Verify
the reported native application IDs on the tested installations.

Relay transport silence for ten seconds withdraws all companion sessions and
starts reconnection while retaining the browser fallback grace. Reconnection
never replays a command or reuses a retired backend ID.
A primary change within an existing tab retains its list identity and replaces
its command binding. The service independently checks the binding even if the
backend has not received the latest catalog yet.

Run backend regressions from the repository root:

```powershell
dotnet test .\tests\MediaControlsExtension.Media.Tests\JPSoftworks.MediaControlsExtension.Media.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~Browser|FullyQualifiedName~MediaSourcePolicyTests"
```

These tests use isolated named pipes and cover profile/tab collisions, captured
commands, replacement primaries, metadata-only updates, reconnects, malformed
catalogs, cancellation, and uncertain outcomes. The Node transport test runs the
real relay and native bridge, including watch updates, grouping parity with the
popup, and stale tab generations. Actual CmdPal/browser acceptance still requires
the live checks above. Images that require browser authentication, blob URLs, or
SVG decoding are not supported.

## Release bundle

Run `eng/Build-BrowserCompanion.ps1` to create a versioned x64 ZIP under
`artifacts/BrowserCompanion/release`. `-Runtime win-arm64` selects ARM64; validate
that executable on ARM64 before distributing it. Native AOT publishing requires
the Windows C++ build tools. The script copies only the extension runtime files,
the self-contained executable, setup scripts, docs, license, and runtime notices.
It writes a SHA-256 sidecar and leaves debugging symbols outside the bundle.

The bundle uses `Setup.ps1`; source checkouts use `eng/Register-BrowserCompanion.ps1`.
Both share the same registration module and preserve existing allowed extension
IDs for that copy. Removal only deletes registry values pointing to its manifest.
Setup migrates owned legacy native-host registrations; the pipe name remains stable.

Run `tests/BrowserCompanion/registration.test.ps1` for isolated registry checks.
Set `BROWSER_COMPANION_TEST_HOST` to the bundle's `host` directory before running
the Node suite to validate the actual AOT binary. The native tests cover concurrent
automatic startup, rejection of unregistered origins, prompt bridge disconnect,
idle shutdown, and the existing routing/heartbeat contract.

The browser companion is opt-in. The popup is English; the desktop provider and
fallback messages use all supported app locales.

Automated tests do not establish live browser identity, physical foreground
activation, or suspend/resume behavior. Validate those on the deployed build.

## API references

- [Chrome Native Messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging)
- [Edge Native Messaging registration](https://learn.microsoft.com/en-us/microsoft-edge/extensions/developer-guide/native-messaging)
- [Content script worlds and frames](https://developer.chrome.com/docs/extensions/reference/manifest/content-scripts)
- [Tabs and document-targeted messages](https://developer.chrome.com/docs/extensions/reference/api/tabs)
- [Page lifecycle, freezing, and discarding](https://developer.chrome.com/docs/web-platform/page-lifecycle-api)
- [Window focus and state](https://developer.chrome.com/docs/extensions/reference/api/windows)
- [Service worker lifecycle and native ports](https://developer.chrome.com/docs/extensions/develop/concepts/service-workers/lifecycle)
- [Media Session specification](https://www.w3.org/TR/mediasession/)
