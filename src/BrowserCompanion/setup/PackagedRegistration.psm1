Set-StrictMode -Version Latest
$script:RegistrationRoot = 'HKCU:\Software'
$script:HostName = 'com.jiripolasek.media_controls'
$script:LegacyHostName = 'com.jpsoftworks.media_controls_spike'

function Get-PackagedCompanionKeys([string] $Directory, [string] $HostName = $script:HostName) {
    @(
        @{ Key = "$script:RegistrationRoot\Microsoft\Edge\NativeMessagingHosts\$HostName"; Manifest = (Join-Path $Directory 'native-host.json') },
        @{ Key = "$script:RegistrationRoot\Google\Chrome\NativeMessagingHosts\$HostName"; Manifest = (Join-Path $Directory 'native-host.json') },
        @{ Key = "$script:RegistrationRoot\Mozilla\NativeMessagingHosts\$HostName"; Manifest = (Join-Path $Directory 'firefox-native-host.json') }
    )
}

function Remove-OwnedCompanionKey($Registration) {
    if (Test-Path -LiteralPath $Registration.Key) {
        $key = Get-Item -LiteralPath $Registration.Key
        try { $owned = $key.GetValue('') -eq $Registration.Manifest }
        finally { $key.Dispose() }
        if ($owned) { Remove-Item -LiteralPath $Registration.Key -Force }
    }
}

function Write-CompanionFile([string] $Path, [string] $Content) {
    $temporary = Join-Path (Split-Path -Parent $Path) ([Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($temporary, $Content, [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temporary, $Path, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $Path) }
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}

function Set-PackagedCompanionRegistration {
    param(
        [Parameter(Mandatory)][string] $Directory,
        [Parameter(Mandatory)][string] $AliasPath,
        [ValidateCount(0, 16)][ValidatePattern('^[a-p]{32}$')][string[]] $ExtensionId = @(),
        [ValidateCount(0, 16)][ValidatePattern('^[a-zA-Z0-9._-]{1,64}@[a-zA-Z0-9.-]{1,64}$')][string[]] $FirefoxExtensionId = @()
    )

    if ($ExtensionId.Count -eq 0 -and $FirefoxExtensionId.Count -eq 0) { throw 'At least one extension ID is required.' }
    if (!(Test-Path -LiteralPath $AliasPath -PathType Leaf)) { throw 'Enable the Media Controls browser bridge in Windows App execution aliases, then try again.' }
    $directoryPath = [IO.Path]::GetFullPath($Directory).TrimEnd('\') + '\'
    $aliasFullPath = [IO.Path]::GetFullPath($AliasPath)
    $launcher = Join-Path $Directory 'firefox-bridge.cmd'
    if ($FirefoxExtensionId.Count -gt 0) {
        if ([IO.Path]::GetPathRoot($directoryPath) -ne [IO.Path]::GetPathRoot($aliasFullPath)) { throw 'The Firefox launcher and app alias must be on the same volume.' }
        $relative = [Uri]::UnescapeDataString(([Uri] $directoryPath).MakeRelativeUri([Uri] $aliasFullPath).ToString()).Replace('/', '\')
        if ($relative -match '[^\x20-\x7e]|"') { throw 'The Firefox launcher requires an ASCII relative alias path.' }
        # Firefox rejects app alias reparse points; cmd resolves the same packaged alias.
        $launcherText = '@echo off' + "`r`n" + 'setlocal DisableDelayedExpansion' + "`r`n" + '"%~dp0' + $relative.Replace('%', '%%') + '" %*' + "`r`n"
    }
    [IO.Directory]::CreateDirectory($Directory) | Out-Null
    $keys = @(Get-PackagedCompanionKeys $Directory)
    foreach ($file in @('native-host.json', 'firefox-native-host.json')) {
        $firefox = $file -eq 'firefox-native-host.json'
        $ids = @($(if ($firefox) { $FirefoxExtensionId | Sort-Object -Unique }
            else { $ExtensionId | Sort-Object -Unique | ForEach-Object { "chrome-extension://$_/" } }))
        $manifestPath = Join-Path $Directory $file
        $registrations = @($keys | Where-Object Manifest -eq $manifestPath)
        if ($ids.Count -eq 0) {
            foreach ($registration in $registrations) { Remove-OwnedCompanionKey $registration }
            if (Test-Path -LiteralPath $manifestPath) { Remove-Item -LiteralPath $manifestPath -Force }
            if ($firefox -and (Test-Path -LiteralPath $launcher)) { Remove-Item -LiteralPath $launcher -Force }
            continue
        }
        if ($firefox) { Write-CompanionFile $launcher $launcherText }
        $manifest = [ordered]@{ name = $script:HostName; description = 'Media Controls browser companion'; path = $(if ($firefox) { $launcher } else { $AliasPath }); type = 'stdio' }
        $manifest[$(if ($firefox) { 'allowed_extensions' } else { 'allowed_origins' })] = @($ids)
        Write-CompanionFile $manifestPath (($manifest | ConvertTo-Json) + "`r`n")
        foreach ($registration in $registrations) {
            New-Item -Path $registration.Key -Force | Out-Null
            Set-Item -LiteralPath $registration.Key -Value $manifestPath
        }
    }
    foreach ($registration in @(Get-PackagedCompanionKeys $Directory $script:LegacyHostName)) { Remove-OwnedCompanionKey $registration }
}

function Remove-PackagedCompanionRegistration {
    param([Parameter(Mandatory)][string] $Directory)

    $keys = @(Get-PackagedCompanionKeys $Directory) + @(Get-PackagedCompanionKeys $Directory $script:LegacyHostName)
    foreach ($registration in $keys) { Remove-OwnedCompanionKey $registration }
    foreach ($manifestPath in @($keys.Manifest | Sort-Object -Unique) + @(Join-Path $Directory 'firefox-bridge.cmd')) {
        if (Test-Path -LiteralPath $manifestPath) { Remove-Item -LiteralPath $manifestPath -Force }
    }
}

Export-ModuleMember -Function Set-PackagedCompanionRegistration, Remove-PackagedCompanionRegistration
