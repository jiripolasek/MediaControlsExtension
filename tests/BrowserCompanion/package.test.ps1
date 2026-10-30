[CmdletBinding()]
param([Parameter(Mandatory)][string] $Archive)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$archivePath = (Resolve-Path -LiteralPath $Archive).Path
$report = [Collections.Generic.List[object]]::new()
function Assert-True {
    param([bool] $Condition, [string] $Name)
    if (-not $Condition) { throw $Name }
    $report.Add([pscustomobject]@{ name = $Name; passed = $true })
}
$zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $files = @($zip.Entries | Where-Object { $_.Name })
    Assert-True ($files.Count -gt 0) 'Archive contains release files'
    Assert-True (@($files | Where-Object { $_.FullName -match '(^[/\\]|(^|[/\\])\.\.([/\\]|$)|:)' }).Count -eq 0) 'Archive paths stay inside the extracted folder'
    $roots = @($files | ForEach-Object { ($_.FullName -replace '\\','/').Split('/')[0] } | Sort-Object -Unique)
    Assert-True ($roots.Count -eq 1) 'Archive extracts into one portable folder'
    $prefix = $roots[0] + '/'
    $entries = @{}
    foreach ($file in $files) { $entries[($file.FullName -replace '\\','/').Substring($prefix.Length)] = $file }
    function Read-Entry([string] $Name) {
        $reader = [IO.StreamReader]::new($entries[$Name].Open())
        try { $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    $manifest = Read-Entry 'extension/manifest.json' | ConvertFrom-Json
    $required = @('host/JPSoftworks.MediaControlsExtension.BrowserCompanion.exe', 'Setup.ps1', 'Registration.psm1', 'README.md', 'PRIVACY.md',
        'LICENSE.txt', 'licenses/dotnet/LICENSE.TXT', 'licenses/dotnet/THIRD-PARTY-NOTICES.TXT',
        "extension/$($manifest.background.service_worker)", "extension/$($manifest.action.default_popup)")
    $required += @($manifest.content_scripts | ForEach-Object { $_.js } | ForEach-Object { "extension/$_" })
    $required += @($manifest.icons.PSObject.Properties.Value | ForEach-Object { "extension/$_" })
    Assert-True (@($required | Where-Object { -not $entries.ContainsKey($_) }).Count -eq 0) 'Includes native host, setup, licenses and every manifest entry point'
    Assert-True ($roots[0] -match "-$([regex]::Escape($manifest.version))-win-(x64|arm64)$") 'Archive identity matches the extension version'
    Assert-True (@($entries.Keys | Where-Object { $_ -match '(^|/)(tests?|fixture|preview|obj|bin)/|\.pdb$|native-host\.json$|foreground-trace|\.runtimeconfig\.json$|\.deps\.json$|\.dll$' }).Count -eq 0) 'Excludes development files, local registration and managed-runtime dependencies'
    foreach ($name in @('Setup.ps1', 'Registration.psm1')) {
        $tokens = $null; $parseErrors = $null
        $null = [Management.Automation.Language.Parser]::ParseInput((Read-Entry $name), [ref]$tokens, [ref]$parseErrors)
        Assert-True ($parseErrors.Count -eq 0) "$name parses successfully"
    }
    $expectedHash = (Get-Content -LiteralPath "$archivePath.sha256" -Raw).Split(' ')[0]
    Assert-True ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -eq $expectedHash) 'SHA-256 sidecar matches the archive'
    $report | ConvertTo-Json | Set-Content (Join-Path $repoRoot 'artifacts\tests\browser-companion\release\package-results.json') -Encoding utf8
    Write-Host "$($report.Count) package checks passed."
}
finally { $zip.Dispose() }
