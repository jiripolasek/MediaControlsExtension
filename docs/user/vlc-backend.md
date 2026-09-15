# VLC media source

The optional `vlc` registration connects to one manually configured VLC 3 player
on this computer or another computer. It is disabled by default and runs alongside GSMTC.
`MediaControlsExtension.Media.Vlc` owns the HTTP transport and depends only on the
media contracts. The extension owns settings and credential storage.

## Enable it

1. In VLC, open Tools > Preferences and select All under Show settings.
2. Under Interface > Main interfaces, enable Web.
3. Under Main interfaces > Lua, set the Lua HTTP password.
4. Under Input / Codecs > Network settings, set the HTTP server port to `8080`
   (or another port). For local use, bind the HTTP server address to `127.0.0.1`.
   For remote use, bind to the server's LAN address and allow this computer through
   the server firewall and VLC's access controls.
5. Save, fully exit VLC, and reopen it.
6. Open the Media Controls command menu and choose Media sources. Open VLC media
   player to configure it, enter the server URL and password, and save. Examples:
   `http://127.0.0.1:8080`, `http://living-room:8080`, or `http://192.168.1.10:8080`.
7. Use Test saved connection from the configuration page's command menu to verify
   setup. It reads VLC status without enabling the provider or changing playback.
8. Return to Media sources and choose Enable from VLC's command menu.
9. Play or queue media in VLC. Its player appears alongside the GSMTC sessions.

Configuration is available while disabled; saving it does not enable the provider.
Connection settings apply while enabled. General Settings contains shared
preferences and a text hint pointing to Media sources. The password is stored in Windows
Credential Locker under `JPSoftworks.MediaControlsExtension.Vlc`, keyed by the
normalized server URL. Existing port settings and the legacy `http` credential
migrate to their original loopback endpoint. The form always starts with an empty
password field; leaving it blank uses the saved credential for the selected URL.
A new endpoint never receives another endpoint's password. Switching back restores
that endpoint's saved password. Passwords are not written to `settings.json`,
session identities, or diagnostic messages.
If Windows Credential Locker cannot read the password, the configuration page displays an error
and lets you save it again. A failed replacement displays a notification and keeps
the previous password; other settings remain usable.

The Media sources page reports the endpoint, connection and authentication failures. A running
VLC with an empty playlist is connected with zero sessions. A stopped player with
queued media remains available for Play. Disabling the provider closes its HTTP
client and stops polling; it does not exit VLC or change playback.

## Behavior and boundaries

- Treat this session as local offers Automatic, Yes, and No. Automatic treats
  explicit loopback URLs (`127.x.x.x`, `localhost`, or `[::1]`) as local; other URLs
  are remote. Yes and No override playback behavior. Remote treatment moves VLC to
  Remote sessions and adds a Remote marker to its player label, including current
  playback and dock presentation. It can still be controlled directly, but is
  excluded from automatic selection and next/previous player switching.
- Automatic pausing includes only sessions treated as local by default. The shared
  Include remote sessions when pausing other players option includes remote sessions
  as both initiators and targets, while the existing pause-others option remains
  the master switch.
- Treatment controls behavior and does not establish native source identity.
  Only loopback endpoints replace local VLC's GSMTC sessions, regardless of treatment.
  A remote endpoint treated as local still leaves local GSMTC sessions visible.
  LAN addresses and aliases are conservatively remote even if they resolve to this
  computer; use a loopback URL when local source replacement is intended.
  One connection is configured at a time; multiple named connections are future work.
- Polls once per second. Commands and fresh observations share one provider lane.
- Supports metadata, timeline, play, pause, stop, next, previous, shuffle, repeat,
  and artwork. Repeat cycles through off, current track, entire playlist, and off.
  Mode commands read the latest VLC state before changing it, including changes
  made in VLC itself. Source activation, seek, application volume, and playlist
  browsing are not exposed by this provider.
- Loads available covers from the configured server's `/art?item=...` endpoint, using a separate
  HTTP client so cover downloads do not block playback commands. The extension
  does not open the file paths or remote URLs in VLC's artwork metadata. One image
  is cached at a time; track changes, settings changes, and reconnections invalidate
  old artwork keys. A missing cover falls back to the application icon and can
  retry after ten seconds without making the player unavailable.
- Uses a bundled cone icon for source tags and list items, independently of track
  artwork or a local VLC installation. Only loopback sessions report the native
  application identity `vlc.exe`.
- Uses explicit `pl_forcepause` and `pl_forceresume`; a secondary pause cannot
  toggle an already paused player back to playing. Commands are never retried.
