[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Baseline', 'Install', 'Launch', 'Observe', 'Uninstall', 'VerifyUninstall')][string] $Phase,
    [string] $InputDirectory = 'C:\1PasswordProofInput',
    [string] $ResultDirectory = 'C:\1PasswordProofResults'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Run this proof only inside Windows Sandbox.' }
if (!(Test-Path -LiteralPath $ResultDirectory -PathType Container)) { throw 'The dedicated results share is missing.' }
$family = 'Agilebits.1Password_amwd9z03whsfe'

function Write-Result([string] $Name, $Value) {
    $json = $Value | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText((Join-Path $ResultDirectory $Name), $json + "`r`n", [Text.UTF8Encoding]::new($false))
}

function Read-Registrations {
    foreach ($hive in @([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryHive]::LocalMachine)) {
        foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
            $root = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
            try {
                foreach ($browser in @('Microsoft\Edge', 'Google\Chrome', 'Mozilla')) {
                    $path = "Software\$browser\NativeMessagingHosts\com.1password.1password"
                    $key = $root.OpenSubKey($path)
                    try { $value = if ($null -ne $key) { $key.GetValue('') } else { $null } }
                    finally { if ($null -ne $key) { $key.Dispose() } }
                    $manifestExists = $null -ne $value -and [IO.File]::Exists($value)
                    $native = $null
                    $executable = $null
                    if ($manifestExists) {
                        $native = Get-Content -LiteralPath $value -Raw | ConvertFrom-Json
                        $executable = if ([IO.Path]::IsPathRooted($native.path)) { $native.path } else { Join-Path (Split-Path -Parent $value) $native.path }
                    }
                    [ordered]@{
                        hive = $hive.ToString(); view = $view.ToString(); browser = $browser; path = $path
                        exists = $null -ne $key; value = $value; manifestExists = $manifestExists
                        executable = $executable; executableExists = $null -ne $executable -and [IO.File]::Exists($executable)
                    }
                }
            }
            finally { $root.Dispose() }
        }
    }
}

function Read-Snapshot([string] $Name) {
    $packages = @(Get-AppxPackage -Name '*1Password*' | Select-Object Name, PackageFullName, PackageFamilyName, Version, InstallLocation, IsDevelopmentMode, Status)
    $processes = @(Get-CimInstance Win32_Process -Filter "Name LIKE '%1Password%'" | Select-Object ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine)
    $windows = @(Get-Process -Name '*1Password*' -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0 | Select-Object Id, ProcessName, MainWindowHandle, MainWindowTitle)
    $aliases = @('1Password.exe', '1Password-BrowserSupport.exe') | ForEach-Object {
        $path = Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\$_"
        [ordered]@{ name = $_; path = $path; exists = Test-Path -LiteralPath $path }
    }
    $snapshot = [ordered]@{
        phase = $Name; user = [Environment]::UserName; computer = [Environment]::MachineName
        os = [Environment]::OSVersion.Version.ToString(); observedUtc = [DateTime]::UtcNow.ToString('o')
        packages = $packages; registrations = @(Read-Registrations); processes = $processes; windows = $windows; aliases = @($aliases)
    }
    Write-Result "$Name.json" $snapshot
    return $snapshot
}

function Write-UninstallCheck($Snapshot, [string] $PackageDirectory) {
    Write-Result 'uninstall-check.json' ([ordered]@{
        packageAbsent = $Snapshot.packages.Count -eq 0; processCount = $Snapshot.processes.Count
        packageDirectoryExists = Test-Path -LiteralPath $PackageDirectory
        remainingRegistrations = @($Snapshot.registrations | Where-Object exists)
        remainingAliases = @($Snapshot.aliases | Where-Object exists)
    })
}

