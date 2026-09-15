# Production package command checks

This opt-in COM client activates the installed production extension and invokes
the actual Media sources commands. It checks both switch directions, both rows'
enablement, page notifications, and disposal of the actual provider while its
extension process remains alive and callable. It does not automate the WinUI view.

The command checks also exercise dummy media. Build the package in Debug or pass
`-p:EnableDummyBackend=true` when building a Release package for these checks.

Run through the package validation script so the current CmdPal channel and the
original GSMTC settings are restored even if the check fails:

```powershell
./eng/Test-MediaWorkerPackage.ps1 -NativeAot -RestartHost -Checks Commands
```

Omit `-NativeAot` for a managed deployment. The default `-Checks All` also runs
CmdPal activation, crash recovery, and owner-process termination checks. Reports
are written under `artifacts/MediaBackendHost/production-<mode>-<architecture>`.

The package script treats missing extension settings as empty, creates
`LocalState` when needed, and removes the test settings file during cleanup if
none existed originally. Existing files retain unrelated settings while the
original provider flags and their key presence are restored.

The isolated settings checks load the script's settings functions and replace
host refresh with a test callback. They use temporary directories under the
repository artifacts directory and do not activate the installed extension:

```powershell
./tests/MediaControlsExtension.Package.Tests/Test-Settings.ps1
```
