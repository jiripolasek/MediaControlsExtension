# Media hosting subprocess tests

This executable tests the production hosting library with compiled synthetic
factories and the production GSMTC/dummy catalog. It also publishes a controlled
Windows media session for native GSMTC checks. Fault-injection factories and
process controls are not shipped in the extension; the opt-in dummy provider is.

Run from the repository root with .NET 10 and the Windows SDK:

```powershell
./tests/MediaControlsExtension.Media.Hosting.Tests/Run-Tests.ps1
./tests/MediaControlsExtension.Media.Hosting.Tests/Run-Tests.ps1 -NativeAot
./eng/Test-MediaWorkerPackage.ps1 -NativeAot -RestartHost
./tests/MediaControlsExtension.Media.Hosting.Tests/Run-NativeChecks.ps1 -NativeAot -Packaged
```

`Run-Tests.ps1` exercises real pipes/processes, cancellation, invalid payloads,
queue bounds, owner death/disposal, recovery, artwork, policies and callbacks.
Exclusive selection is covered separately by the media-core tests.
The worker application checks use the production startup code with two compiled
test factories. They cover invalid arguments/unknown IDs, construction only after
a validated handshake, logging and activation context, and owner-scope shutdown.
Dummy checks cover seeded bounded playlists, timer progress, paused/stopped
positions, previous/next, artwork version fencing, stale commands and cleanup.
They also run the production dummy factory through a real worker, recover it
while a different backend remains usable, and dispose both through their owner.
Use `-OutputDirectory <directory>` for a separate executable while a native soak
is using the default output directory.

`Test-MediaWorkerPackage.ps1` checks the deployed production payload and CmdPal
activation, recovery and lifetime, including the actual COM page commands and
provider disposal while its extension process remains alive. It temporarily
switches the two GSMTC flags and the dummy flag, and restores their previous values.
It verifies three dummy media rows alongside GSMTC, independent disablement, and
disposal of both workers through the actual extension provider.
`-RestartHost` leaves the external-reload
preference unchanged and restarts the current CmdPal channel. Use `-Checks Lifetime`
for external reload without that switch; CmdPal must allow external reload.
`-PayloadOnly -Platform ARM64 -PackageFile <msix>` checks a built ARM64 package
without executing it.

The published fixture accepts `--dummy-check <results-directory> <worker-path>`
to exercise the same dummy controls and graceful shutdown against a separately
published production worker. Launch it with `Invoke-CommandInDesktopPackage` to
validate the installed package identity, then read `dummy-result.txt`.

`Run-NativeChecks.ps1` uses only its own media application. It checks internal and
hosted play/pause/next/previous/artwork and owner activation routing, runs 100 worker
lifetimes, then keeps a worker running with periodic media-application exit/relaunch
for two hours. Activation verifies callback routing and values without focusing a window.
Use `-DurationMinutes 0 -LifetimeCycles 2` for a smoke test. `-Packaged` launches
asynchronously under the installed production package; inspect the printed
results directory. Closing/killing the test owner terminates its test processes.
`-SampleIntervalSeconds 1` increases controlled churn; the default is 15 seconds.
Artwork retries use fresh versioned keys and are recorded in `artwork-retries.txt`.
`owner-lifecycle.txt` records launch, disconnect, shutdown and exit diagnostics.
`native-primary-failure.txt` and `native-cleanup-failure.txt` preserve both failures
when cleanup also fails; `native-result.txt` includes both exceptions. Each diagnostic
log has a 4 MiB soft limit independent of verbose worker logs.
Sleep/resume and source-window activation require separate interactive checks.

The published fixture also accepts `--memory-check <results-directory> <cycles>`
under the production package identity. It churns a controlled GSMTC source,
records GC configuration and memory counters, forces a full collection, trims
its working set, and verifies playback/artwork/activation afterward. The normal
idle maintenance policy is active with the worker's 16 MiB threshold; any automatic
trim is recorded in `owner-lifecycle.txt`. The CSV separates private committed
bytes, total working set, managed live estimates and GC heap commitment; it does
not measure private working set. The forced collection is test-only.

For example, after publishing the native fixture:

```powershell
$probe = (Resolve-Path artifacts/MediaBackendHost/native-x64/JPSoftworks.MediaControlsExtension.Media.Hosting.Tests.exe).Path
$results = Join-Path $PWD 'artifacts/MediaBackendHost/memory-trim-results'
Invoke-CommandInDesktopPackage -PackageFamilyName JiriPolasek.MediaControlsForCmdPal_15b1mzc2p8w76 -AppId App -Command $probe -Args ('--memory-check "{0}" 300' -f $results) -PreventBreakaway
```

The packaged command returns before the probe finishes. Inspect `memory-result.txt`
and `memory.csv` in that directory. Run only one controlled native fixture at a
time. `-p:MediaGcConserveMemory=0` on `dotnet publish` disables GC conservation
for comparisons; it does not disable idle trimming.

The controlled SMTC publisher follows Microsoft's
[manual transport controls contract](https://learn.microsoft.com/en-us/windows/apps/develop/media-playback/system-media-transport-controls).
It publishes controls and metadata without playing audible audio.
