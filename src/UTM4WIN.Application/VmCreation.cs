using UTM4WIN.Domain;

namespace UTM4WIN.Application;

public sealed record VmCreationRequest(
    VmConfiguration Configuration,
    int DiskSizeGiB,
    string IsoPath,
    string QemuImgPath,
    bool DiscardIncompleteDisk);

public interface IVmCreationService {
    Task<VmSnapshot> CreateAsync(VmCreationRequest request, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IDiskImageService {
    Task<ICreatedDisk> CreateAsync(string executable, string destination, long sizeBytes,
        CancellationToken cancellationToken = default);
}

public interface ICreatedDisk : IAsyncDisposable {
    void Retain();
}

public static class VmCreationValidator {
    public static void Validate(VmCreationRequest request) {
        var issues = ConfigurationValidator.Validate(request.Configuration).ToList();
        if (!request.Configuration.Storage.IsEmpty)
            issues.Add(new("Storage", "Creation requires a new configuration without existing storage."));
        if (request.DiskSizeGiB is < 1 or > 2048)
            issues.Add(new("DiskSizeGiB", "Disk capacity must be between 1 and 2048 GiB."));
        if (!ConfigurationValidator.IsExternalPath(request.IsoPath) ||
            !request.IsoPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            issues.Add(new("IsoPath", "Choose an absolute local ISO file path."));
        if (!ConfigurationValidator.IsExternalPath(request.QemuImgPath) ||
            !request.QemuImgPath.EndsWith("\\qemu-img.exe", StringComparison.OrdinalIgnoreCase))
            issues.Add(new("QemuImgPath", "Choose the qualified qemu-img.exe from your QEMU installation."));
        if (!request.DiscardIncompleteDisk)
            issues.Add(
                new("Cleanup", "Confirm removal of this operation's incomplete disk on cancellation or failure."));
        if (issues.Count != 0)
            throw new ConfigurationException(string.Join(Environment.NewLine, issues.Select(issue => issue.Message)));
    }
}