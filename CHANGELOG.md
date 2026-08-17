# Changelog

All notable changes to Media Controls for Command Palette are documented in this file.

<!--
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
-->

## [0.22.0] - 2026-08-17

This is a servicing update for the 0.20 release.

### Fixed

- **Media session stability** - hardened GSMTC object lifetimes, command execution, cleanup, and failed-session retirement to avoid crashes originating inside Windows media APIs. ([#49](https://github.com/jiripolasek/MediaControlsExtension/pull/49))
- **Media app discovery stability** - failed cross-process application identity lookups no longer escape through unmanaged window-enumeration callbacks. ([#46](https://github.com/jiripolasek/MediaControlsExtension/pull/46))

## [0.20.0] - 2026-08-04

### Added

- **Media metadata** - media details on Now Playing and media list items, a full metadata page, and a Ctrl+I keyboard shortcut to open it. ([#36](https://github.com/jiripolasek/MediaControlsExtension/pull/36))
- **Icon themes** - choose separate icon themes for the Command Palette and Dock. Fluent icons are now the default fallback and are used in the volume control context menu; volume-zero and unmute now have distinct icons. ([#37](https://github.com/jiripolasek/MediaControlsExtension/pull/37))
- **System volume dock band** - see and adjust the system volume directly from the flyout.
- **Configurable first dock item behavior** - choose whether it brings the app to front, opens the main page, or opens the metadata info page.
- **"Report a problem" page** for filing issues from inside the extension.
- **Adaptive session expiration grace period** - keeps media controls available while supported apps briefly tear down and recreate their sessions, such as browsers when skipping YouTube tracks. ([#40](https://github.com/jiripolasek/MediaControlsExtension/issues/40))
- Separators in context menus for better grouping.

### Changed

- **Native AOT** - Release builds are now published as Native AOT with full trimming, for faster startup and a smaller memory footprint. A managed, untrimmed publish remains available via `-p:ForceManagedPublish=true`.
- **Volume controls overhaul** - improved volume commands and the underlying audio infrastructure, including a new system volume monitor. ([#33](https://github.com/jiripolasek/MediaControlsExtension/pull/33))
- **Settings page redesign** - improved layout and formatting. ([#35](https://github.com/jiripolasek/MediaControlsExtension/pull/35))
- **Media service rewrite** - GSMTC operations are now serialized, making session tracking and metadata updates significantly more reliable. ([#32](https://github.com/jiripolasek/MediaControlsExtension/pull/32))
- Media thumbnails are now enabled by default.
- Previous/next track fallback commands were converted to normal commands.
- Upgraded to .NET 10.
- Logging switched to `Microsoft.Extensions.Logging` together with an updated `JPSoftworks.CommandPalette.Extensions.Toolkit`.
- Updated localization, readme, and documentation.

### Fixed

- Possible memory leak in media session handling, resolved by the media service rewrite. ([#31](https://github.com/jiripolasek/MediaControlsExtension/issues/31))
- Initial dock band labels no longer show the track title by default (regression in skip-track items).
- Improved reliability of metadata updates.

## [0.10.0] - 2026-02-09

### Added

- Support for pinning the media player as a band ([#8](https://github.com/jiripolasek/MediaControlsExtension/pull/8), contributed by [@zadjii-msft](https://github.com/zadjii-msft)).

## [0.8.0] - 2025-08-12

### Added

- "Now Playing" item on the Command Palette home page - see and control playback directly, including skipping tracks or switching media apps.
- Commands to switch between media applications from "Now Playing".
- Option to keep the Command Palette open or dismiss it after activating a command.
- Toast notification feedback for commands, toggleable in settings.
- Option to hide Next/Previous track commands from the Media Controls page; they remain available in the More menu and via keyboard shortcuts.

### Changed

- Improved icon updates and reduced UI jitter when switching media or applications.
- Now runs in eco mode, saving performance, battery, and the planet.

## [0.4.0] - 2025-06-26

### Added

- Top-level commands such as `/play`, `/pause`, `/mute`, and `/skip` for direct execution, with a settings option to enable or disable them.

### Changed

- Major upgrade to switching to the application playing the media, especially for PWAs and desktop applications.
- Bug fixes and performance improvements.

## [0.2.1] - 2025-05-20

Initial public release.
