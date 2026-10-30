[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ResultPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
try {
    $package = [Windows.ApplicationModel.Package, Windows.ApplicationModel, ContentType = WindowsRuntime]::Current
    if ($package.Id.Name -ne 'JPSoftworks.MediaControls.BridgeProof') { throw 'Registration must run inside the proof package.' }
    $manifest = Join-Path $package.InstalledLocation.Path 'BrowserCompanion\native-host.json'
    if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { throw 'Packaged native host manifest is missing.' }
    foreach ($browser in @('Microsoft\Edge', 'Google\Chrome')) {
        $key = "HKCU:\Software\$browser\NativeMessagingHosts\com.jpsoftworks.media_controls_lifecycle"
        New-Item -Path $key -Force | Out-Null
        Set-Item -LiteralPath $key -Value $manifest
    }
    $result = [ordered]@{ success = $true; packageFullName = $package.Id.FullName; manifest = $manifest; processId = $PID }
}
catch {
    $result = [ordered]@{ success = $false; error = $_.Exception.ToString(); processId = $PID }
}
[IO.File]::WriteAllText($ResultPath, ($result | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
if (-not $result.success) { exit 1 }
