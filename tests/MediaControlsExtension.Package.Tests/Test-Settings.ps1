[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$taskRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$taskScript = Join-Path $taskRepo 'eng\Test-MediaWorkerPackage.ps1'
$taskParseErrors = $null
$taskAst = [Management.Automation.Language.Parser]::ParseFile($taskScript, [ref]$null, [ref]$taskParseErrors)
if ($taskParseErrors.Count) { throw ($taskParseErrors.Message -join [Environment]::NewLine) }
foreach ($taskName in @('Read-TestSettings', 'Set-TestMode', 'Restore-TestSettings')) {
    $taskFunction = $taskAst.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $taskName
    }, $false)
    if (!$taskFunction) { throw "Missing package settings function: $taskName" }
    . ([scriptblock]::Create($taskFunction.Extent.Text))
}

function Assert-Test([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}

function Refresh-TestHost {
    if ($taskFailRefresh) { throw 'Injected refresh failure.' }
}

$RestartHost = $false
$taskKeys = @('jpsoftworks.mediacontrols.MediaBackends.gsmtc.Enabled', 'jpsoftworks.mediacontrols.MediaBackends.gsmtc.worker.Enabled', 'jpsoftworks.mediacontrols.MediaBackends.dummy.worker.Enabled')
$taskArtifacts = [IO.Path]::GetFullPath((Join-Path $taskRepo 'artifacts\MediaBackendHost'))
$taskRoot = Join-Path $taskArtifacts "package-settings-$([guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($taskRoot) | Out-Null
try {
    foreach ($taskInitial in @('missing-directory', 'missing-file', 'existing')) {
        foreach ($taskFailRefresh in @($false, $true)) {
            $taskSettingsPath = Join-Path $taskRoot "$taskInitial-$taskFailRefresh\LocalState\settings.json"
            if ($taskInitial -ne 'missing-directory') {
                [IO.Directory]::CreateDirectory((Split-Path -Parent $taskSettingsPath)) | Out-Null
            }
            if ($taskInitial -eq 'existing') {
                @{ $taskKeys[0] = $true; $taskKeys[1] = 'false'; unrelated = @{ value = 17 } } |
                    ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $taskSettingsPath -Encoding utf8
            }
            $taskSettingsExisted = Test-Path -LiteralPath $taskSettingsPath -PathType Leaf
            $taskOriginal = Read-TestSettings
            if (!$taskSettingsExisted) { Assert-Test ($taskOriginal.Count -eq 0) 'Missing settings did not read as empty.' }
            $taskPrevious = @{}
            foreach ($taskKey in $taskKeys) {
                $taskPrevious[$taskKey] = @{ Present = $taskOriginal.ContainsKey($taskKey); Value = $taskOriginal[$taskKey] }
            }
            $taskFailed = $false
            try {
                Set-TestMode $true
                $taskDuring = Read-TestSettings
                Assert-Test ($taskDuring[$taskKeys[0]] -eq 'false' -and $taskDuring[$taskKeys[1]] -eq 'true') 'Worker mode was not written.'
                Set-TestMode $false
                $taskDuring = Read-TestSettings
                Assert-Test ($taskDuring[$taskKeys[0]] -eq 'true' -and $taskDuring[$taskKeys[1]] -eq 'false') 'Internal mode was not written.'
                $taskDuring['added-during-check'] = 23
                $taskDuring | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $taskSettingsPath -Encoding utf8
            }
            catch {
                if (!$taskFailRefresh -or $_.Exception.Message -ne 'Injected refresh failure.') { throw }
                $taskFailed = $true
                Assert-Test (Test-Path -LiteralPath $taskSettingsPath -PathType Leaf) 'The failure did not exercise cleanup after writing settings.'
            }
            finally { Restore-TestSettings }
            Assert-Test ($taskFailed -eq $taskFailRefresh) 'The expected refresh failure was not exercised.'
            Assert-Test (!(Test-Path -LiteralPath ($taskSettingsPath + '.worker-test.tmp'))) 'A temporary settings file survived cleanup.'
            if ($taskSettingsExisted) {
                $taskRestored = Read-TestSettings
                Assert-Test ($taskRestored[$taskKeys[0]] -is [bool] -and $taskRestored[$taskKeys[0]]) 'The original boolean was not restored.'
                Assert-Test ($taskRestored[$taskKeys[1]] -ceq 'false' -and !$taskRestored.ContainsKey($taskKeys[2])) 'Provider key presence or value changed.'
                Assert-Test ($taskRestored.unrelated.value -eq 17) 'Unrelated settings were lost.'
                if (!$taskFailRefresh) { Assert-Test ($taskRestored['added-during-check'] -eq 23) 'A setting saved during the check was lost.' }
            }
            else { Assert-Test (!(Test-Path -LiteralPath $taskSettingsPath)) 'Cleanup did not restore the missing settings file.' }
            "PASS $taskInitial settings; refresh failure: $taskFailRefresh"
        }
    }
    $taskSettingsPath = Join-Path $taskRoot 'never-created\settings.json'
    $taskSettingsExisted = $false
    Restore-TestSettings
    Assert-Test (!(Test-Path -LiteralPath $taskSettingsPath)) 'Cleanup created settings before any test write.'
    'PASS cleanup before the first settings write'

    $taskSettingsPath = Join-Path $taskRoot 'invalid.json'
    [IO.File]::WriteAllText($taskSettingsPath, '{ invalid')
    $taskRejected = $false
    try { $null = Read-TestSettings } catch { $taskRejected = $true }
    Assert-Test $taskRejected 'Malformed settings were treated as missing.'
    'PASS malformed settings remain an error'
}
finally {
    $taskResolved = [IO.Path]::GetFullPath($taskRoot)
    if (!$taskResolved.StartsWith($taskArtifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        !(Split-Path -Leaf $taskResolved).StartsWith('package-settings-', [StringComparison]::Ordinal)) {
        throw 'Refusing to remove a directory outside the package settings test workspace.'
    }
    Remove-Item -LiteralPath $taskResolved -Recurse -Force
}
