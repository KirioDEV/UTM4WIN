using System.Collections.Immutable;

namespace UTM4WIN.Domain;

public enum GuestArchitecture { X86_64 }
public enum ExecutionMode { Whpx, Tcg }
public enum FirmwareKind { Bios }
public enum NetworkMode { None, User }
public enum DisplayKind { Vnc }
public enum ResourceOwnership { Owned, External }
public enum DiskFormat { Qcow2, Raw, Iso }

public sealed record StorageResource
{
    public required Guid Id { get; init; }
    public required string Path { get; init; }
    public required ResourceOwnership Ownership { get; init; }
    public required DiskFormat Format { get; init; }
    public required bool IsReadOnly { get; init; }
}

public sealed record VmConfiguration
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Backend { get; init; }
    public required string Profile { get; init; }
    public required GuestArchitecture Architecture { get; init; }
    public required string Machine { get; init; }
    public required ExecutionMode ExecutionMode { get; init; }
    public required int CpuCount { get; init; }
    public required int MemoryMiB { get; init; }
    public required FirmwareKind Firmware { get; init; }
    public required NetworkMode Network { get; init; }
    public required DisplayKind Display { get; init; }
    public required ImmutableArray<StorageResource> Storage { get; init; }

    public static VmConfiguration CreateMetadata(string name) => new()
    {
        Id = Guid.NewGuid(), Name = name, Backend = "qemu", Profile = "linux-x86_64-bios",
        Architecture = GuestArchitecture.X86_64, Machine = "pc", ExecutionMode = ExecutionMode.Whpx,
        CpuCount = 2, MemoryMiB = 2048, Firmware = FirmwareKind.Bios,
        Network = NetworkMode.None, Display = DisplayKind.Vnc, Storage = []
    };
}
