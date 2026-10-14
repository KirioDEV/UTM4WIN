using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using UTM4WIN.Application;
using UTM4WIN.Domain;

namespace UTM4WIN.App.ViewModels;

public sealed class QemuToolSelection {
    public string SystemToolPath { get; set; } = "";
    public string ImageToolPath { get; set; } = "";
}

public sealed partial class CreationWizardViewModel : ObservableObject {
    private readonly IVmCreationService _creation;
    private readonly QemuToolSelection _tools;
    private readonly ILogger<CreationWizardViewModel> _logger;
    private readonly string _originalToolPath;
    private readonly Guid _id = Guid.NewGuid();
    private CancellationTokenSource? _cancellation;
    private int _step;
    [ObservableProperty]
    public partial string Name {
        get; set;
    }
    [ObservableProperty]
    public partial double CpuCount {
        get; set;
    }
    [ObservableProperty]
    public partial double MemoryMiB {
        get; set;
    }
    [ObservableProperty]
    public partial int ExecutionModeIndex {
        get; set;
    }
    [ObservableProperty]
    public partial double DiskSizeGiB {
        get; set;
    }
    [ObservableProperty]
    public partial string IsoPath {
        get; set;
    }
    [ObservableProperty]
    public partial string QemuImgPath {
        get; set;
    }
    [ObservableProperty]
    public partial bool DiscardIncompleteDisk {
        get; set;
    }
    [ObservableProperty]
    public partial bool IsCreating {
        get; set;
    }
    [ObservableProperty]
    public partial string Status {
        get; set;
    }
    [ObservableProperty]
    public partial string Error {
        get; set;
    }
    public VmSnapshot? CreatedMachine {
        get; private set;
    }
    public IRelayCommand CancelCreationCommand {
        get;
    }

    public CreationWizardViewModel(IVmCreationService creation, QemuToolSelection tools, ILogger<CreationWizardViewModel> logger) {
        _creation = creation;
        _tools = tools;
        _logger = logger;
        _originalToolPath = tools.ImageToolPath;
        Name = "";
        CpuCount = 2;
        MemoryMiB = 2048;
        DiskSizeGiB = 32;
        IsoPath = "";
        QemuImgPath = _originalToolPath;
        Status = "";
        Error = "";
        CancelCreationCommand = new RelayCommand(() => { _cancellation?.Cancel(); Status = "Canceling creation and waiting for cleanup…"; }, () => IsCreating);
    }

    public int Step {
        get => _step;
        private set {
            if (!SetProperty(ref _step, value))
                return;
            foreach (string property in new[] { nameof(IsModeStep), nameof(IsOperatingSystemStep), nameof(IsMediaStep), nameof(IsGeneralStep), nameof(IsStorageStep), nameof(IsReviewStep), nameof(StepTitle), nameof(Summary) })
                OnPropertyChanged(property);
        }
    }
    public bool IsModeStep => Step == 0;
    public bool IsOperatingSystemStep => Step == 1;
    public bool IsMediaStep => Step == 2;
    public bool IsGeneralStep => Step == 3;
    public bool IsStorageStep => Step == 4;
    public bool IsReviewStep => Step == 5;
    public bool CanEdit => !IsCreating;
    public string StepTitle => Step switch { 0 => "Start", 1 => "Operating System", 2 => "Linux", 3 => "Hardware", 4 => "Storage", _ => "Summary" };
    public string Summary => $"{Name}\nLinux x86_64 · BIOS\n{CpuCount} CPUs · {MemoryMiB} MiB · {((ExecutionMode)ExecutionModeIndex).ToString().ToUpperInvariant()}\n{DiskSizeGiB} GiB sparse qcow2 disk\nISO: {IsoPath}\nThe ISO stays at its current location and is read-only. The disk grows as the guest writes data; sufficient free space is still required for future use.";
    public bool HasChanges => Name != "" || CpuCount != 2 || MemoryMiB != 2048 || ExecutionModeIndex != 0 || DiskSizeGiB != 32 ||
        IsoPath != "" || QemuImgPath != _originalToolPath || DiscardIncompleteDisk;

