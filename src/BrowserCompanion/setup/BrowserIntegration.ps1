[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Register', 'Remove')][string] $Action,
    [string] $ExtensionIds = '',
    [Parameter(Mandatory)][ValidatePattern('^JPSoftworks\.MediaControls\.BrowserSetup\.[a-f0-9]{32}$')][string] $ResultPipe,
    [Parameter(Mandatory)][int] $CallerProcessId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$result = @{ success = $false }
$mutex = $null
$acquired = $false
try {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class BrowserIntegrationIdentity {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetCurrentPackageFullName(ref uint length, IntPtr name);
}
'@
    $length = [uint32]0
    if ([BrowserIntegrationIdentity]::GetCurrentPackageFullName([ref]$length, [IntPtr]::Zero) -ne 15700) {
        throw 'Windows did not start the setup helper outside the package. Browser integration was not changed.'
    }
    $config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'packaged-integration.json') -Raw | ConvertFrom-Json
    $packageRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    [xml] $manifest = Get-Content -LiteralPath (Join-Path $packageRoot 'AppxManifest.xml') -Raw
    if ($manifest.Package.Identity.Name -ne $config.packageName) { throw 'The browser bridge does not belong to this Media Controls package.' }
    $localData = [Environment]::GetFolderPath('LocalApplicationData')
    $directory = Join-Path $localData 'JPSoftworks\MediaControls\BrowserCompanion'
    $alias = Join-Path $localData "Microsoft\WindowsApps\$($config.executionAlias)"
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $mutex = [Threading.Mutex]::new($false, "Local\JPSoftworks.MediaControls.BrowserSetup.$sid")
    try { $acquired = $mutex.WaitOne(5000) }
    catch [Threading.AbandonedMutexException] { $acquired = $true }
    if (!$acquired) { throw 'Another browser integration setup is running. Try again in a moment.' }
    Import-Module (Join-Path $PSScriptRoot 'PackagedRegistration.psm1') -Force
    if ($Action -eq 'Register') {
        $ids = @($config.storeExtensionIds) + @($ExtensionIds.Split(',') | Where-Object { $_ })
        $firefoxIds = @($config.firefoxExtensionIds)
        if ($ids.Count -eq 0 -and $firefoxIds.Count -eq 0) { throw 'Store installation is not available yet. For an unpacked extension, use Advanced > Developer setup.' }
        Set-PackagedCompanionRegistration -Directory $directory -AliasPath $alias -ExtensionId $ids -FirefoxExtensionId $firefoxIds
    }
    else {
        Remove-PackagedCompanionRegistration -Directory $directory
        $bridgePath = Join-Path $PSScriptRoot 'JPSoftworks.MediaControlsExtension.BrowserCompanion.exe'
        foreach ($process in Get-Process -Name JPSoftworks.MediaControlsExtension.BrowserCompanion -ErrorAction SilentlyContinue) {
            if ($process.Id -ne $CallerProcessId -and $process.Path -eq $bridgePath) {
                try {
                    $process.Kill()
                    if (!$process.WaitForExit(2000)) { throw 'The browser bridge did not stop. Try removal again.' }
                }
                catch { if (!$process.HasExited) { throw } }
            }
        }
    }
    $result = @{ success = $true }
}
catch { $result = @{ success = $false; error = $_.Exception.Message } }
finally {
    if ($acquired) { $mutex.ReleaseMutex() }
    if ($null -ne $mutex) { $mutex.Dispose() }
}
$pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $ResultPipe, [IO.Pipes.PipeDirection]::Out)
try {
    $pipe.Connect(3000)
    $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false))
    try { $writer.WriteLine(($result | ConvertTo-Json -Compress)); $writer.Flush() }
    finally { $writer.Dispose() }
}
finally { $pipe.Dispose() }
if (!$result.success) { exit 1 }
