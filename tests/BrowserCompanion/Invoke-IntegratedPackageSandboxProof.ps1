[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Baseline', 'Install', 'Register', 'Upgrade', 'Remove', 'Uninstall', 'Cleanup', 'Capture')][string] $Phase,
    [ValidatePattern('^[a-z0-9-]*$')][string] $Label = '',
    [string] $InputDirectory = 'C:\MediaControlsProofInput',
    [string] $ResultDirectory = 'C:\MediaControlsProofResults',
    [switch] $Firefox
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Run only inside Windows Sandbox.' }
$name = 'JiriPolasek.MediaControlsForCmdPal'
$alias = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\MediaControlsBrowserBridge.exe'
$nativePath = Join-Path $env:LOCALAPPDATA 'JPSoftworks\MediaControls\BrowserCompanion\native-host.json'
if ($Firefox) { $nativePath = Join-Path (Split-Path -Parent $nativePath) 'firefox-native-host.json' }
$thumbprint = 'F286A14450E9C8F242DB64FFB570EE7314CF46C0'
$id = 'bfclnkfiooaolpmbhpnmhmjlibkeacge'

function Write-Result([string] $Name, $Value) {
    if ($Label) { $Name = "$Label-$Name" }
    [IO.File]::WriteAllText((Join-Path $ResultDirectory $Name), ($Value | ConvertTo-Json -Depth 16) + "`r`n", [Text.UTF8Encoding]::new($false))
}

function Read-State {
    $package = Get-AppxPackage -Name $name
    $browsers = if ($Firefox) { @('Mozilla') } else { @('Microsoft\Edge', 'Google\Chrome') }
    $keys = foreach ($browser in $browsers) {
        foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
            $root = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
            $key = $null
            try {
                $key = $root.OpenSubKey("Software\$browser\NativeMessagingHosts\com.jiripolasek.media_controls")
                [ordered]@{ browser = $browser; view = $view.ToString(); value = if ($null -ne $key) { $key.GetValue('') } else { $null } }
            }
            finally { if ($null -ne $key) { $key.Dispose() }; $root.Dispose() }
        }
    }
    $native = if (Test-Path -LiteralPath $nativePath) { Get-Content -LiteralPath $nativePath -Raw | ConvertFrom-Json } else { $null }
    $processes = @(Get-Process -Name JPSoftworks.MediaControlsExtension.BrowserCompanion -ErrorAction SilentlyContinue | Select-Object Id,Path)
    return [ordered]@{
        utc = [DateTime]::UtcNow.ToString('o'); user = [Environment]::UserName; machine = [Environment]::MachineName
        package = if ($null -ne $package) { $package.PackageFullName } else { $null }
        location = if ($null -ne $package) { $package.InstallLocation } else { $null }
        aliasExists = Test-Path -LiteralPath $alias; manifest = $native; registrations = @($keys); processes = $processes
        manifestHash = if (Test-Path -LiteralPath $nativePath) { (Get-FileHash -LiteralPath $nativePath).Hash } else { $null }
        firefoxLauncherHash = if (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $nativePath) 'firefox-bridge.cmd')) {
            (Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $nativePath) 'firefox-bridge.cmd')).Hash
        } else { $null }
        trustedCertificatePresent = Test-Path "Cert:\LocalMachine\TrustedPeople\$thumbprint"
    }
}

try {
    switch ($Phase) {
        Capture { Write-Result 'state.json' (Read-State) }
        Baseline {
            $state = Read-State
            if ($null -ne $state.package -or $state.aliasExists -or $null -ne $state.manifest -or
                @($state.registrations | Where-Object { $null -ne $_.value }).Count -ne 0) { throw 'The sandbox is not clean for this package.' }
            if (Test-Path "Cert:\LocalMachine\TrustedPeople\$thumbprint") { throw 'The test certificate is already trusted.' }
            Write-Result 'baseline.json' $state
        }
        Install {
            if (!(Test-Path (Join-Path $ResultDirectory 'baseline.json'))) { throw 'Capture a clean baseline first.' }
            Import-Certificate -FilePath (Join-Path $InputDirectory 'proof-signing.cer') -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
            Add-AppxPackage -Path (Join-Path $InputDirectory 'MediaControls-v1.msix')
            Write-Result 'installed.json' (Read-State)
        }
        Register {
            $output = if ($Firefox) { & $alias --register-browser-integration 2>&1 | Out-String }
                else { & $alias --register-browser-integration $id 2>&1 | Out-String }
            $code = $LASTEXITCODE
            Write-Result 'register.json' @{ output = $output; exitCode = $code; state = Read-State }
            if ($code -ne 0) { throw $output }
        }
        Upgrade {
            Write-Result 'before-upgrade.json' (Read-State)
            Add-AppxPackage -Path (Join-Path $InputDirectory 'MediaControls-v2.msix') -ForceApplicationShutdown
            Write-Result 'upgraded.json' (Read-State)
        }
        Remove {
            $output = & $alias --remove-browser-integration 2>&1 | Out-String
            $code = $LASTEXITCODE
            Write-Result 'removed.json' @{ output = $output; exitCode = $code; state = Read-State }
            if ($code -ne 0) { throw $output }
        }
        Uninstall {
            Write-Result 'before-uninstall.json' (Read-State)
            $package = Get-AppxPackage -Name $name
            if ($null -eq $package) { throw 'Expected the test package to be installed.' }
            Remove-AppxPackage -Package $package.PackageFullName
            Write-Result 'uninstalled.json' (Read-State)
        }
        Cleanup {
            if (Get-AppxPackage -Name $name) { throw 'Uninstall the test package before cleanup.' }
            Import-Module (Join-Path $InputDirectory 'PackagedRegistration.psm1') -Force
            Remove-PackagedCompanionRegistration -Directory (Split-Path -Parent $nativePath)
            if (Test-Path "Cert:\LocalMachine\TrustedPeople\$thumbprint") { Remove-Item "Cert:\LocalMachine\TrustedPeople\$thumbprint" }
            Write-Result 'cleaned.json' (Read-State)
        }
    }
}
catch {
    Write-Result "$Phase-error.json" @{ message = $_.Exception.ToString(); stack = $_.ScriptStackTrace }
    exit 1
}
