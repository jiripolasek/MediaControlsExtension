using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace JPSoftworks.MediaControlsExtension.Media.Hosting;

internal sealed partial class OwnedWorkerProcess : IDisposable
{
    private readonly SafeFileHandle _job;
    private readonly SafeProcessHandle _process;

    private OwnedWorkerProcess(SafeFileHandle job, SafeProcessHandle process, int processId)
    {
        this._job = job;
        this._process = process;
        this.ProcessId = processId;
    }

    public int ProcessId { get; }

    public uint ExitCode
    {
        get
        {
            Check(GetExitCodeProcess(this._process, out var exitCode));
            return exitCode;
        }
    }

    public unsafe string? PackageFullName
    {
        get
        {
            uint length = 0;
            var error = GetPackageFullName(this._process, ref length, null);
            if (error == 15700) { return null; }
            if (error != 122) { throw new Win32Exception(error); }
            var buffer = new char[length];
            fixed (char* value = buffer)
            {
                error = GetPackageFullName(this._process, ref length, value);
                if (error != 0) { throw new Win32Exception(error); }
                return new string(value);
            }
        }
    }

    public static unsafe string? CurrentPackageFullName
    {
        get
        {
            uint length = 0;
            var error = GetCurrentPackageFullName(ref length, null);
            if (error == 15700) { return null; }
            if (error != 122) { throw new Win32Exception(error); }
            var buffer = new char[length];
            fixed (char* value = buffer)
            {
                error = GetCurrentPackageFullName(ref length, value);
                if (error != 0) { throw new Win32Exception(error); }
                return new string(value);
            }
        }
    }

    public bool HasExited => WaitForSingleObject(this._process, 0) switch
    {
        0 => true,
        258 => false,
        _ => throw new Win32Exception(Marshal.GetLastPInvokeError()),
    };

    public static unsafe OwnedWorkerProcess Start(string executable, IEnumerable<string> arguments)
    {
        executable = Path.GetFullPath(executable);
        var job = new SafeFileHandle(CreateJobObjectW(0, null), ownsHandle: true);
        if (job.IsInvalid)
        {
            job.Dispose();
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        nint attributes = 0;
        var initialized = false;
        try
        {
            var limits = new ExtendedLimits { Basic = new() { LimitFlags = 0x2000 } };
            Check(SetInformationJobObject(job, 9, &limits, (uint)sizeof(ExtendedLimits)));
            nuint size = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attributes = (nint)NativeMemory.Alloc(size);
            Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
            initialized = true;
            var jobHandle = job.DangerousGetHandle();
            Check(UpdateProcThreadAttribute(attributes, 0, 0x0002000D, &jobHandle, (nuint)sizeof(nint), 0, 0));
            var startup = new StartupInfoEx { StartupInfo = new() { Size = (uint)sizeof(StartupInfoEx) }, Attributes = attributes };
            var commandLine = string.Join(' ', new[] { executable }.Concat(arguments).Select(QuoteArgument)) + '\0';
            var buffer = commandLine.ToCharArray();
            fixed (char* command = buffer)
            {
                // The kernel assigns the job before any worker code can run.
                Check(CreateProcessW(executable, command, 0, 0, 0, 0x08080000, 0,
                    Path.GetDirectoryName(executable), ref startup, out var information));
                using var thread = new SafeFileHandle(information.Thread, ownsHandle: true);
                return new(job, new SafeProcessHandle(information.Process, ownsHandle: true), checked((int)information.ProcessId));
            }
        }
        catch
        {
            job.Dispose();
            throw;
        }
        finally
        {
            if (initialized)
            {
                DeleteProcThreadAttributeList(attributes);
            }

            NativeMemory.Free((void*)attributes);
        }
    }

    public void Terminate()
    {
        try
        {
            if (!this.HasExited && !this._job.IsClosed)
            {
                Check(TerminateJobObject(this._job, 1));
            }
        }
        catch (ObjectDisposedException) when (this._job.IsClosed)
        {
            // The owner deadline already closed the kill-on-close job.
        }
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        if (this.HasExited) { return; }
        cancellationToken.ThrowIfCancellationRequested();
        Check(DuplicateHandle(new nint(-1), this._process, new nint(-1), out var handle, 0, 0, 2));
        using var wait = new ProcessWaitHandle(handle);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(wait,
            static (state, _) => ((TaskCompletionSource)state!).TrySetResult(), exited, Timeout.InfiniteTimeSpan, executeOnlyOnce: true);
        try { await exited.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally { registration.Unregister(null); }
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeWaitHandle handle) => this.SafeWaitHandle = handle;
    }

    public void Dispose()
    {
        this._job.Dispose();
        this._process.Dispose();
    }

    internal void CloseJob() => this._job.Dispose();

    private static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }

            result.Append('\\', character == '"' ? (slashes * 2) + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }

        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }

    private static void Check(int result)
    {
        if (result == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime;
        public long JobTime;
        public uint LimitFlags;
        public nuint MinimumWorkingSet;
        public nuint MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations;
        public ulong WriteOperations;
        public ulong OtherOperations;
        public ulong ReadBytes;
        public ulong WriteBytes;
        public ulong OtherBytes;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemory;
        public nuint PeakJobMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort ReservedSize;
        public nint ReservedPointer;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial int SetInformationJobObject(SafeFileHandle job, int informationClass, void* information, uint length);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int InitializeProcThreadAttributeList(nint list, uint count, uint flags, ref nuint size);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial int UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, void* value, nuint size, nint previous, nint returnedSize);
    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint list);
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int CreateProcessW(string application, char* command, nint processAttributes,
        nint threadAttributes, int inheritHandles, uint flags, nint environment, string? directory,
        ref StartupInfoEx startup, out ProcessInformation information);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int TerminateJobObject(SafeFileHandle job, uint exitCode);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int DuplicateHandle(nint sourceProcess, SafeProcessHandle source, nint targetProcess,
        out SafeWaitHandle target, uint access, int inheritHandle, uint options);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetExitCodeProcess(SafeProcessHandle handle, out uint exitCode);
    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetPackageFullName(SafeProcessHandle process, ref uint length, char* name);
    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetCurrentPackageFullName(ref uint length, char* name);
}