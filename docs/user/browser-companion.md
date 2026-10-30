# Browser companion

The browser companion adds individual browser tabs to Media Controls, including
exact tab activation and site-provided playback controls. Its popup also controls
media directly in the browser. The optional desktop companion starts automatically
when the browser connects. It does not require a sign-in or cloud service.

## Set up

Use Edge or Chrome 119+, or Firefox 153+, on Windows. The Media Controls build must include the
**Browser companion** source. The desktop bridge is included in the Media Controls
package; no separate desktop download or .NET installation is needed. Listings in
the Chrome Web Store, Microsoft Edge Add-ons, and Firefox Add-ons are planned but
are not available yet.

Firefox 154 has been tested. See **Firefox preview** below for temporary add-on
installation. A signed Firefox add-on is not available yet.

1. Open `edge://extensions` or `chrome://extensions`, turn on **Developer mode**,
   and use **Load unpacked** to load `src/BrowserCompanion/extension` from this
   checkout (or the `extension` folder in a companion ZIP). Copy its extension ID.
2. In Media Controls, open **Media sources**, select **Browser companion**, and
   open **Configure > Advanced**. Under **Developer setup**, paste the extension ID
   and choose **Save developer setup**.
   Multiple IDs can be separated by commas, including IDs from both browsers.
   Setup registers Chrome and Edge for your Windows account and enables the source.
   No administrator access, scheduled task, or Windows service is required.
3. Reload the extension and existing media tabs, then open its popup. It should show
   **Desktop service connected**. Use **Reconnect to desktop service** if needed.
4. Keep **Windows media** enabled for automatic fallback.

The main **Enable browser integration** button reuses your saved setup. Once the
store versions are available, their IDs will be included in Media Controls and
normal setup will not require entering an ID. **Get the extension** opens this
guide while the store listings are pending.

After setup, step 2 shows **Browser integration enabled** instead of the Enable
button. This confirms that the source is enabled and the desktop registration is
present; check the browser popup for the live connection. Disabling the source or
removing integration makes Enable available again.

The popup's connection status confirms the local service connection. Enablement
and fallback status in Command Palette are shown separately on **Media sources**.
The shared **Configure** page lists installed browsers and connected companions,
including other Chromium browsers such as Brave. Each browser shows its connection
status and current media source. Profiles of the same installation share a row;
different installations and channels remain separate. A missing connection can
mean the browser is closed or its extension is unavailable. During the fallback
grace period the page reports that it is waiting for companion recovery. It also
reports when Windows media is disabled, starting, or unavailable.
Setup actions, including **Get the extension** and **Advanced**, appear above the
browser status section. Connected and reconnecting browsers appear in compact
columns; expand **Other browsers** to inspect browsers without a connection.
The popup can still control tabs when the desktop service is disconnected.
Choose **Open Media Controls** in the popup to open its main page in Command
Palette. From there, open **Media sources > Browser companion > Configure** for
desktop setup. Command Palette must be installed and support command links; your
browser and Command Palette may ask you to allow opening the link. If it does not
open, launch Command Palette manually and select Media Controls.

The desktop relay starts in the background on connection and exits after thirty
seconds without any browser or desktop connections. It starts again when needed.
The browser popup is currently in English; desktop source messages follow the
Media Controls language.

The unpacked browser extension must remain in the same folder. The desktop bridge
updates with Media Controls. Portable desktop setup remains available for
development; see **Build from source** below.

If upgrading from a build that used the old JPSoftworks browser identity, run
**Configure > Enable browser integration** again and reload the Chromium extension.
Firefox builds now use `media-controls-companion@jiripolasek.com`: remove the old
temporary add-on and load the newly generated Firefox build. Setup migrates only
legacy registrations owned by this installation.

### Firefox preview

Build a Firefox extension directory from this checkout:

```powershell
.\eng\Build-BrowserExtension.ps1 -Browser Firefox -OutputDirectory artifacts\BrowserCompanion\firefox-extension
```

In Firefox, open `about:debugging#/runtime/this-firefox`, choose **Load Temporary
Add-on**, and select `manifest.json` in that directory. In Media Controls, open
**Media sources > Browser companion > Configure** and choose **Enable browser
integration**. Firefox's add-on ID is already included, so no ID entry is needed.
Reload existing media tabs and keep **Windows media** enabled for fallback.
Temporary add-ons are removed when Firefox restarts; load it again for another
test session. The same **Remove browser integration** action cleans up Firefox.

In Command Palette, a browser media source tag shows the site's name and tab
favicon. Sites without a supplied name use their hostname. Browser and profile
identifiers remain available in source connection details.

## Use the popup

Each media tab has its own row with artwork, title, artist, and playback state.
Play/Pause and track buttons follow the selected player's capabilities. **Open
tab** selects its tab and brings its current browser window forward. Expand
**players in this tab** when you need a specific embedded or individual player.
A sleeping tab can be opened or woken by Play when that player supports it.