- Control requests use HTTP/1.1 and an explicit empty body. This prevents the
  [.NET HTTP handler's automatic replay after an empty response](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/HttpConnection.cs#L581),
  which is unsafe for VLC's commands expressed as GET requests. A loopback socket
  regression test covers the actual HTTP handler, not just mocked responses.
- One-second polling resumes after connection, authentication, and response
  failures. Each HTTP request has a three-second timeout, including image transfer.
  JSON responses are limited to 1 MiB and encoded artwork to 32 MiB. PNG, JPEG, GIF,
  BMP, and WebP signatures are recognized; other responses use the icon fallback.
- Settings changes and observed disconnects withdraw the old session. Reconnection
  produces a fresh binding, and commands captured against the old binding fail.
- The HTTP API has no server instance token or atomic command preconditions. A VLC
  restart that completes between requests may be indistinguishable from the old
  instance. External track changes may also race a command. The provider therefore
  represents the configured player, not a particular document or playlist item.
- Accepts HTTP and HTTPS server roots, with hostnames, IPv4, or bracketed IPv6.
  Paths, queries, fragments, embedded credentials, and invalid ports are rejected.
  Redirects and proxies are disabled; HTTPS uses normal certificate validation.
  HTTP Basic authentication is unencrypted over HTTP, so use it on trusted networks.
  HTTPS requires a compatible server or gateway; the provider does not configure it.
- While enabled with a loopback endpoint, VLC claims the exact GSMTC application ID `vlc.exe`,
  verified with the desktop `vlc-win10smtc` plugin. GSMTC discovery and commands
  exclude that source, including while the HTTP connection is unavailable.
  Disabling the VLC provider or switching to a remote endpoint restores GSMTC
  discovery with fresh session bindings. Switching back to loopback immediately
  retires duplicate routes before applying the native exclusion policy.
  Unrelated applications and the separate VLC UWP app are unaffected. This claim
  covers every session with that ID; multiple desktop VLC instances cannot be
  distinguished through this application-level policy.

## Localization

Settings, connection-test results, and remote-session labels use the application's
`src/MediaControlsExtension/Resources/Strings.resx`. Connection and protocol diagnostics
belong to `src/MediaControlsExtension.Media.Vlc/Resources/Strings.resx`, so the provider
can be used without the Command Palette UI. Add translations as `Strings.<culture>.resx`
alongside each neutral resource file. Both use the current UI culture and fall back
to English for missing translations. Keep resource keys, protocol names, setting values,
and connection identities unchanged; translate only display text. Resource comments
explain the distinction between physical location and local playback behavior.

## Manual validation

With VLC and a GSMTC player such as Spotify, play each through Media Controls and
verify that the other pauses. Enable Show toast messages, then make VLC unreachable
while it is playing and switch to the other player before the next poll: a failed
secondary pause should leave the primary Play successful and produce a warning.

Also check disabling and re-enabling VLC, restarting VLC, replacing a wrong
password while enabled, changing the server URL, and an empty versus stopped playlist.
Check shuffle and the complete repeat cycle, including a mode changed in VLC
between extension commands. With Show media thumbnails enabled, switch between
tracks with different covers and a track with no cover; check the list and details
page and confirm the VLC source tag retains the application icon. A slow or missing
cover should not delay playback commands or disconnect the player.
With the desktop SMTC plugin enabled, check that only the direct VLC session is
shown while the VLC provider is enabled, that its GSMTC duplicate stays excluded
during HTTP connection loss, and that disabling the provider restores GSMTC VLC.
With a remote URL, verify that local VLC remains visible through GSMTC. Check
Automatic, Yes, and No treatment, plus both directions of the shared remote-pause
option. Changing URLs while a command or artwork request is pending must not send
that command to the new server or show the old artwork. A new server with a blank
password should request a password, and switching back should reuse its own saved
credential. Verify remote transport against a second computer when available.

The automated tests cover endpoint validation, settings migration, credential
isolation, treatment, HTTP response handling, mode controls, stale bindings and
artwork, image limits, cancellation, disposal, dynamic source policies, and composite
routing. Packaged UI rendering and real GSMTC/VLC switching require host validation.

Protocol references: [VideoLAN HTTP API commands](https://github.com/videolan/vlc/blob/3.0.x/share/lua/http/requests/README.txt)
and [status/command implementation](https://github.com/videolan/vlc/blob/3.0.x/share/lua/intf/modules/httprequests.lua),
plus the [local artwork endpoint](https://github.com/videolan/vlc/blob/3.0.x/share/lua/intf/http.lua).
