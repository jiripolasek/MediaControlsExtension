[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$id = [Guid]::NewGuid().ToString('N')
$testDirectory = Join-Path $repoRoot "artifacts\BrowserCompanion\tests\registration-$id"
$registryRoot = "HKCU:\Software\JPSoftworks\CompanionSetupTests\$id"
$module = Import-Module (Join-Path $repoRoot 'src\BrowserCompanion\setup\Registration.psm1') -Force -PassThru
& $module { param($root) $script:RegistrationRoot = $root } $registryRoot
$edgeKey = "$registryRoot\Microsoft\Edge\NativeMessagingHosts\com.jiripolasek.media_controls"
$chromeKey = "$registryRoot\Google\Chrome\NativeMessagingHosts\com.jiripolasek.media_controls"
$legacyEdgeKey = "$registryRoot\Microsoft\Edge\NativeMessagingHosts\com.jpsoftworks.media_controls_spike"
$legacyChromeKey = "$registryRoot\Google\Chrome\NativeMessagingHosts\com.jpsoftworks.media_controls_spike"
$report = [Collections.Generic.List[object]]::new()

function Assert-True {
    param([bool] $Condition, [string] $Name)
    if (-not $Condition) { throw $Name }
    $report.Add([pscustomobject]@{ name = $Name; passed = $true })
}

try {
    [IO.Directory]::CreateDirectory($testDirectory) | Out-Null
    [IO.File]::WriteAllText((Join-Path $testDirectory 'JPSoftworks.MediaControlsExtension.BrowserCompanion.exe'), 'fixture')
    $manifestPath = Join-Path $testDirectory 'native-host.json'
    [IO.File]::WriteAllText($manifestPath, (@{ name = 'com.jpsoftworks.media_controls_spike';
        allowed_origins = @("chrome-extension://$('b' * 32)/") } | ConvertTo-Json))
    New-Item -Path $legacyEdgeKey -Force | Out-Null
    Set-Item -LiteralPath $legacyEdgeKey -Value $manifestPath
    New-Item -Path $legacyChromeKey -Force | Out-Null
    Set-Item -LiteralPath $legacyChromeKey -Value 'C:\another-installation\native-host.json'
    Set-CompanionRegistration -HostDirectory $testDirectory -ExtensionId ('a' * 32) -Browser Edge
    $migrated = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    Assert-True ($migrated.name -eq 'com.jiripolasek.media_controls' -and
        $migrated.allowed_origins -contains "chrome-extension://$('b' * 32)/" -and
        !(Test-Path -LiteralPath $legacyEdgeKey)) 'Migrates an owned legacy host and preserves authorized extension IDs'
    Assert-True ((Get-Item -LiteralPath $edgeKey).GetValue('') -eq $manifestPath) 'Registers the exact manifest for the selected browser'
    Assert-True (-not (Test-Path -LiteralPath $chromeKey)) 'Does not register an unselected browser'
    Set-CompanionRegistration -HostDirectory $testDirectory -ExtensionId ('b' * 32) -Browser Chrome
    Set-CompanionRegistration -HostDirectory $testDirectory -ExtensionId ('a' * 32) -Browser Both
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    Assert-True ($manifest.allowed_origins.Count -eq 2) 'Preserves existing extension IDs without duplicates'
    Assert-True ($manifest.path -eq (Join-Path $testDirectory 'JPSoftworks.MediaControlsExtension.BrowserCompanion.exe')) 'Uses an absolute executable path'
    Assert-True ((Get-Item -LiteralPath $legacyChromeKey).GetValue('') -eq 'C:\another-installation\native-host.json') 'Migration preserves foreign legacy registrations'
    $originalManifest = [IO.File]::ReadAllText($manifestPath)
    $foreignManifest = $manifest | Select-Object *
    $foreignManifest.name = 'another.host'
    [IO.File]::WriteAllText($manifestPath, ($foreignManifest | ConvertTo-Json))
    $foreignText = [IO.File]::ReadAllText($manifestPath)
    $rejected = $false
    try { Set-CompanionRegistration -HostDirectory $testDirectory -ExtensionId ('d' * 32) -Browser Both }
    catch { $rejected = $true }
    Assert-True ($rejected -and [IO.File]::ReadAllText($manifestPath) -ceq $foreignText) 'Rejects unrelated host manifests without rewriting them'
    [IO.File]::WriteAllText($manifestPath, $originalManifest)
    $rejected = $false
    try { Set-CompanionRegistration -HostDirectory $testDirectory -ExtensionId 'invalid-id' }
    catch [Management.Automation.ParameterBindingException] { $rejected = $true }
    Assert-True $rejected 'Rejects invalid extension IDs before writing registration'
    Remove-CompanionRegistration -HostDirectory $testDirectory -Browser Chrome
    Assert-True ((Test-Path -LiteralPath $edgeKey) -and -not (Test-Path -LiteralPath $chromeKey)) 'Unregisters only the selected browser without requiring an ID'
    Set-Item -LiteralPath $edgeKey -Value 'C:\another-installation\native-host.json'
    Remove-CompanionRegistration -HostDirectory $testDirectory -Browser Both
    Assert-True ((Get-Item -LiteralPath $edgeKey).GetValue('') -eq 'C:\another-installation\native-host.json') 'Preserves registrations belonging to another installation'
    New-Item -Path $legacyEdgeKey -Force | Out-Null
    Set-Item -LiteralPath $legacyEdgeKey -Value $manifestPath
    Remove-CompanionRegistration -HostDirectory $testDirectory -Browser Both
    Assert-True (!(Test-Path -LiteralPath $legacyEdgeKey) -and
        (Get-Item -LiteralPath $legacyChromeKey).GetValue('') -eq 'C:\another-installation\native-host.json') 'Removal deletes owned legacy keys and preserves foreign keys'
    Assert-True (@(Get-ChildItem -LiteralPath $testDirectory -Filter '*.tmp').Count -eq 0) 'Leaves no partial manifest files'
    $report | ConvertTo-Json | Set-Content (Join-Path $repoRoot 'artifacts\BrowserCompanion\tests\registration-results.json') -Encoding utf8
    Write-Host "$($report.Count) registration checks passed."
}
finally {
    Remove-Module $module.Name -Force
    if ($registryRoot -notmatch '^HKCU:\\Software\\JPSoftworks\\CompanionSetupTests\\[a-f0-9]{32}$') { throw 'Unsafe registry cleanup target.' }
    if (Test-Path -LiteralPath $registryRoot) { Remove-Item -LiteralPath $registryRoot -Recurse -Force }
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\BrowserCompanion\tests')) + '\'
    if (-not [IO.Path]::GetFullPath($testDirectory).StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup target.' }
    if (Test-Path -LiteralPath $testDirectory) { Remove-Item -LiteralPath $testDirectory -Recurse -Force }
}
