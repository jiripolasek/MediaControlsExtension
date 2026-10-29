This policy covers the following products and services from **Jiri Polasek**:

- **Media Controls for Command Palette:** the Windows desktop extension, including
  its Command Palette and Dock presentation and optional media sources.
- **Media Controls Companion:** the browser extensions for Microsoft Edge, Google
  Chrome, and Mozilla Firefox, including their desktop bridge.
- **The Media Controls website:** these product, setup, support, and privacy pages.

Last updated: **29 October 2026**.

No Media Controls account is required. The project does not operate an analytics,
advertising, or cloud media service. The products handle media information to
provide their features. Some optional features contact other computers or image
providers, as described below.

## Command Palette extension

### Media information and controls

The desktop extension reads the media sessions exposed by Windows or by enabled
media sources. Depending on the player, this includes track or video titles,
artists, albums, genres, track numbers, artwork, playback state, position,
duration, and available controls. It uses application names and identifiers, and
where available executable paths and window information, to label players and
switch to their applications.

Media details are displayed in PowerToys Command Palette and the Dock. Playback
commands go to the selected player. System volume controls read and change the
Windows audio output's volume and mute state; they do not record audio or video.

The recommended and compatibility Windows media sources use Windows media APIs.
The optional iTunes source uses the local desktop iTunes automation interface to
read the current track and control playback. It does not browse your library or
upload it to the project author. These sources can use a separate local worker
process; communication with that worker remains on your PC.

### Optional VLC connection

If you configure VLC, Media Controls contacts the VLC HTTP interface at the server
URL you enter. That server can be on this PC or another computer. It receives
connection requests, playback commands, and the configured VLC password through
HTTP Basic authentication. Media Controls reads player status and metadata and
can retrieve artwork from that same server. The server sees your connection's IP
address and requested resources.

HTTP does not encrypt those requests or credentials. HTTPS protects the connection
when your configured server supports it. Disabling VLC stops automatic polling,
but does not erase its saved settings or password. **Test saved connection** still
contacts the configured server when you request that check, even while the source
is disabled.

### Desktop settings and credentials

Display and playback preferences, enabled media sources, and the configured VLC
server URL are saved in a local settings file. They remain across launches and
updates until changed or their stored app data is removed. Recovery of malformed
settings can leave a local backup of the previous file.

