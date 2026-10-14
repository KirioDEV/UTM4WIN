using CommunityToolkit.Mvvm.ComponentModel;
using UTM4WIN.Application;
using UTM4WIN.Domain;

namespace UTM4WIN.App.ViewModels;

public sealed partial class MetadataEditorViewModel : ObservableObject {
    private readonly VmDraft _draft;
    [ObservableProperty]
    public partial int SectionIndex {
        get; set;
    }
    public bool IsInfoSection => SectionIndex == 0;
    public bool IsSystemSection => SectionIndex == 1;
    public bool IsDrivesSection => SectionIndex == 2;
    public bool IsNetworkSection => SectionIndex == 3;
    public bool IsDisplaySection => SectionIndex == 4;
    public VmConfiguration OriginalConfiguration => _draft.Configuration;
    public string NetworkLabel => OriginalConfiguration.Network == NetworkMode.None ? "Disabled" : "User-mode networking";
    public string DriveSummary => OriginalConfiguration.Storage.IsEmpty ? "No drives configured. Use Create VM to prepare installation media and a disk." : "Managed disks stay in the VM directory. External installation media is read-only.";
    partial void OnSectionIndexChanged(int value) {
        foreach (string property in new[] { nameof(IsInfoSection), nameof(IsSystemSection), nameof(IsDrivesSection), nameof(IsNetworkSection), nameof(IsDisplaySection) })
            OnPropertyChanged(property);
    }

    [ObservableProperty]
    public partial string Name {
        get;
        set;
    }

    [ObservableProperty]
    public partial double CpuCount {
        get;
        set;
    }

    [ObservableProperty]
    public partial double MemoryMiB {
        get;
        set;
    }

    [ObservableProperty]
    public partial int ExecutionModeIndex {
        get;
        set;
    }

    [ObservableProperty]
    public partial string Error {
        get;
        set;
    }

    public MetadataEditorViewModel(VmSnapshot snapshot) {
        _draft = new(snapshot);
        Name = snapshot.Configuration.Name;
        CpuCount = snapshot.Configuration.CpuCount;
        MemoryMiB = snapshot.Configuration.MemoryMiB;
        ExecutionModeIndex = (int)snapshot.Configuration.ExecutionMode;
        Error = "";
    }

    public string Revision => _draft.Revision;

    public bool HasChanges => Name != _draft.Configuration.Name || CpuCount != _draft.Configuration.CpuCount ||
                              MemoryMiB != _draft.Configuration.MemoryMiB ||
                              ExecutionModeIndex != (int)_draft.Configuration.ExecutionMode;

    public VmConfiguration? Validate() {
        if (!double.IsFinite(CpuCount) || CpuCount != Math.Truncate(CpuCount) || CpuCount is < 1 or > 64 ||
            !double.IsFinite(MemoryMiB) || MemoryMiB != Math.Truncate(MemoryMiB) || MemoryMiB is < 128 or > 262144) {
            Error = "Use whole numbers: 1–64 CPUs and 128–262144 MiB of memory.";
            return null;
        }

        var configuration = _draft.Configuration with {
            Name = Name,
            CpuCount = (int)CpuCount,
            MemoryMiB = (int)MemoryMiB,
            ExecutionMode = (ExecutionMode)ExecutionModeIndex
        };
        var issues = ConfigurationValidator.Validate(configuration);
        Error = string.Join(Environment.NewLine, issues.Select(issue => issue.Message));
        return issues.Count == 0 ? configuration : null;
    }
}
