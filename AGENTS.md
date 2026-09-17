# Agent instructions

- Follow `.editorconfig`; preserve line endings and final newlines. Keep comments brief.
- Preserve unrelated and staged changes. Commit only when requested.
- Finish with `git diff --check`.

Use these scripts directly from the repo root in PowerShell on Windows:

| Task | Command |
| --- | --- |
| Build and deploy AOT | `.\eng\Deploy-Package.ps1 -Aot` |
| Deploy AOT and reload Command Palette | `.\eng\Test-Package.ps1 -Aot -AfterDeploy Reload` |
| Deployment regression checks | `.\tests\Deploy-Package.Tests.ps1` |
| Worker publish regression checks | `.\tests\MediaWorker.Publish.Tests.ps1` |

Deployment updates the local package and defaults to `Release|x64`. Omit `-Aot`
for managed builds; use `-AfterDeploy Restart` to restart the host.
Packaging requires .NET 10 and Visual Studio 2026 with DesktopBridge and Windows SDK tools.
See [docs/dev/KB.md](docs/dev/KB.md) for options and troubleshooting.
