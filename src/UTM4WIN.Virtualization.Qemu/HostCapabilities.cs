using System.Runtime.InteropServices;
using UTM4WIN.Application;
using UTM4WIN.Domain;

namespace UTM4WIN.Virtualization.Qemu;

internal static class HostCapabilities {
    public static void Check(VmConfiguration configuration) {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory))
            throw new IOException("Windows memory availability could not be checked.");
        ValidateCapacity(configuration, memory.AvailablePhysical, Environment.ProcessorCount);
        if (configuration.ExecutionMode != ExecutionMode.Whpx)
            return;
        try {
            int result = WHvGetCapability(0, out int present, sizeof(int), out uint written);
            if (result < 0 || written != sizeof(int) || present == 0)
                throw new ConfigurationException("The Windows hypervisor is unavailable. Choose TCG explicitly or review the host requirements; Windows features were not changed.");
        } catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) {
            throw new ConfigurationException("Windows Hypervisor Platform is unavailable. Choose TCG explicitly; Windows features were not changed.");
        }
    }

    internal static void ValidateCapacity(VmConfiguration configuration, ulong availableMemory, int availableProcessors) {
        ulong requested = checked((ulong)configuration.MemoryMiB * 1048576);
        if (configuration.CpuCount > availableProcessors || requested + 536870912 > availableMemory)
            throw new ConfigurationException("VM allocation exceeds available host capacity. Reduce CPU/memory allocation or free host memory; at least 512 MiB must remain available for Windows.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
    [DllImport("WinHvPlatform.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WHvGetCapability(uint code, out int buffer, uint size, out uint written);
}