    partial void OnIsCreatingChanged(bool value) {
        OnPropertyChanged(nameof(CanEdit));
        CancelCreationCommand?.NotifyCanExecuteChanged();
    }

    public bool SelectExecutionMode(ExecutionMode mode) {
        if (IsCreating || Step != 0 || !Enum.IsDefined(mode))
            return false;
        ExecutionModeIndex = (int)mode;
        Error = "";
        Step = 1;
        return true;
    }

    public bool SelectLinux() {
        if (IsCreating || Step != 1)
            return false;
        Error = "";
        Step = 2;
        return true;
    }

    public bool Next() {
        if (IsCreating || Step < 2 || Step >= 5)
            return false;
        Error = "";
        if (Step == 2 && (!ConfigurationValidator.IsExternalPath(IsoPath) || !IsoPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))) {
            Error = "Choose an absolute local Linux installation ISO path.";
            return false;
        }
        if (Step == 3 && GetConfiguration() is null || Step == 4 && GetRequest(requireConsent: false) is null)
            return false;
        Step++;
        return true;
    }

    public void Back() {
        if (IsCreating || Step == 0)
            return;
        Error = "";
        Step--;
    }

    public async Task<bool> CreateAsync() {
        if (IsCreating || CreatedMachine is not null || Step != 5)
            return false;
        Error = "";
        var request = GetRequest(requireConsent: true);
        if (request is null)
            return false;
        IsCreating = true;
        _cancellation = new();
        try {
            CreatedMachine = await _creation.CreateAsync(request, new Progress<string>(message => Status = message), _cancellation.Token);
            _tools.ImageToolPath = QemuImgPath;
            Status = "Virtual machine created.";
            _logger.LogInformation("Created VM {VmId} with a managed disk.", CreatedMachine.Configuration.Id);
            return true;
        } catch (OperationCanceledException) { Error = "Creation canceled. Any committed files remain available as retained metadata after closing and reloading."; return false; } catch (Exception exception) when (exception is ConfigurationException or ConfigurationConflictException or IOException or UnauthorizedAccessException) {
            Error = exception.Message;
            _logger.LogWarning(exception, "VM creation failed for {VmId}.", _id);
            return false;
        } finally { _cancellation.Dispose(); _cancellation = null; IsCreating = false; }
    }

    private VmConfiguration? GetConfiguration() {
        if (!double.IsFinite(CpuCount) || CpuCount != Math.Truncate(CpuCount) || CpuCount is < 1 or > 64 ||
            !double.IsFinite(MemoryMiB) || MemoryMiB != Math.Truncate(MemoryMiB) || MemoryMiB is < 128 or > 262144) {
            Error = "Use whole numbers: 1–64 CPUs and 128–262144 MiB memory.";
            return null;
        }
        var configuration = VmConfiguration.CreateMetadata(Name) with {
            Id = _id,
            CpuCount = (int)CpuCount,
            MemoryMiB = (int)MemoryMiB,
            ExecutionMode = (ExecutionMode)ExecutionModeIndex
        };
        var issues = ConfigurationValidator.Validate(configuration);
        if (issues.Count == 0)
            return configuration;
        Error = string.Join(Environment.NewLine, issues.Select(issue => issue.Message));
        return null;
    }

    private VmCreationRequest? GetRequest(bool requireConsent) {
        var configuration = GetConfiguration();
        if (configuration is null)
            return null;
        if (!double.IsFinite(DiskSizeGiB) || DiskSizeGiB != Math.Truncate(DiskSizeGiB) || DiskSizeGiB is < 1 or > 2048) {
            Error = "Use a whole disk capacity between 1 and 2048 GiB.";
            return null;
        }
        var request = new VmCreationRequest(configuration, (int)DiskSizeGiB, IsoPath, QemuImgPath, !requireConsent || DiscardIncompleteDisk);
        try {
            VmCreationValidator.Validate(request);
            return request;
        } catch (ConfigurationException exception) { Error = exception.Message; return null; }
    }
}
