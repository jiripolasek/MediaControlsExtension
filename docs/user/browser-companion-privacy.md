# Browser companion privacy

The browser companion connects media tabs to Media Controls for Command Palette.
It has no account, analytics, advertising, or cloud service. Media observations and
playback commands travel between the browser and the desktop companion on your PC.

## What it reads

On sites where you allow access, the extension observes HTML audio/video players
and the page's Media Session API. It reads media titles, artists, albums, artwork
URLs, playback state, available controls, and reported position and duration. It
also reads the tab's title, site name, favicon, and URL. Page URLs sent to the
desktop omit query strings and fragments; their host and path can still identify
the page you are viewing. Artwork URLs may contain the site's own parameters.

The extension observes frames and may find paused or inactive players. It does not
read form entries, passwords, browser history, or cookies. Private browsing and
browser-internal pages are excluded. Pages and their media metadata are untrusted;
the browser supplies the actual tab and document identities used for commands.

The desktop configuration page reads Windows browser registrations to list
installed browsers. The local native bridge reports its launching browser's
product name and executable path so profiles can be grouped by installation.
These details stay on your PC and are not read from web page content.

## Where data goes

The native messaging bridge and relay run as your Windows account. A named pipe
restricted to that account connects them to Media Controls. There is no listening
TCP port and no remote control server.

The popup and desktop may download artwork and favicons from the URLs reported by
the site. Those requests contact the image server, which receives your IP address
and the requested URL. The desktop does not send browser cookies or credentials.
The popup requests images anonymously and without a referrer. No media metadata
is sent to the project author or to an analytics service.

## What is stored

The extension stores one random identifier in the browser profile so multiple
profiles remain distinct. The live media catalog stays in memory. Desktop cover
art is cached in memory; favicon files are kept in a private local cache and
removed when the provider shuts down normally. Abnormal shutdown may leave cache
files behind.

Packaged setup stores the allowed extension IDs and bridge launcher paths under
`%LOCALAPPDATA%\JPSoftworks\MediaControls\BrowserCompanion`. It registers
`native-host.json` for Chrome and Edge and `firefox-native-host.json` for Firefox
under your Windows user account. Firefox also uses a small `firefox-bridge.cmd`
launcher to start the same packaged bridge. These files and registry entries
contain no media metadata. **Remove browser integration** deletes them;
uninstalling Media Controls alone can leave these inactive files and entries behind in v1.

Firefox's installation prompt declares browsing activity and website content
because media details and page URLs are transferred to the local desktop bridge.
These declarations cover that local transfer; no cloud service is involved.

Optional developer tracing records command identifiers, foreground-permission
handoffs, and process/window identifiers locally. It is disabled unless the
developer tracing marker is present. Diagnostic files you choose to share may
contain source details; review them before sharing.

## Your controls

- Limit or revoke the extension's site access in your browser's extension settings.
- Disable **Browser companion** on the Media Controls **Media sources** page to
  stop using it in Command Palette. Browser popup controls remain available.
- Disable or remove the browser extension to stop its observations and connection.
- Use **Remove browser integration** in the source configuration page to disconnect
  the desktop bridge and remove its browser registration.
- Follow the [removal instructions](https://github.com/jiripolasek/MediaControlsExtension/blob/master/docs/user/browser-companion.md#remove) to remove the
  desktop registration and companion files.

The broad HTTP/HTTPS permission lets the extension discover players in background
tabs and embedded frames. Native messaging connects to the local desktop helper;
storage preserves the profile identifier; alarms reconnect after interruption;
web navigation retires old document targets when a tab or frame navigates.
