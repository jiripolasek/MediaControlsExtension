<#
.SYNOPSIS
Validates packaged activation and restart of the separate GSMTC worker probe.

.DESCRIPTION
Activates and holds the installed normal extension process, then creates the
named-pipe endpoint and activates the separately packaged worker COM class. The
script validates the worker's kernel-derived PID, package identity, image path,
and GSMTC manager acquisition, kills that exact worker process, and repeats
activation while requiring the normal extension process to remain alive.
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string] $PackageName = 'JiriPolasek.MediaControlsForCmdPal',

    [Parameter()]
    [string] $WorkerPackageName = 'JiriPolasek.MediaControlsWorkerProbe',

    [Parameter()]
    [int] $TimeoutSeconds = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not ('MediaWorkerProbe.ComActivator' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace MediaWorkerProbe
{
    public static class ComActivator
    {
        private const uint ClsctxLocalServer = 0x4;
        private const uint CoinitMultithreaded = 0x0;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const int ErrorInsufficientBuffer = 122;

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(
            ref Guid classId,
            IntPtr outer,
            uint classContext,
            ref Guid interfaceId,
            out IntPtr instance);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientProcessId(
            SafePipeHandle pipe,
            out uint clientProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPackageFullName(
            IntPtr process,
            ref uint packageFullNameLength,
            StringBuilder packageFullName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetApplicationUserModelId(
            IntPtr process,
            ref uint applicationUserModelIdLength,
            StringBuilder applicationUserModelId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(
            IntPtr process,
            int flags,
            StringBuilder executablePath,
            ref int size);

        public static IntPtr Activate(Guid classId, Guid interfaceId)
        {
            int initializeResult = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);
            if (initializeResult < 0 && initializeResult != unchecked((int)0x80010106))
            {
                Marshal.ThrowExceptionForHR(initializeResult);
            }

            int result = CoCreateInstance(
                ref classId,
                IntPtr.Zero,
                ClsctxLocalServer,
                ref interfaceId,
                out IntPtr instance);
            Marshal.ThrowExceptionForHR(result);
            return instance;
        }

        public static int GetClientProcessId(NamedPipeServerStream pipe)
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientProcessId))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return checked((int)clientProcessId);
        }

        public static ProcessIdentity GetProcessIdentity(int processId)
        {
            IntPtr process = OpenProcess(
                ProcessQueryLimitedInformation,
                inheritHandle: false,
                checked((uint)processId));
            if (process == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var executablePath = new StringBuilder(32768);
                int executablePathLength = executablePath.Capacity;
                if (!QueryFullProcessImageName(
                    process,
                    flags: 0,
                    executablePath,
                    ref executablePathLength))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                return new ProcessIdentity(
                    ReadIdentity(process, GetPackageFullName),
                    ReadIdentity(process, GetApplicationUserModelId),
                    executablePath.ToString());
            }
            finally
            {
                CloseHandle(process);
            }
        }

        public static string ReadJson(NamedPipeServerStream pipe, int timeoutSeconds)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var reader = new StreamReader(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024,
                leaveOpen: true);

            string json;
            try
            {
                json = reader.ReadToEndAsync(cancellation.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException ex)
            {
                throw new TimeoutException(
                    $"Worker did not finish its JSON response within {timeoutSeconds} seconds.",
                    ex);
            }

            if (Encoding.UTF8.GetByteCount(json) > 64 * 1024)
            {
                throw new InvalidDataException("Worker JSON response exceeded 64 KiB.");
            }

            return json;
        }

        private static string ReadIdentity(IntPtr process, ProcessIdentityReader reader)
        {
            uint length = 0;
            int result = reader(process, ref length, null);
            if (result != ErrorInsufficientBuffer)
            {
                throw new Win32Exception(result);
            }

            var value = new StringBuilder(checked((int)length));
            result = reader(process, ref length, value);
            if (result != 0)
            {
                throw new Win32Exception(result);
            }

            return value.ToString();
        }

        private delegate int ProcessIdentityReader(
            IntPtr process,
            ref uint length,
            StringBuilder value);

        public static void Release(IntPtr instance)
        {
            if (instance != IntPtr.Zero)
            {
                Marshal.Release(instance);
            }
        }
    }

    public sealed class ProcessIdentity
    {
        public ProcessIdentity(
            string packageFullName,
            string appUserModelId,
            string executablePath)
        {
            PackageFullName = packageFullName;
            AppUserModelId = appUserModelId;
            ExecutablePath = executablePath;
        }

        public string PackageFullName { get; }

        public string AppUserModelId { get; }

        public string ExecutablePath { get; }
    }
}
'@
}

