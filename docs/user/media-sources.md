# Media sources

Open the Media Controls command menu and choose **Media sources** to choose which
players the extension discovers. Each source has a description and status badge.
Select a row to see setup guidance, actions, and connection details.

Row icons identify the source. Status badges are green for enabled sources, gray
for disabled sources, and amber when a source needs attention. Their text and
tooltips explain the state. Menus and details show the same actions in the same
order, with an icon for each action.

## Choose a source

| Source | When to use it |
| --- | --- |
| **Windows media**, under Recommended | Enabled by default. Discovers apps that support Windows media controls, including Apple Music, Spotify, and browsers. Start playback in the app; no extra setup is needed. |
| **iTunes**, under Optional players | For classic desktop iTunes. Off by default. Open iTunes and choose **Enable**. The Apple Music app uses Windows media instead. See the [iTunes guide](itunes-backend.md). |
| **VLC media player**, under Optional players | For a local or remote VLC 3 player. Off by default. Choose **Configure**, enter the Web interface URL and password, then choose **Save and enable**. See the [VLC setup guide](vlc-backend.md). |
| **Windows media (compatibility)**, under Troubleshooting | An alternative for diagnosing Windows media problems. Enabling it replaces the recommended Windows media source. Enable the recommended source to switch back. See the [Windows media troubleshooting guide](GSMTC-Worker.md). |

Actions are available in the details pane and the source's command menu. Enter
opens configuration for VLC or enables/disables a source that has no configuration
form. Disabling a source stops discovery and releases its connection; it does not
close the player or stop playback.

Turning off the active Windows media source asks for confirmation because Apple
Music, Spotify, browser playback, and other Windows media sessions will disappear
from Media Controls. Enabled VLC and iTunes sources remain available. To switch
between Windows media modes, enable the other mode directly; the extension turns
off the previous mode automatically.

If neither Windows media mode is enabled and no player session is visible, the
main Media Controls page shows **Windows media is turned off**. Choose **Enable
Windows media** to turn on the recommended source, or use **Media sources** to
choose another source. This reminder does not change your saved choices until
you choose an action.

Your enabled and disabled choices are saved. Updates preserve explicit choices.
If no iTunes choice has been saved, the new default leaves it off, including on
existing installations.

## Save and check VLC setup

**Save and enable** saves the connection settings and enables VLC. **Save** only
saves settings and preserves its current enabled or disabled choice. The combined
action is offered while VLC is disabled. Failed validation or saving leaves the
source disabled.

Use **Test saved connection** from the configuration page's command menu to check
the settings. The test works while the source is disabled and does not change
playback. Return to Media sources to see the current connection status.

## Understand the status

| Status | What to do |
| --- | --- |
| **Off** | Choose Enable when you want to use the source. VLC can be configured first. |
| **Starting** or **Connecting** | Wait for the connection to complete. |
| **Waiting for playback** | The source is enabled but has no media sessions. Start playback or select media in the player. |
| **Waiting for iTunes** | Open desktop iTunes. The source reconnects automatically. |
| **Not connected** | Check that the player is running. For VLC, check its Web interface and use Test saved connection. |
| **1 session** or **N sessions** | Players are available in Media Controls. |
| **Needs attention** | Read the messages in the details pane. For an enabled source that failed, resolve the problem and choose Retry. If it is disabled, choose Enable after resolving the problem. |

Retry keeps the source enabled. Technical provider state, connection messages,
and session counts remain in the details pane for troubleshooting.
