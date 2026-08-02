<div align="center">

<img src="./art/logo.svg" alt="Logo" width="200" height="200">
<h1 align="center"><span style="font-weight: bold">Media Controls</span> <br /><span style="font-weight: 200">for Command Palette</span></h1>

</div>

Take full control of your media playback without leaving your workflow. Media Controls for [PowerToys Command Palette](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/overview) brings Windows media sessions and system audio controls together in the Command Palette and Dock.

The extension works with desktop apps, Microsoft Store apps, and browser tabs that publish a Windows Global System Media Transport Controls (GSMTC) session.

## Features

- **Playback controls** — Play, pause, stop, skip tracks, and change shuffle or repeat mode when the active player makes those actions available.
- **Multiple media sessions** — Browse every active session, switch to its application, and move between players. Optionally pause other sessions when starting a new one.
- **Rich media details** — View title, artist, album, artwork, player, track length, playback state, and additional metadata supplied by the player.
- **System volume controls** — See the volume of the default Windows playback device, mute or unmute, adjust the level, or jump to 0%, 25%, 50%, 75%, or 100%.
- **Command Palette integration** — Put Now Playing and volume actions on the home page, surface media commands in global search, keep the palette open for consecutive actions, and show action notifications.
- **Dock controls** — Add a compact media band with current playback and optional previous/next controls, plus a system volume band in the flyout.
- **Personalization** — Choose separate icon themes for the Command Palette and Dock, use album art in the session list, configure which commands appear on each surface, and choose what the first dock item does (bring the app to front, open the main page, or open media details).

Popular compatible players include Spotify, Apple Music, foobar2000, Media Player, VLC UWP, and media websites running in Edge, Chrome, other Chromium-based browsers, or Firefox. Exact controls and metadata depend on what each player exposes to Windows.

See the [player compatibility and GSMTC guide](docs/user/GSMTC-Compatibility.md) for the feature matrix, browser notes, optional integrations, and troubleshooting. For a version-by-version history of changes, see the [changelog](CHANGELOG.md).

## Installation

> **Note:** This extension requires [Microsoft PowerToys](https://apps.microsoft.com/detail/xp89dcgq3k6vld) to be installed.

### Microsoft Store installation (recommended)

<a href="https://apps.microsoft.com/detail/9N3BQ81G19K7"><img alt="alt_text" width="240px" src="https://get.microsoft.com/images/en-us%20dark.svg" /></a>

### Command Palette

![Command Palette Installation Page](art/command_palette_installation_page.png)

- Open the Command Palette.
- Navigate to the page *Install Command Palette extensions*.
- From the list of extensions, select *Media Controls for Command Palette*.

### WinGet

- Open a terminal.
- Run:

  ```pwsh
  winget install -e --id JiriPolasek.MediaControlsforCommandPalette
  ```

### Manual installation

- Download the MSIX installer from the [Releases](https://github.com/jiripolasek/MediaControlsExtension/releases) section and run it.

## Licence

Apache 2.0

## Author

[Jiří Polášek](https://jiripolasek.com)

## Very special thanks

[Mike Griese](https://github.com/zadjii-msft)