using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UTM4WIN.Virtualization.Qemu;

internal sealed class OwnedToolProcess : IDisposable {
    private readonly SafeFileHandle _job, _process, _thread;
    private bool _disposed;
    public uint ProcessId {
        get; private set;
    }
    public StreamWriter Input {
        get;
    }
    public StreamReader Output {
        get;
    }
    public StreamReader Error {
        get;
    }

    private OwnedToolProcess(SafeFileHandle job, SafeFileHandle process, SafeFileHandle thread,
        SafeFileHandle input, SafeFileHandle output, SafeFileHandle error) {
        _job = job;
        _process = process;
        _thread = thread;
        Input = new(new FileStream(input, FileAccess.Write, 4096, false), new UTF8Encoding(false));
        Output = new(new FileStream(output, FileAccess.Read, 4096, false), Encoding.UTF8);
        Error = new(new FileStream(error, FileAccess.Read, 4096, false), Encoding.UTF8);
    }

    public static OwnedToolProcess Start(string executable, IReadOnlyList<string> arguments, string directory) {
        var commandLine = new StringBuilder(string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote)));
        if (commandLine.Length >= 32767)
            throw new ArgumentException("Windows command-line length exceeded.");
        var handles = new List<SafeFileHandle>();
        nint attributes = 0, jobs = 0, inherited = 0;
        bool initialized = false;
        OwnedToolProcess? child = null;
        try {
            var job = ToolNative.CreateJobObjectW(0, null);
            handles.Add(job);
            if (job.IsInvalid)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            var limits = new ToolNative.ExtendedLimits();
            limits.Basic.LimitFlags = 0x2000;
            ToolNative.Check(ToolNative.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ToolNative.ExtendedLimits>()));
            (SafeFileHandle Read, SafeFileHandle Write) Pipe() {
                var security = new ToolNative.SecurityAttributes { Length = Marshal.SizeOf<ToolNative.SecurityAttributes>(), Inherit = true };
                ToolNative.Check(ToolNative.CreatePipe(out var read, out var write, ref security, 0));
                handles.Add(read);
                handles.Add(write);
                return (read, write);
            }
            var input = Pipe();
            var output = Pipe();
            var error = Pipe();
            foreach (var parent in new[] { input.Write, output.Read, error.Read })
                ToolNative.Check(ToolNative.SetHandleInformation(parent, 1, 0));
            nuint bytes = 0;
            _ = ToolNative.InitializeProcThreadAttributeList(0, 2, 0, ref bytes);
            if (bytes == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            attributes = Marshal.AllocHGlobal(checked((int)bytes));
            ToolNative.Check(ToolNative.InitializeProcThreadAttributeList(attributes, 2, 0, ref bytes));
            initialized = true;
            jobs = Marshal.AllocHGlobal(nint.Size);
            Marshal.WriteIntPtr(jobs, job.DangerousGetHandle());
            inherited = Marshal.AllocHGlobal(3 * nint.Size);
            var childHandles = new[] { input.Read, output.Write, error.Write };
            for (int index = 0; index < childHandles.Length; index++)
                Marshal.WriteIntPtr(inherited, index * nint.Size, childHandles[index].DangerousGetHandle());
            ToolNative.Check(ToolNative.UpdateProcThreadAttribute(attributes, 0, 0x2000D, jobs, (nuint)nint.Size, 0, 0));
            ToolNative.Check(ToolNative.UpdateProcThreadAttribute(attributes, 0, 0x20002, inherited, (nuint)(3 * nint.Size), 0, 0));
            var startup = new ToolNative.StartupInfoEx { Attributes = attributes };
            startup.Info.Size = (uint)Marshal.SizeOf<ToolNative.StartupInfoEx>();
            startup.Info.Flags = 0x100;
            startup.Info.Input = input.Read.DangerousGetHandle();
            startup.Info.Output = output.Write.DangerousGetHandle();
            startup.Info.Error = error.Write.DangerousGetHandle();
            // Assignment is atomic during CreateProcess; no unsupervised Process.Start fallback.
            ToolNative.Check(ToolNative.CreateProcessW(executable, commandLine, 0, 0, true,
                0x00080000 | 0x00000004 | 0x08000000, 0, directory, ref startup, out var information));
            var process = new SafeFileHandle(information.Process, true);
            var thread = new SafeFileHandle(information.Thread, true);
            handles.Add(process);
            handles.Add(thread);
            child = new(job, process, thread, input.Write, output.Read, error.Read);
            child.ProcessId = information.ProcessId;
            ToolNative.Check(ToolNative.IsProcessInJob(process, job, out bool contained));
            if (!contained)
                throw new IOException("Tool process containment could not be verified.");
            foreach (var owned in new[] { job, process, thread, input.Write, output.Read, error.Read })
                handles.Remove(owned);
            return child;
        } catch { child?.Dispose(); throw; } finally {
            if (initialized)
                ToolNative.DeleteProcThreadAttributeList(attributes);
            if (attributes != 0)
                Marshal.FreeHGlobal(attributes);
            if (jobs != 0)
                Marshal.FreeHGlobal(jobs);
            if (inherited != 0)
                Marshal.FreeHGlobal(inherited);
            foreach (var handle in handles)
                handle.Dispose();
        }
    }

    public void Resume(bool keepInputOpen = false) {
        uint previous = ToolNative.ResumeThread(_thread);
        if (previous == uint.MaxValue)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (previous != 1)
            throw new IOException("Unexpected tool process suspend count.");
        _thread.Dispose();
        if (!keepInputOpen)
            Input.Dispose();
    }

    public uint ExitCode {
        get {
            ToolNative.Check(ToolNative.GetExitCodeProcess(_process, out uint code));
            return code;
        }
    }
    public void CloseJob() => _job.Dispose();

    public async Task WaitForExitAsync(CancellationToken cancellationToken) {
        while (true) {
            uint result = ToolNative.WaitForSingleObject(_process, 0);
            if (result == 0)
                return;
            if (result != 258)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose() {
        if (_disposed)
            return;
        _disposed = true;
        _job.Dispose();
        uint result = ToolNative.WaitForSingleObject(_process, 5000);
        Input.Dispose();
        Output.Dispose();
        Error.Dispose();
        _thread.Dispose();
        _process.Dispose();
        if (result != 0)
            throw new ToolCleanupException("Tool exit was not confirmed; retain its disk for manual recovery.");
    }

    internal static string Quote(string value) {
        if (value.Contains('\0'))
            throw new ArgumentException("Embedded NUL is forbidden.");
        var text = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in value) {
            if (character == '\\') {
                slashes++;
                continue;
            }
            if (character == '"')
                text.Append('\\', slashes * 2 + 1).Append('"');
            else
                text.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }
}

internal sealed class ToolCleanupException(string message) : IOException(message);

internal static class ToolNative {
    public static void Check(bool passed) {
        if (!passed)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct SecurityAttributes {
        public int Length; public nint Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfo {
        public uint Size; public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize; public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfoEx {
        public StartupInfo Info; public nint Attributes;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation {
        public nint Process, Thread; public uint ProcessId, ThreadId;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct BasicLimits {
        public long ProcessTime, JobTime; public uint LimitFlags; public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit; public nuint Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct IoCounters {
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct ExtendedLimits {
        public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern SafeFileHandle CreateJobObjectW(nint security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, uint size);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32.dll")] public static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool CreateProcessW(string executable, StringBuilder commandLine, nint processSecurity, nint threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsProcessInJob(SafeFileHandle process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool contained);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern uint WaitForSingleObject(SafeFileHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetExitCodeProcess(SafeFileHandle process, out uint code);
}
