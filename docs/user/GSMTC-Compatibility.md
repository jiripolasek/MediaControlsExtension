# Player compatibility and GSMTC

Media Controls for Command Palette works with media applications that publish a Windows media session. It does not contain player-specific playback integrations: applications publish controls and metadata through [System Media Transport Controls (SMTC)](https://learn.microsoft.com/en-us/windows/apps/develop/media-playback/integrate-with-systemmediatransportcontrols), and the extension discovers and controls those sessions through the Global System Media Transport Controls (GSMTC) API.

The extension uses the capabilities advertised by each live session. A player may expose play and pause but omit previous, next, stop, shuffle, repeat, artwork, or the timeline information used to show track length. Availability can also change with the current content.

This is a practical compatibility guide, not a test certification. Player versions, settings, websites, browser behavior, and content types can change the result.

## Legend

### GSMTC integration

- ✅ Built into the application or browser.
- 🧩 Available through an optional plugin or add-on.
- ⚠️ Conditional on application settings, browser behavior, website support, or application version.
- ➖ No known integration for the listed version.

### Feature availability

- ✅ Usually exposed to GSMTC.
- ⚠️ Varies or is only partially exposed.
- ➖ Usually not exposed.

## Music players

| Player | GSMTC | Play/pause | Previous/next | Metadata | Artwork | Track length | Shuffle/repeat | Notes |
| --- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | --- |
| Spotify for Windows | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | Availability can still vary by account, device, and current content. |
| Apple Music for Windows | ✅ | ✅ | ✅ | ✅ | ✅ | ⚠️ | ⚠️ | If the session does not appear, start playback once and check that Windows background/media-key integration is allowed for the app. |
| Media Player | ✅ | ✅ | ⚠️ | ✅ | ⚠️ | ✅ | ⚠️ | Previous/next and playlist modes depend on the active queue and content type. |
| [foobar2000](https://www.foobar2000.org/) 1.5.1 or later | ✅ | ✅ | ✅ | ✅ | ✅ | — | — | Windows media-control integration was re-enabled by default in [foobar2000 1.5.1](https://www.foobar2000.org/changelog). |
| [Dopamine](https://github.com/digimezzo/dopamine-windows) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | Support can vary between major Dopamine versions. |
| [MediaMonkey](https://www.mediamonkey.com/) | ✅ | ✅ | ✅ | ✅ | — | — | — | App identity is generally available even when richer media properties are not. |
| MusicBee | 🧩 | ✅ | ✅ | ✅ | ✅ | ⚠️ | ✅ | Requires the third-party [mb_MediaControl plugin](https://github.com/ameer1234567890/mb_MediaControl). |
| AIMP | 🧩 | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | Requires the third-party [Windows 10 Media Control plugin](https://www.aimp.ru/?do=catalog&rec_id=1097). |
| iTunes | 🧩 | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | Requires the third-party [iTunes-SMTC integration](https://github.com/thewizrd/iTunes-SMTC). |

## Video and general media players

| Player | GSMTC | Play/pause | Previous/next | Metadata | Artwork | Track length | Shuffle/repeat | Notes |
| --- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | --- |
| Movies & TV | ✅ | ✅ | ⚠️ | ✅ | ⚠️ | ✅ | — | Previous/next depends on the active playlist. |
| VLC UWP | ✅ | ✅ | ✅ | ✅ | ✅ | — | — | This is the Microsoft Store/UWP edition, not the classic desktop application. |
| [MPC-HC](https://github.com/clsid2/mpc-hc) | ✅ | ✅ | ✅ | ✅ | ⚠️ | — | — | Feature availability depends on the media and playlist. |
| [MPC-BE](https://sourceforge.net/projects/mpcbe/) | ✅ | ✅ | ✅ | ✅ | ⚠️ | — | — | Feature availability depends on the media and playlist. |
| [Rise Media Player](https://github.com/theimpactfulcompany/Rise-Media-Player) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ⚠️ | Some transport actions may be reported as unavailable for particular content. |
| VLC desktop 3.x | 🧩 | ✅ | ✅ | ✅ | ⚠️ | — | — | Requires the third-party [VLC Windows 10 SMTC plugin](https://github.com/spmn/vlc-win10smtc), which targets VLC 3.0.x. |
| Winamp | 🧩 | ✅ | ✅ | ✅ | ⚠️ | — | — | Requires the third-party [gen_smtc plugin](https://github.com/NanMetal/gen_smtc). |
| mpv | 🧩 | ✅ | ⚠️ | ✅ | ⚠️ | — | — | Requires [MPV-SMTC](https://github.com/x0wllaar/MPV-SMTC) or [MPVMediaControl](https://github.com/datasone/MPVMediaControl); previous/next and artwork support differ between them. |

## Browsers and web players

Edge, Chrome, other Chromium-based browsers, and Firefox can publish browser media as a Windows session. For richer results, the website must also provide metadata and action handlers through the browser's [Media Session API](https://developer.mozilla.org/en-US/docs/Web/API/Media_Session_API).

| Browser or website | GSMTC | Play/pause | Previous/next | Metadata | Artwork | Track length | Shuffle/repeat | Notes |
| --- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | --- |
| Microsoft Edge | ✅ | ✅ | ⚠️ | ⚠️ | ⚠️ | ⚠️ | — | Capabilities come from the active website. |
| Google Chrome | ✅ | ✅ | ⚠️ | ⚠️ | ⚠️ | ⚠️ | — | Capabilities come from the active website. |
| Other Chromium-based browsers | ✅ | ✅ | ⚠️ | ⚠️ | ⚠️ | ⚠️ | — | Includes browsers such as Brave, Opera, and Vivaldi; individual builds can differ. |
| Firefox | ✅ | ✅ | ⚠️ | ⚠️ | ⚠️ | ⚠️ | — | Browser settings and website implementation can affect media-control integration. |
| YouTube and YouTube Music | ⚠️ | ✅ | ⚠️ | ✅ | ✅ | ⚠️ | ⚠️ | Appears through the browser; controls differ by site mode, queue, and account. |
| SoundCloud web player | ⚠️ | ✅ | ⚠️ | ✅ | ✅ | ⚠️ | — | Appears through the browser. |
| Netflix, Disney+, HBO Max, Prime Video, and similar services | ⚠️ | ✅ | — | ⚠️ | ⚠️ | ⚠️ | — | Appears through the browser or installed web app. Metadata and artwork can vary by service. |

Browser support is two-layered: the browser must bridge media to Windows, and the website must expose each action or metadata field. A site can therefore work differently in two browsers, and two sites can expose different controls in the same browser.

## Players that may need an integration

These community integrations can make additional desktop players publish an SMTC session. They are maintained separately from Media Controls for Command Palette; review their compatibility and installation instructions before using them.

| Player | Integration |
| --- | --- |
| MusicBee | [mb_MediaControl](https://github.com/ameer1234567890/mb_MediaControl) |
| AIMP | [Windows 10 Media Control plugin](https://www.aimp.ru/?do=catalog&rec_id=1097) |
| iTunes | [iTunes-SMTC](https://github.com/thewizrd/iTunes-SMTC) |
| VLC desktop 3.x | [vlc-win10smtc](https://github.com/spmn/vlc-win10smtc) |
| Winamp | [gen_smtc](https://github.com/NanMetal/gen_smtc) |
| mpv | [MPV-SMTC](https://github.com/x0wllaar/MPV-SMTC) or [MPVMediaControl](https://github.com/datasone/MPVMediaControl) |

## What Media Controls can use

Depending on the data and capabilities advertised by a live session, the extension can use:

- play and pause;
- stop;
- previous and next;
- shuffle and repeat;
- title, artist, album, album artist, subtitle, genre, track number, and media type;
- artwork;
- playback state and track length derived from timeline information;
- application identity for the **Switch to Application** command.

The player decides which values and controls are available. The extension cannot add a missing capability to a session.

System-volume commands are independent of GSMTC. They control the default Windows playback device, so volume up, volume down, mute, unmute, and presets can work even when no compatible media session is active.

## Troubleshooting

If an application does not appear or a command is unavailable:

1. Start playback in the application or browser tab at least once. Some players do not publish a session while idle.
2. Check the player's settings for media keys, system media controls, background activity, or operating-system integration.
3. For browser playback, try the same website in Edge, Chrome, or Firefox. Website and browser support are both required.
4. Confirm that Windows or another GSMTC-aware utility can see the session. If the application does not publish a Windows media session, this extension cannot discover it.
5. Update the application, browser, and extension. For a plugin-enabled player, also verify the plugin's supported player version and architecture.

When reporting a compatibility problem, include the player and version, Windows version, whether the player is native or browser-based, and which metadata or commands are missing.
