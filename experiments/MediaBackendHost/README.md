# Media backend host spike (superseded)

The reusable host now lives in `src/MediaControlsExtension.Media.Hosting` and the
GSMTC executable in `src/MediaControlsExtension.MediaHost`. Exclusivity is enforced
by the production registry/composite/settings. The duplicate spike implementation
has been removed.

Current tests and instructions are in the [hosting test project](../../tests/MediaControlsExtension.Media.Hosting.Tests/README.md).

`Test-Spike.ps1` forwards to the production-host test runner. `Test-Package.ps1`
still creates an isolated package named `JPSoftworks.MediaBackendHostSpike` for
packaged subprocess tests, using the production host library and test fixture.
It unregisters that package afterward unless `-KeepRegistered` is supplied.
It does not deploy the Media Controls extension.

```powershell
./experiments/MediaBackendHost/Test-Spike.ps1 -NativeAot
./experiments/MediaBackendHost/Test-Package.ps1
```

This experiment remains separate from the earlier `MediaWorkerProbe` activation
probe. The production worker uses direct child launch with job assignment at
creation; it is not a COM activation server.
