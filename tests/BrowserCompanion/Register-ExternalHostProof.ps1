[CmdletBinding()]
param(
    [Parameter(Mandatory)][Guid] $Instance,
    [Parameter(Mandatory)][string] $PackageLocation,
    [Parameter(Mandatory)][string] $ResultPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
try {
    $packageType = [Windows.ApplicationModel.Package, Windows.ApplicationModel, ContentType = WindowsRuntime]
    $identityError = $null
    try { $currentPackage = $packageType::Current.Id.FullName }
    catch { $currentPackage = $null; $identityError = $_.Exception.Message }
    if ($null -ne $currentPackage) { throw "The helper is still packaged: $currentPackage" }
    [xml] $packageManifest = Get-Content -LiteralPath (Join-Path $PackageLocation 'AppxManifest.xml') -Raw
    if ($packageManifest.Package.Identity.Name -ne 'JPSoftworks.MediaControls.BridgeProof') { throw 'Unexpected proof package.' }
    $localData = [Environment]::GetFolderPath('LocalApplicationData')
    $directory = Join-Path $localData "JPSoftworks\BrowserCompanionProof\$Instance"
    if (Test-Path -LiteralPath $directory) { throw 'The isolated registration directory already exists.' }
    $alias = Join-Path $localData 'Microsoft\WindowsApps\MediaControlsBridgeProof.exe'
    if (!(Test-Path -LiteralPath $alias)) { throw 'The package execution alias is missing.' }
    $keys = @('Software\Microsoft\Edge\NativeMessagingHosts\com.jpsoftworks.media_controls_lifecycle',
        'Software\Google\Chrome\NativeMessagingHosts\com.jpsoftworks.media_controls_lifecycle')
    foreach ($path in $keys) {
        $existing = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($path)
        if ($null -ne $existing) { $existing.Dispose(); throw "Refusing to replace an existing proof key: $path" }
    }
    $native = Get-Content -LiteralPath (Join-Path $PackageLocation 'BrowserCompanion\native-host.json') -Raw | ConvertFrom-Json
    if ($native.name -ne 'com.jpsoftworks.media_controls_lifecycle') { throw 'Unexpected native host.' }
    $native.path = $alias
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $manifestPath = Join-Path $directory 'native-host.json'
    [IO.File]::WriteAllText($manifestPath, ($native | ConvertTo-Json) + "`r`n", [Text.UTF8Encoding]::new($false))
    foreach ($path in $keys) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($path)
        try { $key.SetValue('', $manifestPath, [Microsoft.Win32.RegistryValueKind]::String) }
        finally { $key.Dispose() }
    }
    $result = [ordered]@{ success = $true; package = $currentPackage; identityError = $identityError; processId = $PID; manifest = $manifestPath; alias = $alias; keys = $keys }
}
catch { $result = [ordered]@{ success = $false; error = $_.Exception.ToString(); processId = $PID } }
$temporaryResult = "$ResultPath.tmp"
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $temporaryResult
Move-Item -LiteralPath $temporaryResult -Destination $ResultPath
if (-not $result.success) { exit 1 }
