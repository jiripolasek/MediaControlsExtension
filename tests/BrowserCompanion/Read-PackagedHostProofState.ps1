[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ResultPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$result = [ordered]@{ processId = $PID; keys = @() }
try { $result.package = [Windows.ApplicationModel.Package, Windows.ApplicationModel, ContentType = WindowsRuntime]::Current.Id.FullName }
catch { $result.package = $null }
if ($null -ne $result.package) { throw 'Run this reader from an ordinary desktop process outside package virtualization.' }

foreach ($browser in @('Microsoft\Edge', 'Google\Chrome')) {
    $keyPath = "Software\$browser\NativeMessagingHosts\com.jpsoftworks.media_controls_lifecycle"
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($keyPath)
    try { $value = if ($null -ne $key) { $key.GetValue('') } else { $null } }
    finally { if ($null -ne $key) { $key.Dispose() } }
    $result.keys += [ordered]@{ path = $keyPath; exists = $null -ne $key; value = $value }
}

$result.processes = @(Get-CimInstance Win32_Process -Filter "Name='BrowserCompanion.exe'" |
    Where-Object { $_.CommandLine -like '*JPSoftworks.MediaControls.BridgeProof_*' } |
    Select-Object ProcessId, ParentProcessId, ExecutablePath, CommandLine)
$result.observedUtc = [DateTime]::UtcNow.ToString('o')
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ResultPath
