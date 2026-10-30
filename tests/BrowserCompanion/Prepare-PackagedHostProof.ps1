[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $HostExecutable,
    [string] $OutputDirectory = 'artifacts\BrowserCompanion\package-lifecycle',
    [ValidateSet('AppRegistration', 'Declarative', 'ExternalHelper')][string] $RegistrationMode = 'Declarative',
    [switch] $EnableDevelopmentCapability
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($EnableDevelopmentCapability -and $RegistrationMode -ne 'Declarative') { throw 'Only the declarative proof uses a development capability.' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
if (Test-Path -LiteralPath $output) { throw "Choose a new proof directory: $output" }
$hostFile = (Resolve-Path -LiteralPath $HostExecutable).Path
$utf8 = [Text.UTF8Encoding]::new($false)

function Write-ProofFile([string] $Path, [string] $Text) {
    [IO.File]::WriteAllText($Path, ($Text -replace '\r?\n', "`r`n").TrimEnd() + "`r`n", $utf8)
}

[IO.Directory]::CreateDirectory($output) | Out-Null
$extensionRoot = Join-Path $output 'extension'
Copy-Item -LiteralPath (Join-Path $repoRoot 'src\BrowserCompanion\extension') -Destination $extensionRoot -Recurse
$rsa = [Security.Cryptography.RSA]::Create(2048)
try { $publicKey = $rsa.ExportSubjectPublicKeyInfo() }
finally { $rsa.Dispose() }
$digest = [Security.Cryptography.SHA256]::HashData($publicKey)
$extensionId = -join ($digest[0..15] | ForEach-Object { [char](97 + ($_ -shr 4)); [char](97 + ($_ -band 15)) })
$extensionManifestPath = Join-Path $extensionRoot 'manifest.json'
$extensionManifest = Get-Content -LiteralPath $extensionManifestPath -Raw | ConvertFrom-Json
$extensionManifest | Add-Member key ([Convert]::ToBase64String($publicKey))
$extensionManifest.name = 'Media Controls lifecycle proof'
Write-ProofFile $extensionManifestPath ($extensionManifest | ConvertTo-Json -Depth 10)
$backgroundPath = Join-Path $extensionRoot 'background.js'
$nativeHostName = 'com.jpsoftworks.media_controls_lifecycle'
$background = [IO.File]::ReadAllText($backgroundPath)
if (-not $background.Contains('com.jiripolasek.media_controls')) { throw 'Cannot isolate the extension native host name.' }
$background = $background.Replace('com.jiripolasek.media_controls', $nativeHostName)
Write-ProofFile $backgroundPath $background

foreach ($version in @('0.1.0.0', '0.2.0.0')) {
    $layout = Join-Path $output $version
    [IO.Directory]::CreateDirectory((Join-Path $layout 'Assets')) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $layout 'BrowserCompanion')) | Out-Null
    Copy-Item -LiteralPath $hostFile -Destination (Join-Path $layout 'BrowserCompanion\BrowserCompanion.exe')
    foreach ($asset in @('StoreLogo', 'Square150x150Logo', 'Square44x44Logo')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot "src\MediaControlsExtension.Package\Assets\$asset.scale-100.png") -Destination (Join-Path $layout "Assets\$asset.png")
    }
    $nativeManifest = [ordered]@{
        name = $nativeHostName
        description = 'Media Controls packaged bridge lifecycle proof'
        path = 'BrowserCompanion.exe'
        type = 'stdio'
        allowed_origins = @("chrome-extension://$extensionId/")
    }
    Write-ProofFile (Join-Path $layout 'BrowserCompanion\native-host.json') ($nativeManifest | ConvertTo-Json)
    $manifest = @'
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  xmlns:rescap4="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities/4"
  xmlns:uap4="http://schemas.microsoft.com/appx/manifest/uap/windows10/4"
  xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
  xmlns:desktop4="http://schemas.microsoft.com/appx/manifest/desktop/windows10/4"
  xmlns:virtualization="http://schemas.microsoft.com/appx/manifest/virtualization/windows10"
  IgnorableNamespaces="uap uap4 uap5 desktop4 rescap rescap4 virtualization">
  <Identity Name="JPSoftworks.MediaControls.BridgeProof" Publisher="CN=735F2779-478E-4220-AA28-465CBADFF852" Version="__VERSION__" ProcessorArchitecture="x64" />
  <Properties>
    <DisplayName>Media Controls bridge lifecycle proof</DisplayName>
    <PublisherDisplayName>JPSoftworks</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" /></Dependencies>
  <Resources><Resource Language="en-US" /></Resources>
  <Applications>
    <Application Id="BrowserBridge" Executable="BrowserCompanion\BrowserCompanion.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="Media Controls bridge proof" Description="Isolated packaged native host" BackgroundColor="transparent"
        Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png" AppListEntry="none" />
    </Application>
  </Applications>
  <Extensions>
    <rescap4:Extension Category="windows.classicAppCompatKeys" EntryPoint="Windows.FullTrustApplication">
      <rescap4:ClassicAppCompatKeys>
        <rescap4:ClassicAppCompatKey Name="HKCU\Software\Microsoft\Edge\NativeMessagingHosts\__HOST__" ValueName="" ValueType="REG_SZ" Value="[{AppVPackageRoot}]\BrowserCompanion\native-host.json" />
        <rescap4:ClassicAppCompatKey Name="HKCU\Software\Google\Chrome\NativeMessagingHosts\__HOST__" ValueName="" ValueType="REG_SZ" Value="[{AppVPackageRoot}]\BrowserCompanion\native-host.json" />
      </rescap4:ClassicAppCompatKeys>
    </rescap4:Extension>
  </Extensions>
  <Capabilities><rescap:Capability Name="runFullTrust" /></Capabilities>
