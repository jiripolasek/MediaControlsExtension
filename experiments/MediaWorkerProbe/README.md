# GSMTC worker proof of concept

This experiment has its own executable, MSIX package, and solution. The production
extension does not reference or package it, and the backend registry contains only
the original direct GSMTC provider.

The probe acquires a GSMTC manager, reports process and package identity plus the
session count over a named pipe, and stays alive for the restart test. It is not
an `IMediaBackend` implementation. Session updates, playback commands, artwork,
and a persistent IPC protocol remain future work.

## Build

Open `MediaWorkerProbe.slnx` in Visual Studio, or run this from a Visual Studio
developer PowerShell at the repository root:

```powershell
msbuild .\experiments\MediaWorkerProbe\MediaWorkerProbe.slnx -restore -p:Configuration=Release -p:Platform=x64
```

Release builds use NativeAOT. Use `-p:Platform=ARM64` to cross-build ARM64. The
experimental package output goes to `artifacts/MediaWorkerProbe/`. Building this
solution does not build, stop, or deploy the production extension. The package
links the existing artwork assets without referencing the production executable.

To publish just the probe executable:

```powershell
dotnet publish .\experiments\MediaWorkerProbe\Worker\JPSoftworks.MediaControlsExtension.MediaWorkerProbe.csproj -c Release -r win-x64 -p:Platform=x64
```

## Packaged recovery check

Install the normal Media Controls extension and deploy the experimental package
from Visual Studio. They have separate package identities and COM class IDs, so
the probe can coexist with the production extension and older probe builds.

Run from the repository root:

```powershell
.\experiments\MediaWorkerProbe\Test-PackagedMediaWorker.ps1
```

The harness holds the normal extension, activates the probe using an `IUnknown`
lease, checks its kernel-reported process identity against the experimental
package, and verifies GSMTC acquisition. It terminates that verified worker and
repeats activation while requiring the normal extension to survive. It cleans up
the worker and any normal extension process it started.

The worker package is `JiriPolasek.MediaControlsWorkerProbe`, its COM class ID is
`f47a961b-a78e-4523-9c9e-ef9bf5e88706`, and its activation argument is
`-RegisterProcessAsComServer`. There is no worker switch in the production
executable and no worker option in media-source settings.
