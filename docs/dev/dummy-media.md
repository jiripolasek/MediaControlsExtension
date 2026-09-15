# Dummy media backend

The dummy backend is a simulator for development, testing, and backend integration.
It is available in the app only in Debug builds or builds made with
`-p:EnableDummyBackend=true`, which defines `FF_ENABLE_DUMMY_BACKEND`.
Standard Release builds omit its app registration and Media sources entry.
The worker still includes the dummy backend factory.

Open **Media sources** from the Media Controls command menu and enable **Dummy
media** to add three simulated players. It is disabled by default and plays no
audio. Windows media and other enabled providers can remain active alongside it.

Each player has twelve randomly generated tracks, including titles, artists,
albums, genres, durations and small colored artwork. The first player starts
playing; the other two start paused. Playing sessions advance to the next track
after 15-45 seconds. Next and previous move through
the same playlist, wrapping at either end. Pausing freezes the position; stopping
resets it. Shuffle, repeat and opening a source application are not supported.

Dummy sessions participate in the normal media controls, including the configured
pause-other-sessions behavior. They do not register Windows media sessions or
replace real applications. Disabling the source removes its sessions and stops
its worker. Enabling it again creates new random playlists.

The simulator runs in its own process using the same worker executable and owner
lifetime as Windows media. It holds three fixed-size playlists, at most one small
artwork image per player, and one timer. It does not keep a growing track history.
It publishes playback updates on commands and track changes. Timeline positions
include their sample timestamp, so consumers can advance a displayed position
locally without receiving a full snapshot every second.

The implementation is also a backend integration example. See
[adding a worker backend](media-backend-hosting.md#adding-a-worker-backend).