</Package>
'@
    if ($RegistrationMode -in @('AppRegistration', 'ExternalHelper')) {
        $manifest = $manifest -replace '(?s)  <Extensions>.*?</Extensions>\r?\n', ''
    }
    if ($RegistrationMode -eq 'ExternalHelper') {
        $manifest = $manifest.Replace('<Application Id="BrowserBridge"', '<Application Id="BrowserBridge" desktop4:SupportsMultipleInstances="true"')
        $alias = @'
      <Extensions>
        <uap5:Extension Category="windows.appExecutionAlias" EntryPoint="Windows.FullTrustApplication">
          <uap5:AppExecutionAlias desktop4:Subsystem="console">
            <uap5:ExecutionAlias Alias="MediaControlsBridgeProof.exe" />
          </uap5:AppExecutionAlias>
        </uap5:Extension>
      </Extensions>
'@
        $manifest = $manifest.Replace('    </Application>', "$alias`r`n    </Application>")
        $helperDirectory = Join-Path $layout 'ProofHelper'
        [IO.Directory]::CreateDirectory($helperDirectory) | Out-Null
        foreach ($helper in @('Invoke-PackagedHostHelperProof.ps1', 'Register-ExternalHostProof.ps1')) {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot $helper) -Destination $helperDirectory
        }
    }
    elseif ($RegistrationMode -eq 'AppRegistration') {
        $exclusions = @'
    <virtualization:RegistryWriteVirtualization>
      <virtualization:ExcludedKeys>
        <virtualization:ExcludedKey>HKEY_CURRENT_USER\Software\Microsoft\Edge\NativeMessagingHosts\__HOST__</virtualization:ExcludedKey>
        <virtualization:ExcludedKey>HKEY_CURRENT_USER\Software\Google\Chrome\NativeMessagingHosts\__HOST__</virtualization:ExcludedKey>
      </virtualization:ExcludedKeys>
    </virtualization:RegistryWriteVirtualization>
'@
        $manifest = $manifest.Replace('  </Properties>', "$exclusions`r`n  </Properties>")
        $manifest = $manifest.Replace('<rescap:Capability Name="runFullTrust" />', '<rescap:Capability Name="runFullTrust" /><rescap:Capability Name="unvirtualizedResources" />')
    }
    elseif ($EnableDevelopmentCapability) {
        $manifest = $manifest.Replace('<rescap:Capability Name="runFullTrust" />', '<rescap:Capability Name="runFullTrust" /><uap4:CustomCapability Name="Microsoft.classicAppCompat_8wekyb3d8bbwe" />')
        $descriptor = @'
<?xml version="1.0" encoding="utf-8"?>
<!-- Developer-mode test descriptor. Never ship this file. -->
<CustomCapabilityDescriptor xmlns="http://schemas.microsoft.com/appx/2016/sccd">
  <CustomCapabilities><CustomCapability Name="Microsoft.classicAppCompat_8wekyb3d8bbwe" /></CustomCapabilities>
  <AuthorizedEntities><AuthorizedEntity AppPackageFamilyName="JPSoftworks.MediaControls.BridgeProof_15b1mzc2p8w76" CertificateSignatureHash="0000000000000000000000000000000000000000000000000000000000000000" /></AuthorizedEntities>
  <Catalog>0000</Catalog>
  <DeveloperModeOnly Value="true" />
</CustomCapabilityDescriptor>
'@
        Write-ProofFile (Join-Path $layout 'development-only.sccd') $descriptor
    }
    Write-ProofFile (Join-Path $layout 'AppxManifest.xml') $manifest.Replace('__VERSION__', $version).Replace('__HOST__', $nativeHostName)
}

$instance = [Guid]::NewGuid().ToString()
$browserOutput = Join-Path $repoRoot ('output\playwright\' + (Split-Path -Leaf $output))
[IO.Directory]::CreateDirectory($browserOutput) | Out-Null
$configuration = [ordered]@{
    browser = [ordered]@{
        browserName = 'chromium'
        userDataDir = Join-Path $browserOutput 'edge-profile'
        launchOptions = [ordered]@{
            executablePath = 'C:\Program Files (x86)\Microsoft\Edge Dev\Application\msedge.exe'
            headless = $true
            ignoreDefaultArgs = @('--disable-extensions')
            args = @("--disable-extensions-except=$extensionRoot", "--load-extension=$extensionRoot")
        }
    }
    outputDir = $browserOutput
}
Write-ProofFile (Join-Path $output 'edge.config.json') ($configuration | ConvertTo-Json -Depth 8)
$configuration.browser.userDataDir = Join-Path $browserOutput 'chrome-profile'
$configuration.browser.launchOptions.executablePath = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
$configuration.browser.launchOptions.args = @('--enable-unsafe-extension-debugging')
Write-ProofFile (Join-Path $output 'chrome.config.json') ($configuration | ConvertTo-Json -Depth 8)
$state = [ordered]@{
    packageName = 'JPSoftworks.MediaControls.BridgeProof'
    nativeHostName = $nativeHostName
    extensionId = $extensionId
    instance = $instance
    sourceHost = $hostFile
    sourceHostSha256 = (Get-FileHash -LiteralPath $hostFile -Algorithm SHA256).Hash
    registrationMode = $RegistrationMode
    executionAlias = if ($RegistrationMode -eq 'ExternalHelper') { 'MediaControlsBridgeProof.exe' } else { $null }
    developmentCapability = [bool] $EnableDevelopmentCapability
    outputDirectory = $output
    browserOutputDirectory = $browserOutput
}
Write-ProofFile (Join-Path $output 'state.json') ($state | ConvertTo-Json)
$state | ConvertTo-Json
