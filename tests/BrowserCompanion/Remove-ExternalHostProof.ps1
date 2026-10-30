[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $EvidenceDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ProofPackageIdentity
{
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint length, IntPtr buffer);
    public static int GetStatus()
    {
        uint length = 0;
        return GetCurrentPackageFullName(ref length, IntPtr.Zero);
    }
}
'@
$identityStatus = [ProofPackageIdentity]::GetStatus()
if ($identityStatus -ne 15700) { throw "Expected APPMODEL_ERROR_NO_PACKAGE, received $identityStatus." }
$evidence = (Resolve-Path -LiteralPath $EvidenceDirectory).Path
$state = Get-Content -LiteralPath (Join-Path $evidence 'state.json') -Raw | ConvertFrom-Json
$instance = [Guid]::Parse($state.instance)
$external = Get-Content -LiteralPath (Join-Path $evidence 'external-files-v1.json') -Raw | ConvertFrom-Json
$proofBase = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'JPSoftworks\BrowserCompanionProof'))
$directory = [IO.Path]::GetFullPath((Join-Path $proofBase $instance.ToString()))
$manifest = Join-Path $directory 'native-host.json'
if ($external.manifest -ne $manifest -or !$directory.StartsWith($proofBase + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected cleanup target.' }
$sha256 = [Security.Cryptography.SHA256]::Create()
try { $manifestHash = [BitConverter]::ToString($sha256.ComputeHash([IO.File]::ReadAllBytes($manifest))).Replace('-', '') }
finally { $sha256.Dispose() }
if ($manifestHash -ne $external.manifestSha256) { throw 'The proof manifest changed; preserve it for inspection.' }
Import-Module (Join-Path $PSHOME 'Modules\Appx\Appx.psd1')
if (Get-AppxPackage -Name JPSoftworks.MediaControls.BridgeProof) { throw 'Uninstall the proof package before cleanup.' }
$paths = @('Software\Microsoft\Edge\NativeMessagingHosts\com.jpsoftworks.media_controls_lifecycle',
    'Software\Google\Chrome\NativeMessagingHosts\com.jpsoftworks.media_controls_lifecycle')
foreach ($path in $paths) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($path)
    try {
        if ($null -eq $key -or $key.GetValue('') -ne $manifest -or $key.SubKeyCount -ne 0 -or $key.ValueCount -ne 1) { throw "Proof registry key changed: $path" }
    }
    finally { if ($null -ne $key) { $key.Dispose() } }
}
foreach ($path in $paths) { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($path, $true) }
Remove-Item -LiteralPath $manifest
if ([IO.Directory]::GetFileSystemEntries($directory).Length -ne 0) { throw 'Unexpected files remain in the proof directory.' }
[IO.Directory]::Delete($directory, $false)
if ([IO.Directory]::GetFileSystemEntries($proofBase).Length -eq 0) { [IO.Directory]::Delete($proofBase, $false) }
$remainingKeys = 0
foreach ($path in $paths) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($path)
    if ($null -ne $key) { $remainingKeys++; $key.Dispose() }
}
[ordered]@{ manualCleanup = $true; remainingKeys = $remainingKeys; externalManifestExists = Test-Path -LiteralPath $manifest; packageIdentityStatus = $identityStatus; observedUtc = [DateTime]::UtcNow.ToString('o') } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'manual-cleanup.json')