VLC passwords are stored by server URL in Windows Credential Locker, not in the
plain-text settings file. Windows protects those credentials and can roam them
with your Microsoft account under its own account and system behavior; see
[Microsoft's Credential Locker documentation](https://learn.microsoft.com/en-us/windows/apps/develop/security/credential-locker).
The project author does not receive them. Credentials remain until replaced or
removed from Credential Locker. Leaving the VLC password field blank reuses the
saved password; it does not delete it. Disabling VLC or removing browser
integration does not remove VLC credentials.

### Desktop artwork and caches

Artwork obtained from Windows media sessions is handled in memory. The iTunes
source exports current artwork to a temporary file, reads it, and attempts to
delete that file afterward. Interrupted operations or cleanup failures can leave
temporary files behind. VLC artwork is fetched from the configured VLC server
and cached in memory. Browser artwork and favicon handling is described in the
browser companion section below.

### Local diagnostic logs

The desktop extension writes local operational and error logs during normal use.
Worker processes also write local diagnostic logs. These can contain application
or source identifiers, configured endpoints, local paths, errors, and other
context relevant to the operation. They are not automatically uploaded.

Detailed logging adds diagnostic context and can include media or source details
and process or window identifiers. It is normally off, can be enabled under
**Report a problem**, and can be enabled by an explicit developer/debug launch.
Turning it off stops additional detail; it does not erase existing logs.

Logs can persist across restarts and are subject to local log rotation or manual
deletion. **Save diagnostic logs** creates a ZIP on your Desktop; it does not
send the ZIP to anyone. Exported ZIPs remain until you delete them. Close the
extension before manually removing its local settings, logs, or cache files.

## Browser companions

### What the companion does

The companion discovers browser media and provides playback controls, exact-tab
activation, and integration with Media Controls in PowerToys Command Palette and
the Dock. Observations and commands travel between the browser and the desktop
application on your PC.

### What the companion reads

On sites where you allow access, the companion observes HTML audio and video
players and the page's Media Session API. It reads titles, artists, albums, artwork
URLs, playback state, available controls, and reported position and duration. It
also reads the tab title, site name, favicon, and URL. It can observe media in
embedded frames, including paused or inactive players.

Page URLs sent to the desktop omit query strings and fragments. Their host and
path can still identify the page you are viewing. Artwork URLs can contain the
site's own parameters.

The companion does not read form entries, passwords, browser history, or cookies.
Private browsing and browser-internal pages are excluded. Store disclosures about
browsing activity or web history refer to the current page details described
above, not access to your saved browser history.

### The desktop connection

The native messaging bridge runs under your Windows account. A named pipe
restricted to that account connects it to Media Controls. It does not open a
listening TCP port or a remote control server.

Desktop configuration reads Windows browser registrations. The local bridge
reports the browser product name and executable path so profiles can be grouped
by installation. Those details stay on your PC.

### Artwork and favicons

The popup and desktop may request artwork and favicons from URLs supplied by the
website. The image server receives your IP address and the requested URL. The
desktop does not send browser cookies or credentials; the popup requests images
anonymously and without a referrer. The image provider's own privacy policy
applies to those requests.

Media metadata is not automatically sent to the project author or an analytics
service. Information you choose to share for support is described below.

### Browser storage and retention

The companion stores a random identifier in your browser profile to distinguish
profiles. It remains until the extension's local data is cleared or the extension
is removed. The live browser media catalog and its desktop artwork cache stay in
memory. That catalog is not saved as a listening or browsing history database;
diagnostic logging is described separately in this policy.

Desktop favicons use a private local cache that is removed on a normal shutdown.
An abnormal shutdown can leave cache files behind.

Desktop setup stores allowed extension IDs, bridge paths, native-host manifests,
and browser registrations under your Windows account. Firefox also uses a small
launcher. These files do not contain media metadata and remain until you remove
the integration. Uninstalling the desktop app alone can leave inactive entries
behind. Use **Remove browser integration** before uninstalling; see the
[removal instructions](../support/#remove).

### Why the browser asks for access

- **Website access (HTTP and HTTPS):** finds media in background tabs and embedded
  frames on sites you permit. You can restrict access in your browser's extension
  settings; restricted sites will not appear.
- **Native messaging:** connects the companion to the local Media Controls bridge
  and exchanges media information and playback commands.
- **Storage:** preserves the random browser profile identifier.
- **Alarms:** maintains the connection and retries after interruption.
- **Web navigation:** keeps tab and frame targets current when pages navigate.

Firefox's installation prompt refers to browsing activity and website content
because media details and page URLs are transferred to the local desktop bridge.
No cloud service is involved.

### Limited use of browser data

Media Controls Companion follows the
[Chrome Web Store User Data Policy](https://developer.chrome.com/docs/webstore/program-policies/user-data),
including its Limited Use requirements. Browser data is used only for the media
discovery, playback, tab activation, desktop connection, and troubleshooting of
those features. It is not sold, used for advertising or profiling, or used to assess
creditworthiness or lending eligibility.

The project author does not receive or routinely inspect your media or browsing
data. If you choose to share diagnostic information for support, share only what
is needed for that request.

## Your controls and removal

- Disable a desktop media source to stop its discovery or connection. This does
  not stop the player's own playback or delete saved settings or VLC credentials.
- Disable or uninstall the Command Palette extension to stop its desktop media
  access. Uninstalling does not guarantee removal of exported logs, temporary
  files, or credentials managed separately by Windows.
- Limit or revoke site access in the browser's extension settings.
- Disable **Browser companion** in Media Controls to stop using it in Command
  Palette. The browser popup can still work.
- Disable or remove the browser extension to stop its observations and connection.
- Use **Media sources > Browser companion > Configure > Advanced > Remove browser
  integration** to remove the desktop registration and companion files.
- Remove the extension in each browser profile to clear its local profile data.

There is no project account or server-side media catalog to delete. See
[setup and connection help](../companions/#setup) or the
[support page](../support/) for more information.

## Developer diagnostics and support

In addition to the desktop logs described above, optional browser bridge tracing
records command identifiers, foreground permission handoffs, and process and
window identifiers locally. That developer tracing is disabled by default.
Review diagnostic files and remove private URLs, media details, and other
personal information before choosing to share them.

Information you submit in a public GitHub issue is visible publicly and handled
by GitHub. Do not post private URLs, personal media details, or unreviewed logs.

## Website

This website uses **no cookies, tracking, or analytics**. Images and fonts are
served from the same site or provided by your device. The theme follows your
device until you choose Light or Dark. Your choice is saved in this browser using
local storage. Clear this site's data to remove that preference and follow your
device again.

The hosting provider can process normal web request information, such as your IP
address and the requested URL. Download buttons and external links take you to
their respective providers, whose own privacy policies apply.

## Shared data-use commitments

The project does not sell media, browsing, or credential information, use it for
advertising or profiling, or use it to assess creditworthiness or lending
eligibility. Media information is used to provide the controls, presentation,
and integrations described here. The project author does not receive a live
media catalog. Data you deliberately share for support is used for that request.

Windows, PowerToys, media players, configured VLC servers, image providers, GitHub,
and browser or app stores have their own data practices. This policy covers
Media Controls and its website, rather than the independent operation of those
products and services.

## Questions and changes

For privacy questions, contact Jiri Polasek through
[jiripolasek.com](https://jiripolasek.com) or the project's
[GitHub issues](https://github.com/jiripolasek/MediaControlsExtension/issues).

Changes to this policy will be published on this page with an updated date. Any
change requiring new browser permissions or consent will also use the browser's
applicable permission and consent flow.
