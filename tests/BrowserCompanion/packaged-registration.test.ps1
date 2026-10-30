[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$id = [Guid]::NewGuid().ToString('N')
$directory = Join-Path $repoRoot "artifacts\BrowserCompanion\tests\packaged-registration-$id"
$registryRoot = "HKCU:\Software\JPSoftworks\CompanionSetupTests\$id"
$module = Import-Module (Join-Path $repoRoot 'src\BrowserCompanion\setup\PackagedRegistration.psm1') -Force -PassThru
& $module { param($root) $script:RegistrationRoot = $root } $registryRoot
$edgeKey = "$registryRoot\Microsoft\Edge\NativeMessagingHosts\com.jiripolasek.media_controls"
$chromeKey = "$registryRoot\Google\Chrome\NativeMessagingHosts\com.jiripolasek.media_controls"
$firefoxKey = "$registryRoot\Mozilla\NativeMessagingHosts\com.jiripolasek.media_controls"
$firefoxId = 'media-controls-companion@jiripolasek.com'
$legacyEdgeKey = "$registryRoot\Microsoft\Edge\NativeMessagingHosts\com.jpsoftworks.media_controls_spike"
$legacyChromeKey = "$registryRoot\Google\Chrome\NativeMessagingHosts\com.jpsoftworks.media_controls_spike"
$legacyFirefoxKey = "$registryRoot\Mozilla\NativeMessagingHosts\com.jpsoftworks.media_controls_spike"
$checks = 0
function Assert-True([bool] $Condition, [string] $Name) {
    if (!$Condition) { throw $Name }
    $script:checks++
    Write-Host "PASS: $Name"
}
try {
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $alias = Join-Path $directory 'MediaControlsBrowserBridge.exe'
    [IO.File]::WriteAllText($alias, 'fixture')
    $manifestPath = Join-Path $directory 'native-host.json'
    $rejected = $false
    try { Set-PackagedCompanionRegistration -Directory $directory -AliasPath $alias -ExtensionId '*' }
    catch [Management.Automation.ParameterBindingException] { $rejected = $true }
    Assert-True ($rejected -and !(Test-Path $manifestPath) -and !(Test-Path $edgeKey)) 'Rejects wildcard origins before writing anything'
    Set-PackagedCompanionRegistration -Directory $directory -AliasPath $alias -ExtensionId @(('a' * 32), ('b' * 32), ('a' * 32))
    $native = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    Assert-True ($native.allowed_origins.Count -eq 2 -and $native.path -eq $alias) 'Registers exact unique origins and the stable alias'
    Assert-True ((Get-Item $edgeKey).GetValue('') -eq $manifestPath -and (Get-Item $chromeKey).GetValue('') -eq $manifestPath) 'Registers both browsers'
    Set-PackagedCompanionRegistration -Directory $directory -AliasPath $alias -ExtensionId ('c' * 32)
    $native = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    Assert-True (@($native.allowed_origins).Count -eq 1 -and $native.allowed_origins[0] -eq "chrome-extension://$('c' * 32)/") 'Reconfiguration replaces previously authorized IDs'
    Set-Item -LiteralPath $edgeKey -Value 'C:\another-installation\native-host.json'
    Remove-PackagedCompanionRegistration -Directory $directory
    Assert-True ((Get-Item $edgeKey).GetValue('') -eq 'C:\another-installation\native-host.json') 'Removal preserves another installation registration'
    Assert-True (!(Test-Path $chromeKey) -and !(Test-Path $manifestPath)) 'Removal deletes owned registration and its external manifest'
    Remove-PackagedCompanionRegistration -Directory $directory
    Assert-True (!(Test-Path $chromeKey) -and !(Test-Path $manifestPath)) 'Repeated removal succeeds'
    Assert-True (@(Get-ChildItem -LiteralPath $directory -Filter '*.tmp').Count -eq 0) 'Leaves no temporary manifests'
    $firefoxManifest = Join-Path $directory 'firefox-native-host.json'
    $launcher = Join-Path $directory 'firefox-bridge.cmd'
    $rejected = $false
    try { Set-PackagedCompanionRegistration -Directory $directory -AliasPath $alias -FirefoxExtensionId '*' }
    catch [Management.Automation.ParameterBindingException] { $rejected = $true }
    Assert-True ($rejected -and !(Test-Path $firefoxManifest)) 'Rejects wildcard Firefox IDs before registration'
    Set-PackagedCompanionRegistration -Directory $directory -AliasPath $alias -FirefoxExtensionId $firefoxId
    $native = Get-Content -LiteralPath $firefoxManifest -Raw | ConvertFrom-Json
    Assert-True ($native.allowed_extensions[0] -eq $firefoxId -and $native.path -eq $launcher -and
        (Get-Item $firefoxKey).GetValue('') -eq $firefoxManifest -and !(Test-Path $manifestPath)) 'Firefox alone uses its own manifest and the packaged alias'
    Assert-True ((Get-Content -LiteralPath $launcher -Raw) -eq ('@echo off' + "`r`n" + 'setlocal DisableDelayedExpansion' + "`r`n" +
        '"%~dp0MediaControlsBrowserBridge.exe" %*' + "`r`n")) 'Firefox launcher preserves native arguments and forwards to the app alias'
    Set-PackagedCompanionRegistration -Directory $directory -AliasPath $alias -ExtensionId ('a' * 32) -FirefoxExtensionId $firefoxId
    Assert-True ((Test-Path $manifestPath) -and (Test-Path $firefoxManifest) -and (Test-Path $chromeKey)) 'Chromium and Firefox registration coexist'
    Set-Item -LiteralPath $firefoxKey -Value 'C:\another-installation\firefox-native-host.json'
    Remove-PackagedCompanionRegistration -Directory $directory
    Assert-True ((Get-Item $firefoxKey).GetValue('') -eq 'C:\another-installation\firefox-native-host.json' -and
        !(Test-Path $manifestPath) -and !(Test-Path $firefoxManifest)) 'Firefox removal preserves another installation registration'
    Set-PackagedCompanionRegistration -Directory $directory -AliasPath $alias -FirefoxExtensionId $firefoxId
    Remove-PackagedCompanionRegistration -Directory $directory
    Assert-True (!(Test-Path $firefoxKey) -and !(Test-Path $firefoxManifest) -and !(Test-Path $launcher)) 'Removal deletes owned Firefox registration, manifest and launcher'
    [IO.File]::WriteAllText($manifestPath, (@{ name = 'com.jpsoftworks.media_controls_spike';
        allowed_origins = @("chrome-extension://$('b' * 32)/") } | ConvertTo-Json))
    [IO.File]::WriteAllText($firefoxManifest, (@{ name = 'com.jpsoftworks.media_controls_spike';
        allowed_extensions = @('media-controls-companion@jpsoftworks.com') } | ConvertTo-Json))
    foreach ($legacy in @(@{ Key = $legacyEdgeKey; Manifest = $manifestPath },
            @{ Key = $legacyChromeKey; Manifest = 'C:\another-installation\native-host.json' },
            @{ Key = $legacyFirefoxKey; Manifest = $firefoxManifest })) {
        New-Item -Path $legacy.Key -Force | Out-Null
        Set-Item -LiteralPath $legacy.Key -Value $legacy.Manifest
    }
    Set-PackagedCompanionRegistration -Directory $directory -AliasPath $alias -ExtensionId ('a' * 32) -FirefoxExtensionId $firefoxId
    $native = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $firefoxNative = Get-Content -LiteralPath $firefoxManifest -Raw | ConvertFrom-Json
    Assert-True ($native.name -eq 'com.jiripolasek.media_controls' -and
        $firefoxNative.name -eq $native.name -and $firefoxNative.allowed_extensions[0] -eq $firefoxId -and
        !(Test-Path -LiteralPath $legacyEdgeKey) -and !(Test-Path -LiteralPath $legacyFirefoxKey) -and
        (Get-Item -LiteralPath $edgeKey).GetValue('') -eq $manifestPath -and
        (Get-Item -LiteralPath $firefoxKey).GetValue('') -eq $firefoxManifest) 'Migrates owned Chromium and Firefox registrations to the personal domain'
    Assert-True ((Get-Item -LiteralPath $legacyChromeKey).GetValue('') -eq 'C:\another-installation\native-host.json') 'Migration preserves foreign legacy registrations'
    foreach ($legacy in @(@{ Key = $legacyEdgeKey; Manifest = $manifestPath }, @{ Key = $legacyFirefoxKey; Manifest = $firefoxManifest })) {
        New-Item -Path $legacy.Key -Force | Out-Null
        Set-Item -LiteralPath $legacy.Key -Value $legacy.Manifest
    }
    Remove-PackagedCompanionRegistration -Directory $directory
    Assert-True (!(Test-Path -LiteralPath $legacyEdgeKey) -and !(Test-Path -LiteralPath $legacyFirefoxKey) -and
        (Get-Item -LiteralPath $legacyChromeKey).GetValue('') -eq 'C:\another-installation\native-host.json' -and
        !(Test-Path -LiteralPath $manifestPath) -and !(Test-Path -LiteralPath $firefoxManifest)) 'Removal deletes owned legacy keys and preserves foreign keys'
    Write-Host "$checks packaged registration checks passed."
}
finally {
    Remove-Module $module.Name -Force
    if ($registryRoot -notmatch '^HKCU:\\Software\\JPSoftworks\\CompanionSetupTests\\[a-f0-9]{32}$') { throw 'Unsafe registry cleanup target.' }
    if (Test-Path -LiteralPath $registryRoot) { Remove-Item -LiteralPath $registryRoot -Recurse -Force }
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\BrowserCompanion\tests')) + '\'
    if (![IO.Path]::GetFullPath($directory).StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup target.' }
    if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
}
