using System.Text.RegularExpressions;

namespace UTM4WIN.Domain;

public sealed record ValidationIssue(string Field, string Message);

public static partial class ConfigurationValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(VmConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var issues = new List<ValidationIssue>();
        void Check(bool valid, string field, string message)
        {
            if (!valid) issues.Add(new(field, message));
        }

        Check(configuration.Id != Guid.Empty, nameof(configuration.Id), "A VM must have a nonempty ID.");
        Check(IsSafeName(configuration.Name), nameof(configuration.Name), "Use 1–80 characters without Windows reserved names, trailing spaces or special path characters.");
        Check(configuration.Backend == "qemu" && configuration.Profile == "linux-x86_64-bios" &&
            configuration.Architecture == GuestArchitecture.X86_64 && configuration.Machine == "pc" &&
            configuration.Firmware == FirmwareKind.Bios, "Profile", "Only the x86_64 Linux BIOS metadata profile is supported.");
        Check(Enum.IsDefined(configuration.ExecutionMode), "ExecutionMode", "Choose WHPX or TCG explicitly.");
        Check(configuration.CpuCount is >= 1 and <= 64, "CpuCount", "CPU count must be between 1 and 64.");
        Check(configuration.MemoryMiB is >= 128 and <= 262144, "MemoryMiB", "Memory must be between 128 and 262144 MiB.");
        Check(Enum.IsDefined(configuration.Network), "Network", "Unsupported network mode.");
        Check(configuration.Display == DisplayKind.Vnc, "Display", "Unsupported display type.");
        Check(!configuration.Storage.IsDefault && configuration.Storage.Length <= 16, "Storage", "Storage must be present and contain at most 16 resources.");
        if (configuration.Storage.IsDefault) return issues;
        var ids = new HashSet<Guid>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in configuration.Storage)
        {
            if (resource is null) { issues.Add(new("Storage", "A storage resource cannot be null.")); continue; }
            Check(resource.Id != Guid.Empty && ids.Add(resource.Id), "Storage.Id", "Resource IDs must be nonempty and unique.");
            Check(Enum.IsDefined(resource.Format) && Enum.IsDefined(resource.Ownership), "Storage", "Unsupported storage format or ownership.");
            bool owned = resource.Ownership == ResourceOwnership.Owned;
            bool safe = owned ? IsOwnedPath(resource.Path) : IsExternalPath(resource.Path);
            Check(safe && paths.Add(resource.Path ?? ""), "Storage.Path", "Use unique disk paths: owned files under disks/, or an absolute external file path.");
            Check(owned || resource.IsReadOnly, "Storage.IsReadOnly", "External resources must be read-only in this milestone.");
            Check(resource.Format != DiskFormat.Iso || resource.IsReadOnly, "Storage.IsReadOnly", "ISO images must be read-only.");
        }
        return issues;
    }

    public static bool IsSafeName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 80 &&
        name == name.Trim() && !name.EndsWith('.') && !name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)) &&
        !ReservedName().IsMatch(name);

    public static bool IsOwnedPath(string? path) => path is not null && path.StartsWith("disks/", StringComparison.Ordinal) &&
        path.Split('/').Length == 2 && IsSafeName(path[6..]);

    public static bool IsExternalPath(string? path) => path is not null && path.Length > 3 &&
        char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\' &&
        path[3..].Split('\\').All(IsSafeName);

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])($|\.)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedName();
}