HTML players appear after playback starts with sound enabled. Preloaded players
and muted autoplay previews stay hidden. Once detected, a player stays available
when paused or muted so you can resume it. Sites can also supply a Media Session
entry with their own metadata and controls.

Rows remain separate even if they play the same track. Playback progress is
read-only and appears when the page reports a position and duration. Missing
artwork uses a placeholder. Controls keep their exact player target; if the
player disappears, select a current player and try again.

If no media appears, start playback, reload the tab, and check the extension's
site access. Browser-internal pages and private browsing are not supported.

## Automatic Windows fallback

The companion sends a heartbeat every five seconds, independently of media.
After a complete catalog and ten seconds of healthy heartbeats, it takes over
from Windows media for that browser. An empty catalog is valid.

A disconnect or twenty seconds without a heartbeat withdraws companion rows.
Windows sessions return after sixty seconds total since the last heartbeat.
Recovery requires another ten stable seconds and a complete catalog. Sleep and
resume start a fresh grace period. Edge, Chrome, and Firefox switch independently, and
other Windows media apps remain available throughout.

Profiles sharing a browser application identity switch together. Install the
companion in every profile you use for media: Windows cannot fall back for only
one profile while another profile keeps the companion healthy. Paused media and
sleeping tabs do not trigger fallback. Tracks are never matched by metadata.

Standard Edge, Edge Dev, and Chrome installation paths are recognized. Firefox
uses the exact installation's registered Windows taskbar identity. Other channels,
portable browsers without a registration, and unknown application identities keep using
Windows media; their players can still be controlled in the popup.

## Update or move an existing installation

Media Controls package upgrades keep the bridge registration and its stable
launcher path. Browsers reconnect when the updated bridge becomes available.
No setup is required again for an ordinary package upgrade.

For an unpacked browser extension, reload it after updating its files and refresh
existing media tabs. Keeping its folder in place preserves its unpacked ID.
Moving it can change the ID: run **Enable browser integration** with all IDs you
want to retain. This replaces the previous allowlist.

If you previously loaded the extension from `experiments/BrowserCompanion`, remove
that unpacked extension and load `src/BrowserCompanion/extension`. Moving an
unpacked extension can change its ID: copy the ID from the new installation and
register it through **Configure > Advanced > Developer setup**. If a portable development relay was running,
stop it before switching to the packaged bridge, then reload the browser extension.

Changing only the popup requires reloading the extension. Rebuilding Media
Controls or pressing Reconnect does not reload browser code.

## Remove

Open **Media sources > Browser companion > Configure > Advanced** and choose
**Remove browser integration**. This disconnects the desktop companion, removes
its Chrome, Edge, and Firefox registrations, manifests, and Firefox launcher,
and disables the source.
No extension ID is needed. Windows media remains available when enabled.
Remove the browser extension separately in each browser/profile if desired.

For v1, uninstalling the Media Controls MSIX alone can leave inactive registry
entries, native-host manifests, and the small Firefox launcher under
`%LOCALAPPDATA%\JPSoftworks\MediaControls\BrowserCompanion`.
The packaged executable and app execution alias are removed, so these cannot start the uninstalled
bridge. Use **Remove browser integration** before uninstalling for cleanup. If
already uninstalled, reinstall Media Controls and use the same removal action.

## Connection help

Expand **Connection help** in the popup for the error and extension ID.

- **Access forbidden:** run setup with that exact ID for the browser you are using,
  then reload the extension. Moving an unpacked extension can change its ID.
- **Host not found:** install Media Controls and run **Enable browser integration**.
- **Host exited or failed to start:** check that **MediaControlsBrowserBridge.exe**
  is enabled in Windows **App execution aliases**, then reconnect.
- If registration was changed but the same error remains, fully exit the browser,
  including background processes, and reopen it after saving your work.

## Build from source

For packaged integration, build with `eng/Test-Package.ps1 -Aot -AfterDeploy Reload`
and use **Configure** in Media Controls. For standalone bridge development,
install the .NET 10 SDK, load `src/BrowserCompanion/extension`, then run this from
the repository root:

```powershell
.\eng\Register-BrowserCompanion.ps1 -ExtensionId '<your-extension-id>' -Browser Edge
```

The development host is published to `artifacts/BrowserCompanion/host` and requires
the .NET 10 runtime. It also starts automatically. For a self-contained release
bundle, run `eng/Build-BrowserCompanion.ps1` with the Windows native build tools
installed. The developer guide lists validation commands.

See the [privacy information](https://github.com/jiripolasek/MediaControlsExtension/blob/master/docs/user/browser-companion-privacy.md)
for the data observed and transferred. A copy is included as `PRIVACY.md` in the bundle.

For protocol details, CLI diagnostics, and live acceptance checks, see the
[developer guide](https://github.com/jiripolasek/MediaControlsExtension/blob/master/docs/dev/browser-companion.md).
