# Browser companion

Connect browser tabs to Media Controls for Command Palette, or control them
directly from the extension popup. The extension and native host are maintained
here. Media Controls includes the desktop bridge and setup/removal actions. The
bridge starts when the browser connects. A portable bundle remains available for
development.

- [Setup and use](../../docs/user/browser-companion.md)
- [Architecture, protocol, and validation](../../docs/dev/browser-companion.md)

From the repository root:

```powershell
dotnet build .\src\BrowserCompanion\host\JPSoftworks.MediaControlsExtension.BrowserCompanion.csproj
node --test .\tests\BrowserCompanion\*.test.mjs
node .\tests\BrowserCompanion\preview\server.mjs
.\tests\BrowserCompanion\registration.test.ps1
.\tests\BrowserCompanion\packaged-registration.test.ps1
.\tests\BrowserCompanion.Publish.Tests.ps1
.\eng\Build-BrowserCompanion.ps1
```

The preview serves the real popup with simulated browser data at
`http://127.0.0.1:8766`. It does not connect to your browser tabs or native host.
