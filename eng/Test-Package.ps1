<#
.SYNOPSIS
Deploys the configured package and refreshes its host application.

.DESCRIPTION
Invokes Deploy-Package.ps1 with the supplied build options, then either sends
the configured reload URI or restarts the configured host process. Restart
uses the executable path of the running host when available, which preserves
the active development or release channel. If the host was not running, the
configured launch URI is used instead.

.EXAMPLE
.\eng\Test-Package.ps1

.EXAMPLE
.\eng\Test-Package.ps1 -AfterDeploy Restart

.EXAMPLE
.\eng\Test-Package.ps1 -Aot -AfterDeploy Reload
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string] $Configuration,

    [Parameter()]
    [string] $Platform,

    [Parameter()]
    [switch] $Aot,

    [Parameter()]
    [string] $VisualStudioPath,

    [Parameter()]
    [ValidateSet('Reload', 'Restart')]
    [string] $AfterDeploy = 'Reload',

    [Parameter()]
    [string] $ConfigPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $PSScriptRoot 'Package.config.psd1'
}

$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)
if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    throw "WAP package configuration was not found: '$ConfigPath'."
}

$packageConfig = Import-PowerShellDataFile -LiteralPath $ConfigPath
if (-not $packageConfig.ContainsKey('Host') -or
    -not ($packageConfig.Host -is [Collections.IDictionary])) {
    throw "WAP package configuration '$ConfigPath' is missing the 'Host' hashtable."
}

$hostConfig = $packageConfig.Host
foreach ($requiredHostConfigKey in @('ProcessName', 'LaunchUri', 'ReloadUri')) {
    if (-not $hostConfig.Contains($requiredHostConfigKey) -or
        [string]::IsNullOrWhiteSpace([string] $hostConfig[$requiredHostConfigKey])) {
        throw "Host configuration in '$ConfigPath' is missing '$requiredHostConfigKey'."
    }
}

$deployScriptPath = Join-Path $PSScriptRoot 'Deploy-Package.ps1'
if (-not (Test-Path -LiteralPath $deployScriptPath -PathType Leaf)) {
    throw "Deployment script was not found: '$deployScriptPath'."
}

$deployParameters = @{
    ConfigPath = $ConfigPath
}
if ($PSBoundParameters.ContainsKey('Configuration')) {
    $deployParameters.Configuration = $Configuration
}
if ($PSBoundParameters.ContainsKey('Platform')) {
    $deployParameters.Platform = $Platform
}
if ($Aot) {
    $deployParameters.Aot = $true
}
if ($PSBoundParameters.ContainsKey('VisualStudioPath')) {
    $deployParameters.VisualStudioPath = $VisualStudioPath
}

& $deployScriptPath @deployParameters

$hostProcessName = [string] $hostConfig.ProcessName

function Wait-ForHostProcess {
    param(
        [Parameter()]
        [int[]] $ExcludedProcessIds = @()
    )

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $matchingProcesses = @(
            Get-Process -Name $hostProcessName -ErrorAction SilentlyContinue |
                Where-Object { $_.Id -notin $ExcludedProcessIds }
        )
        if ($matchingProcesses.Count -gt 0) {
            return $matchingProcesses
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Host process '$hostProcessName' did not start within 10 seconds."
}

if ($AfterDeploy -eq 'Reload') {
    $reloadUri = [string] $hostConfig.ReloadUri
    Write-Host "Invoking host reload URI '$reloadUri'..."
    Start-Process -FilePath $reloadUri | Out-Null
    $hostProcesses = @(Wait-ForHostProcess)
    Write-Host "Package deployed and host reload requested. Host PID: $($hostProcesses[0].Id)."
    return
}

$existingHostProcesses = @(Get-Process -Name $hostProcessName -ErrorAction SilentlyContinue)
$existingHostProcessIds = @($existingHostProcesses | ForEach-Object { $_.Id })
$restartExecutablePath = $null
foreach ($existingHostProcess in $existingHostProcesses) {
    try {
        $candidateExecutablePath = $existingHostProcess.Path
        if (-not [string]::IsNullOrWhiteSpace($candidateExecutablePath) -and
            (Test-Path -LiteralPath $candidateExecutablePath -PathType Leaf)) {
            $restartExecutablePath = $candidateExecutablePath
            break
        }
    }
    catch {
        # Process path access can fail for a process that exits during discovery.
    }
}

if ($existingHostProcesses.Count -gt 0) {
    Write-Host "Stopping host process '$hostProcessName'..."
    $existingHostProcesses | Stop-Process -Force

    $stopDeadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $remainingHostProcesses = @(Get-Process -Name $hostProcessName -ErrorAction SilentlyContinue)
        if ($remainingHostProcesses.Count -eq 0) {
            break
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $stopDeadline)

    if ($remainingHostProcesses.Count -gt 0) {
        throw "Host process '$hostProcessName' did not stop within 10 seconds."
    }
}

if (-not [string]::IsNullOrWhiteSpace($restartExecutablePath)) {
    Write-Host "Restarting host from '$restartExecutablePath'..."
    Start-Process `
        -FilePath $restartExecutablePath `
        -WorkingDirectory (Split-Path -Parent $restartExecutablePath) `
        -WindowStyle Hidden |
        Out-Null
}
else {
    $launchUri = [string] $hostConfig.LaunchUri
    Write-Host "No running host executable was available; invoking '$launchUri'..."
    Start-Process -FilePath $launchUri | Out-Null
}

$restartedHostProcesses = @(Wait-ForHostProcess -ExcludedProcessIds $existingHostProcessIds)
Write-Host "Package deployed and host restarted. Host PID: $($restartedHostProcesses[0].Id)."