$workerClassId = [Guid] 'f47a961b-a78e-4523-9c9e-ef9bf5e88706'
$normalExtensionClassId = [Guid] '502f0b1d-b778-450c-9803-6c09cb0e6407'
$activationLeaseInterfaceId = [Guid] '00000000-0000-0000-c000-000000000046'
$pipeName = 'LOCAL\JPSoftworks.MediaControlsExtension.MediaWorkerProbe'
$workerActivationArguments = '-RegisterProcessAsComServer'

$normalPackage = Get-AppxPackage -Name $PackageName -ErrorAction Stop |
    Sort-Object Version -Descending |
    Select-Object -First 1
if ($null -eq $normalPackage -or [string] $normalPackage.Status -ne 'Ok') {
    throw "The normal extension package '$PackageName' must be installed and healthy."
}
$expectedNormalExecutablePath = Join-Path $normalPackage.InstallLocation 'JPSoftworks.MediaControlsExtension\JPSoftworks.MediaControlsExtension.exe'

$package = Get-AppxPackage -Name $WorkerPackageName -ErrorAction Stop |
    Sort-Object Version -Descending |
    Select-Object -First 1
if ($null -eq $package) {
    throw "Experimental package '$WorkerPackageName' is not installed for the current user. Deploy the MediaWorkerProbe package first."
}
if ([string] $package.Status -ne 'Ok') {
    throw "Package '$($package.PackageFullName)' has status '$($package.Status)'."
}

$expectedAppUserModelId = "$($package.PackageFamilyName)!App"
$workerExecutablePath = 'JPSoftworks.MediaControlsExtension.MediaWorkerProbe\JPSoftworks.MediaControlsExtension.MediaWorkerProbe.exe'
$expectedExecutablePath = Join-Path $package.InstallLocation $workerExecutablePath
$installedManifestPath = Join-Path $package.InstallLocation 'AppxManifest.xml'
$installedManifest = [xml] [IO.File]::ReadAllText($installedManifestPath)
$probeClassNode = $installedManifest.SelectSingleNode(
    "//*[local-name()='Class' and translate(@Id, 'ABCDEF', 'abcdef')='$($workerClassId.ToString())']")
if ($null -eq $probeClassNode) {
    throw "Installed manifest does not register worker CLSID '$workerClassId'."
}
$probeServerNode = $probeClassNode.ParentNode
if ($probeServerNode.LocalName -ne 'ExeServer' -or
    $probeServerNode.GetAttribute('Arguments') -ne $workerActivationArguments -or
    $probeServerNode.GetAttribute('Executable') -ne $workerExecutablePath) {
    throw 'Installed worker CLSID does not use the separate probe executable and expected activation arguments.'
}

function Get-NormalExtensionProcesses {
    @(Get-CimInstance Win32_Process -Filter "Name = 'JPSoftworks.MediaControlsExtension.exe'" |
        Where-Object {
            $_.ExecutablePath -eq $expectedNormalExecutablePath -and
            $_.CommandLine -like '*-RegisterProcessAsComServer*' -and
            $_.CommandLine -notlike '*--media-worker-probe*'
        })
}

