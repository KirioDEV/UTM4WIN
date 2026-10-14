using CommunityToolkit.Mvvm.ComponentModel;
using UTM4WIN.Application;
using UTM4WIN.Domain;

namespace UTM4WIN.App.ViewModels;

public sealed partial class MetadataEditorViewModel : ObservableObject {
    private readonly VmDraft _draft;

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