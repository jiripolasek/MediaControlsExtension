[CmdletBinding()]
param(
    [Parameter(Mandatory)][Guid] $Instance,
    [Parameter(Mandatory)][string] $ResultPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
try {
    $package = [Windows.ApplicationModel.Package, Windows.ApplicationModel, ContentType = WindowsRuntime]::Current
    if ($package.Id.Name -ne 'JPSoftworks.MediaControls.BridgeProof') { throw 'Run the parent inside the proof package.' }
    $location = $package.InstalledLocation.Path
    if ($PSScriptRoot -ne (Join-Path $location 'ProofHelper')) { throw 'Use the installed helper script.' }
    [xml] $manifest = Get-Content -LiteralPath (Join-Path $location 'AppxManifest.xml') -Raw
    $capabilities = @($manifest.Package.Capabilities.ChildNodes | Where-Object NodeType -eq Element | ForEach-Object { $_.GetAttribute('Name') })
    if ($capabilities.Count -ne 1 -or $capabilities[0] -ne 'runFullTrust') { throw 'The helper proof must declare only runFullTrust.' }
    $childResult = "$ResultPath.child.json"
    if (Test-Path -LiteralPath $childResult) { throw 'Choose a new result path.' }
    $helper = Join-Path $PSScriptRoot 'Register-ExternalHostProof.ps1'
    if ($ResultPath.Contains('"') -or $helper.Contains('"')) { throw 'Invalid quoted path.' }
    $arguments = '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}" -Instance {1} -PackageLocation "{2}" -ResultPath "{3}"' -f $helper, $Instance, $location, $childResult
    $shell = New-Object -ComObject Shell.Application
    $shell.ShellExecute((Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'), $arguments, $location, 'open', 0)
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (!(Test-Path -LiteralPath $childResult) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if (!(Test-Path -LiteralPath $childResult)) { throw 'The external helper did not complete within 15 seconds.' }
    $child = Get-Content -LiteralPath $childResult -Raw | ConvertFrom-Json
    $result = [ordered]@{ success = [bool]$child.success; parentPackage = $package.Id.FullName; parentProcessId = $PID; capabilities = $capabilities; child = $child }
}
catch { $result = [ordered]@{ success = $false; error = $_.Exception.ToString(); parentProcessId = $PID } }
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ResultPath
if (-not $result.success) { exit 1 }
