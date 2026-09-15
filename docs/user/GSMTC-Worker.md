# Windows media in a separate process

Media Controls runs Windows GSMTC in its own worker process by default. This
isolates native Windows media failures from the extension. Open **Media sources**
from the Media Controls command menu to change modes. Selecting either Windows
media provider disables the other. Both can also be disabled.

On upgrade, legacy Windows-media enablement moves to the worker. Disabled Windows
media stays disabled. An explicit choice already made between internal and worker
mode is preserved. Internal mode remains available for troubleshooting.

Both modes use the same GSMTC
backend and support the same Windows sessions, playback controls and artwork.
There is a brief interruption while the previous provider stops and the selected
one connects. Other enabled media sources continue independently.

The worker belongs to the extension instance. Disposing that instance stops its
workers even if the extension process stays alive. Killing the extension process
terminates its workers through Windows job ownership. A disconnected worker exits;
it cannot reconnect to a replacement extension. Media applications run separately.

If the worker fails, sessions become unavailable while Media Controls attempts up
to three replacements. Commands with uncertain outcomes are not repeated. Recovery
does not enable internal GSMTC automatically. If recovery stops, disable and enable
the worker provider to retry. If switching reports that the previous provider
failed to stop, reload the extension before trying again.

Conflicting saved mode choices leave the Windows media group inactive and show a
configuration diagnostic. Explicitly enabling either mode repairs the selection.
Local VLC exclusions apply to both Windows media modes, including while VLC is
disconnected.

Diagnostic archives include separate worker logs. For detailed worker diagnostics,
enable detailed logging before enabling the worker; disable and enable it again if
it was already running. Normal lifecycle logs include worker and owner process IDs,
connection lifetime, startup/shutdown timing, exit codes and restart count.

Both processes favor memory conservation in the managed runtime. While the user
is idle, they also occasionally release resident memory pages above a soft
threshold. This can lower the Task Manager reading; pages can return to RAM when
media controls are used again. It is not a fixed memory limit or a remedy for a
leak. Native allocations and artwork also contribute to usage.