function Invoke-WorkerProbe {
    param(
        [Parameter(Mandatory)]
        [int] $Attempt
    )

    $pipeOptions = [IO.Pipes.PipeOptions] (
        [int][IO.Pipes.PipeOptions]::Asynchronous -bor
        [int][IO.Pipes.PipeOptions]::CurrentUserOnly)
    $pipe = [IO.Pipes.NamedPipeServerStream]::new(
        $pipeName,
        [IO.Pipes.PipeDirection]::In,
        1,
        [IO.Pipes.PipeTransmissionMode]::Byte,
        $pipeOptions)
    $instance = [IntPtr]::Zero
    $document = $null
    $workerProcess = $null
    $succeeded = $false

    try {
        $connection = $pipe.WaitForConnectionAsync()
        $activationStartedAt = Get-Date
        $instance = [MediaWorkerProbe.ComActivator]::Activate(
            $workerClassId,
            $activationLeaseInterfaceId)

        $connectionDeadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        while (-not $pipe.IsConnected -and [DateTime]::UtcNow -lt $connectionDeadline) {
            if ($connection.IsFaulted) {
                $connection.GetAwaiter().GetResult()
            }
            Start-Sleep -Milliseconds 25
        }
        if (-not $pipe.IsConnected) {
            throw "Worker probe attempt $Attempt did not connect within $TimeoutSeconds seconds."
        }

        $kernelWorkerProcessId = [MediaWorkerProbe.ComActivator]::GetClientProcessId($pipe)
        $candidateProcess = Get-Process -Id $kernelWorkerProcessId -ErrorAction Stop
        $kernelIdentity = [MediaWorkerProbe.ComActivator]::GetProcessIdentity($kernelWorkerProcessId)
        if ($kernelIdentity.ExecutablePath -ne $expectedExecutablePath) {
            throw "Pipe client PID $kernelWorkerProcessId is '$($kernelIdentity.ExecutablePath)', not the packaged probe executable."
        }
        if ($kernelIdentity.PackageFullName -ne $package.PackageFullName) {
            throw "Pipe client PID $kernelWorkerProcessId has package '$($kernelIdentity.PackageFullName)'; expected '$($package.PackageFullName)'."
        }
        if ($kernelIdentity.AppUserModelId -ne $expectedAppUserModelId) {
            throw "Pipe client PID $kernelWorkerProcessId has AUMID '$($kernelIdentity.AppUserModelId)'; expected '$expectedAppUserModelId'."
        }
        if ($candidateProcess.StartTime -lt $activationStartedAt.AddSeconds(-2)) {
            throw "Pipe client PID $kernelWorkerProcessId predates worker activation."
        }
        $workerProcess = $candidateProcess

        $json = [MediaWorkerProbe.ComActivator]::ReadJson($pipe, $TimeoutSeconds)
        $document = [Text.Json.JsonDocument]::Parse($json)
        $result = $document.RootElement
        $protocolVersion = $result.GetProperty('protocolVersion').GetInt32()
        $workerEpoch = $result.GetProperty('workerEpoch').GetGuid()
        $reportedWorkerProcessId = $result.GetProperty('workerProcessId').GetInt32()
        $reportedPackageFullName = $result.GetProperty('packageFullName').GetString()
        $reportedAppUserModelId = $result.GetProperty('appUserModelId').GetString()
        $managerAcquired = $result.GetProperty('gsmtcManagerAcquired').GetBoolean()
        $errorText = if ($result.GetProperty('error').ValueKind -eq [Text.Json.JsonValueKind]::Null) {
            $null
        }
        else {
            $result.GetProperty('error').GetString()
        }

        if ($protocolVersion -ne 1) {
            throw "Worker reported protocol version $protocolVersion; expected 1."
        }
        if ($reportedWorkerProcessId -ne $kernelWorkerProcessId) {
            throw "Worker reported PID $reportedWorkerProcessId; the named-pipe client is PID $kernelWorkerProcessId."
        }
        if ($reportedPackageFullName -ne $kernelIdentity.PackageFullName) {
            throw "Worker reported package '$reportedPackageFullName'; the kernel reports '$($kernelIdentity.PackageFullName)'."
        }
        if ($reportedAppUserModelId -ne $kernelIdentity.AppUserModelId) {
            throw "Worker reported AUMID '$reportedAppUserModelId'; the kernel reports '$($kernelIdentity.AppUserModelId)'."
        }
        if (-not $managerAcquired) {
            throw "Worker could not acquire GSMTC: $errorText"
        }

        $succeeded = $true
        [pscustomobject]@{
            Attempt             = $Attempt
            ProtocolVersion     = $protocolVersion
            WorkerEpoch         = $workerEpoch
            WorkerProcessId     = $kernelWorkerProcessId
            PackageFullName     = $reportedPackageFullName
            AppUserModelId      = $reportedAppUserModelId
            SessionCount        = $result.GetProperty('sessionCount').GetInt32()
            GsmtcManagerAcquired = $managerAcquired
            Process             = $workerProcess
            ComInstance         = $instance
        }
    }
    finally {
        if (-not $succeeded) {
            if ($null -ne $workerProcess) {
                try {
                    if (-not $workerProcess.HasExited) {
                        Stop-Process -InputObject $workerProcess -Force
                        $null = $workerProcess.WaitForExit($TimeoutSeconds * 1000)
                    }
                }
                catch {
                    Write-Verbose "Worker cleanup failed after an unsuccessful probe: $($_.Exception.Message)"
                }
            }

            if ($instance -ne [IntPtr]::Zero) {
                try {
                    [MediaWorkerProbe.ComActivator]::Release($instance)
                }
                catch {
                    Write-Verbose "COM proxy release failed after an unsuccessful probe: $($_.Exception.Message)"
                }
            }
        }

        if ($null -ne $document) {
            $document.Dispose()
        }
        $pipe.Dispose()
    }
}

