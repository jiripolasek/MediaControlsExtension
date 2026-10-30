Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:RegistrationRoot = 'HKCU:\Software'
$script:HostName = 'com.jiripolasek.media_controls'
$script:LegacyHostName = 'com.jpsoftworks.media_controls_spike'

function Get-RegistrationKeys {
    param([string] $Browser, [string] $HostName = $script:HostName)
    if ($Browser -in @('Edge', 'Both')) { "$script:RegistrationRoot\Microsoft\Edge\NativeMessagingHosts\$HostName" }
    if ($Browser -in @('Chrome', 'Both')) { "$script:RegistrationRoot\Google\Chrome\NativeMessagingHosts\$HostName" }
}

function Remove-OwnedCompanionRegistration([string] $Key, [string] $ManifestPath) {
    if (Test-Path -LiteralPath $Key) {
        $registration = Get-Item -LiteralPath $Key
        try { $owned = $registration.GetValue('') -eq $ManifestPath }
        finally { $registration.Dispose() }
        if ($owned) { Remove-Item -LiteralPath $Key }
    }
}

function Set-CompanionRegistration {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $HostDirectory,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][ValidatePattern('^[a-p]{32}$')][string[]] $ExtensionId,
        [ValidateSet('Edge', 'Chrome', 'Both')][string] $Browser = 'Edge'
    )
    $hostRoot = [IO.Path]::GetFullPath($HostDirectory)
    $executable = Join-Path $hostRoot 'JPSoftworks.MediaControlsExtension.BrowserCompanion.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Native host not found: $executable" }
    $manifestPath = Join-Path $hostRoot 'native-host.json'
    $origins = @($ExtensionId | ForEach-Object { "chrome-extension://$_/" })
    if (Test-Path -LiteralPath $manifestPath) {
        $existing = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($existing.name -notin @($script:HostName, $script:LegacyHostName)) { throw 'The existing manifest belongs to another native host.' }
        $origins += @($existing.allowed_origins | Where-Object { $_ -cmatch '^chrome-extension://[a-p]{32}/$' })
    }
    $manifest = [ordered]@{
        name = $script:HostName
        description = 'Media Controls browser companion'
        path = $executable
        type = 'stdio'
        allowed_origins = @($origins | Sort-Object -Unique)
    }
    $temporary = "$manifestPath.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporary, ($manifest | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $manifestPath) { [IO.File]::Replace($temporary, $manifestPath, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $manifestPath) }
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    }
    foreach ($key in @(Get-RegistrationKeys $Browser)) {
        New-Item -Path $key -Force | Out-Null
        Set-Item -LiteralPath $key -Value $manifestPath
    }
    foreach ($key in @(Get-RegistrationKeys $Browser $script:LegacyHostName)) { Remove-OwnedCompanionRegistration $key $manifestPath }
    Write-Host "Registered the companion for $Browser. Reload the browser extension to connect."
}

function Remove-CompanionRegistration {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $HostDirectory,
        [ValidateSet('Edge', 'Chrome', 'Both')][string] $Browser = 'Edge'
    )
    $manifestPath = Join-Path ([IO.Path]::GetFullPath($HostDirectory)) 'native-host.json'
    foreach ($key in (@(Get-RegistrationKeys $Browser) + @(Get-RegistrationKeys $Browser $script:LegacyHostName))) {
        Remove-OwnedCompanionRegistration $key $manifestPath
    }
    Write-Host "Removed registrations for $Browser that belonged to this copy of the companion."
}

Export-ModuleMember -Function Set-CompanionRegistration, Remove-CompanionRegistration
