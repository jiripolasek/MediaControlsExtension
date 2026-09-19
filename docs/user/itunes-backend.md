# iTunes media source

The `itunes` media source controls the classic desktop iTunes application on this
computer through its COM automation interface. It is enabled by default and runs
alongside the other media sources. It does not control the newer Apple Music app.

## Use it

1. Install and open desktop iTunes.
2. Open the Media Controls command menu and choose Media sources.
3. Confirm that iTunes is enabled. It is enabled by default on a new installation.
4. Start playback or select a track in iTunes. The player appears in Media Controls.

The provider does not launch iTunes. While the application is closed, it remains
disconnected with no media session and reconnects after iTunes starts. Disabling
the provider releases its COM connection without closing iTunes or changing playback.

## Behavior and boundaries

- Supports play, pause, stop, previous, next, shuffle, repeat, title, artist, album,
  genre, track number, timeline, and artwork. Repeat cycles through off, all, one,
  and off.
- Uses iTunes events for playback and metadata updates. A lightweight discovery
  check runs only while iTunes is disconnected; once connected, the worker watches
  the iTunes process for exit instead of repeatedly polling COM.
- Exports the current artwork through a temporary file created by iTunes, reads it
  into memory, and removes the file immediately. One image is cached at a time and
  track changes invalidate the old artwork key.
- Runs in a dedicated MediaHost worker process. Within that worker, all iTunes COM
  access stays on its own dispatcher thread. The COM calls and event sink use direct,
  Native AOT-compatible interop rather than runtime-generated COM wrappers. A native
  crash or an unresponsive COM call can therefore be terminated and recovered without
  taking down the extension process.
- Reports `Apple.iTunes` for Apple's direct installer and the packaged Store AUMID
  `AppleInc.iTunes_nzyj5cx40ttqa!iTunes` when the discovered executable is under
  `Program Files\WindowsApps`. The discovered `iTunes.exe` path is retained when
  available for normal player identity and application-switching behavior.
- While enabled, claims the direct-install identities `Apple.iTunes` and `iTunes.exe`,
  the Store AUMID `AppleInc.iTunes_nzyj5cx40ttqa!iTunes`, and the two Media Controller
  Helper identities (`49586DaveAntoine.MediaControllerforiTunes_9bzempp7dntjg` and
  `49586DaveAntoine.MediaControllerforiTunes_9bzempp7dntjg!App`) so those integrations
  do not create a duplicate player. The claims remain active during a temporary COM
  disconnection. Disable the iTunes source to use a third-party iTunes SMTC integration
  instead.
- Controls only the local iTunes instance registered with Windows COM. Remote
  libraries, playlist browsing, seeking, and application-specific volume are not
  exposed by this provider.

## Manual validation

Start the extension before and after iTunes, then verify that the source connects in
both orders. Check playback, stop, previous/next, shuffle, the complete repeat cycle,
metadata, timeline, and tracks with different or missing artwork. Close and reopen
iTunes and confirm that the old session disappears and a fresh session replaces it.

If an iTunes SMTC plugin is installed, verify that only the direct iTunes session is
shown while this provider is enabled. Disable the provider and confirm that the GSMTC
session returns, then re-enable it and confirm that the duplicate is retired.