$normalExtensionInstance = [IntPtr]::Zero
$normalExtensionProcess = $null
$normalExtensionWasStartedByTest = $false
$normalProcessesBefore = @(Get-NormalExtensionProcesses)
$normalProcessIdsBefore = @($normalProcessesBefore | ForEach-Object { [int] $_.ProcessId })

try {
    $normalExtensionInstance = [MediaWorkerProbe.ComActivator]::Activate(
        $normalExtensionClassId,
        $activationLeaseInterfaceId)

    $normalProcessDeadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $normalProcesses = @(Get-NormalExtensionProcesses)
        if ($normalProcesses.Count -gt 0) {
            break
        }
        Start-Sleep -Milliseconds 25
    } while ([DateTime]::UtcNow -lt $normalProcessDeadline)

    if ($normalProcesses.Count -ne 1) {
        throw "Expected one packaged normal extension process; found $($normalProcesses.Count)."
    }

    $normalExtensionProcess = Get-Process -Id $normalProcesses[0].ProcessId -ErrorAction Stop
    $normalExtensionWasStartedByTest = $normalExtensionProcess.Id -notin $normalProcessIdsBefore
    Write-Host "Normal extension held at PID $($normalExtensionProcess.Id)."

    $results = [Collections.Generic.List[object]]::new()
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        $result = $null
        try {
            $result = Invoke-WorkerProbe -Attempt $attempt
            $results.Add($result)

            if ($result.WorkerProcessId -eq $normalExtensionProcess.Id) {
                throw 'Worker and normal extension unexpectedly share a process.'
            }

            Write-Host (
                "Attempt {0}: worker PID {1}, package identity confirmed, GSMTC sessions {2}." -f
                $attempt,
                $result.WorkerProcessId,
                $result.SessionCount)
        }
        finally {
            if ($null -ne $result) {
                try {
                    if (-not $result.Process.HasExited) {
                        Stop-Process -InputObject $result.Process -Force
                        $null = $result.Process.WaitForExit($TimeoutSeconds * 1000)
                    }
                    if (-not $result.Process.HasExited) {
                        throw "Worker PID $($result.WorkerProcessId) did not exit after termination."
                    }
                }
                finally {
                    try {
                        [MediaWorkerProbe.ComActivator]::Release($result.ComInstance)
                    }
                    catch {
                        Write-Verbose "COM proxy release failed after worker termination: $($_.Exception.Message)"
                    }
                }
            }
        }

        if ($normalExtensionProcess.HasExited) {
            throw "Normal extension PID $($normalExtensionProcess.Id) exited when worker attempt $attempt was terminated."
        }
    }

    if ($results[0].WorkerEpoch -eq $results[1].WorkerEpoch) {
        throw 'The worker epoch was unexpectedly reused across the two probe attempts.'
    }

    Write-Host (
        "PASS: extension PID {0} survived worker termination and packaged recovery completed with PID {1}." -f
        $normalExtensionProcess.Id,
        $results[1].WorkerProcessId)
}
finally {
    if ($normalExtensionInstance -ne [IntPtr]::Zero) {
        try {
            [MediaWorkerProbe.ComActivator]::Release($normalExtensionInstance)
        }
        catch {
            Write-Verbose "Normal extension COM proxy release failed: $($_.Exception.Message)"
        }
    }

    if ($normalExtensionWasStartedByTest -and $null -ne $normalExtensionProcess) {
        try {
            if (-not $normalExtensionProcess.HasExited) {
                Stop-Process -InputObject $normalExtensionProcess -Force
                $null = $normalExtensionProcess.WaitForExit($TimeoutSeconds * 1000)
            }
        }
        catch {
            Write-Verbose "Normal extension cleanup failed: $($_.Exception.Message)"
        }
    }
}