try {
    switch ($Phase) {
        'Baseline' {
            if (Test-Path -LiteralPath (Join-Path $ResultDirectory 'baseline.json')) { throw 'Baseline already exists; use a fresh evidence directory.' }
            $baseline = Read-Snapshot 'baseline'
            if ($baseline.packages.Count -ne 0 -or $baseline.processes.Count -ne 0 -or @($baseline.registrations | Where-Object exists).Count -ne 0) {
                throw 'Existing 1Password state found in this Sandbox; preserve it.'
            }
        }
        'Install' {
            $baseline = Get-Content -LiteralPath (Join-Path $ResultDirectory 'baseline.json') -Raw | ConvertFrom-Json
            $baselineResult = Get-Content -LiteralPath (Join-Path $ResultDirectory 'Baseline-complete.json') -Raw | ConvertFrom-Json
            if (!$baselineResult.success -or $baseline.computer -ne [Environment]::MachineName -or $baseline.packages.Count -ne 0 -or $baseline.processes.Count -ne 0 -or @($baseline.registrations | Where-Object exists).Count -ne 0) { throw 'Expected a clean baseline from this Sandbox.' }
            if (@(Get-AppxPackage -Name '*1Password*').Count -ne 0 -or @(Read-Registrations | Where-Object exists).Count -ne 0 -or @(Get-Process -Name '*1Password*' -ErrorAction SilentlyContinue).Count -ne 0) { throw '1Password state appeared after the baseline; preserve it.' }
            if (Test-Path -LiteralPath (Join-Path $ResultDirectory 'installed.json')) { throw 'This proof has already installed 1Password.' }
            $bundle = Join-Path $InputDirectory '1PasswordSetup.msixbundle'
            $signature = Get-AuthenticodeSignature -LiteralPath $bundle
            if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|, )O=Agilebits(,|$)') { throw 'Unexpected package signature.' }
            Write-Result 'installer.json' ([ordered]@{ sha256 = (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash; signer = $signature.SignerCertificate.Subject; signature = $signature.Status.ToString() })
            Add-AppxPackage -Path $bundle
            $installed = Read-Snapshot 'installed'
            if ($installed.packages.Count -ne 1 -or $installed.packages[0].PackageFamilyName -ne $family) { throw 'Unexpected installed package identity.' }
            $manifestPath = Join-Path $installed.packages[0].InstallLocation 'AppxManifest.xml'
            Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $ResultDirectory 'installed-AppxManifest.xml')
            [xml] $manifest = Get-Content -LiteralPath $manifestPath -Raw
            Write-Result 'manifest-summary.json' ([ordered]@{
                capabilities = @($manifest.Package.Capabilities.ChildNodes | Where-Object NodeType -eq Element | ForEach-Object { $_.GetAttribute('Name') })
                applications = @($manifest.Package.Applications.Application | ForEach-Object { [ordered]@{ id = $_.Id; executable = $_.Executable } })
            })
        }
        'Launch' {
            $installed = Get-Content -LiteralPath (Join-Path $ResultDirectory 'installed.json') -Raw | ConvertFrom-Json
            $package = Get-AppxPackage -Name Agilebits.1Password
            if ($null -eq $package -or $package.PackageFullName -ne $installed.packages[0].PackageFullName) { throw 'Installed proof package changed.' }
            [xml] $manifest = Get-Content -LiteralPath (Join-Path $package.InstallLocation 'AppxManifest.xml') -Raw
            $apps = @($manifest.Package.Applications.Application | Where-Object { [IO.Path]::GetFileName($_.Executable) -eq '1Password.exe' })
            if ($apps.Count -ne 1) { throw 'Cannot uniquely identify the main 1Password application.' }
            $aumid = $package.PackageFamilyName + '!' + $apps[0].Id
            Start-Process -FilePath explorer.exe -ArgumentList "shell:AppsFolder\$aumid" -WindowStyle Hidden
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            do {
                Start-Sleep -Milliseconds 500
                $ready = @(Read-Registrations | Where-Object { $_.hive -eq 'CurrentUser' -and $_.view -eq 'Registry64' -and $_.browser -ne 'Mozilla' -and $_.exists -and $_.manifestExists -and $_.executableExists }).Count -eq 2
            } while (!$ready -and [DateTime]::UtcNow -lt $deadline)
            $launched = Read-Snapshot 'launched'
            Write-Result 'launch-check.json' ([ordered]@{ aumid = $aumid; bothBrowsersRegistered = $ready; processCount = $launched.processes.Count; windowCount = $launched.windows.Count })
        }
        'Observe' { $null = Read-Snapshot 'observed' }
        'Uninstall' {
            $installed = Get-Content -LiteralPath (Join-Path $ResultDirectory 'installed.json') -Raw | ConvertFrom-Json
            $package = Get-AppxPackage -Name Agilebits.1Password
            if ($null -eq $package -or $package.PackageFullName -ne $installed.packages[0].PackageFullName -or $package.PackageFamilyName -ne $family) { throw 'Refusing to remove a package not installed by this proof.' }
            Remove-AppxPackage -Package $package.PackageFullName
            $null = Read-Snapshot 'uninstalled-immediate'
            Start-Sleep -Seconds 15
            $removed = Read-Snapshot 'uninstalled-settled'
            Write-UninstallCheck $removed $installed.packages[0].InstallLocation
        }
        'VerifyUninstall' {
            $installed = Get-Content -LiteralPath (Join-Path $ResultDirectory 'installed.json') -Raw | ConvertFrom-Json
            $removed = Read-Snapshot 'uninstalled-rechecked'
            Write-UninstallCheck $removed $installed.packages[0].InstallLocation
        }
    }
    Write-Result "$Phase-complete.json" ([ordered]@{ phase = $Phase; success = $true; observedUtc = [DateTime]::UtcNow.ToString('o') })
}
catch {
    Write-Result "$Phase-error.json" ([ordered]@{ phase = $Phase; error = $_.Exception.ToString(); detail = ($_ | Out-String); observedUtc = [DateTime]::UtcNow.ToString('o') })
    exit 1
}
