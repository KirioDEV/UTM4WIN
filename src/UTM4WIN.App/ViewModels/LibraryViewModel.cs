using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using UTM4WIN.Application;
using UTM4WIN.Domain;

namespace UTM4WIN.App.ViewModels;

public interface ILibraryDialogs {
    Task CreateAsync(CreationWizardViewModel wizard);
    Task EditAsync(MetadataEditorViewModel editor, bool isNew, Func<VmConfiguration, Task<bool>> save);
    Task<bool> ConfirmRemoveAsync(string name);
}

public sealed record VmListItem(VmSnapshot Snapshot, string ResourceIssue = "") {
    public override string ToString() => Name;
    public string Name => Snapshot.Configuration.Name;
    public string Details => $"x86_64 · {Snapshot.Configuration.CpuCount} CPUs · {Snapshot.Configuration.MemoryMiB} MiB · {Snapshot.Configuration.ExecutionMode.ToString().ToUpperInvariant()}";
    public string Subtitle => $"Standard PC · {ExecutionLabel}";
    public string ExecutionLabel => Snapshot.Configuration.ExecutionMode == ExecutionMode.Whpx ? "Virtualization (WHPX)" : "Emulation (TCG)";
    public string MemoryLabel => $"{Snapshot.Configuration.MemoryMiB:N0} MiB";
    public string CpuLabel => $"{Snapshot.Configuration.CpuCount} virtual CPUs";
    public string StatusLabel => ResourceIssue.Length != 0 ? "Needs attention" : Snapshot.Configuration.Storage.IsEmpty ? "Metadata only" : "Configured";
    public string StorageLabel => Snapshot.Configuration.Storage.IsEmpty ? "No drives configured" : $"{Snapshot.Configuration.Storage.Length} drives";
    public string NetworkLabel => Snapshot.Configuration.Network == NetworkMode.None ? "Disabled" : "User-mode networking";
    public IReadOnlyList<StorageResource> Storage => Snapshot.Configuration.Storage;
}

public sealed partial class LibraryViewModel : ObservableObject {
    private readonly IVmRepository _repository;
    private readonly ILibraryDialogs _dialogs;
    private readonly ILogger<LibraryViewModel> _logger;
    private readonly IVmCreationService _creation;
    private readonly QemuToolSelection _tools;
    private readonly ILogger<CreationWizardViewModel> _creationLogger;
    [ObservableProperty]
    public partial VmListItem? SelectedMachine {
        get; set;
    }
    [ObservableProperty]
    public partial Guid? SelectedRetainedId {
        get; set;
    }
    [ObservableProperty]
    public partial string Error {
        get; set;
    }
    [ObservableProperty]
    public partial bool IsBusy {
        get; set;
    }
    [ObservableProperty]
    public partial bool IsEmpty {
        get; set;
    }
    [ObservableProperty]
    public partial bool HasRetainedEntries {
        get; set;
    }
    public ObservableCollection<VmListItem> Machines { get; } = [];
    public ObservableCollection<Guid> RetainedIds { get; } = [];
    public IAsyncRelayCommand ReloadCommand {
        get;
    }
    public IAsyncRelayCommand CreateCommand {
        get;
    }
    public IAsyncRelayCommand AddCommand {
        get;
    }
    public IAsyncRelayCommand EditCommand {
        get;
    }
    public IAsyncRelayCommand RemoveCommand {
        get;
    }
    public IAsyncRelayCommand RegisterCommand {
        get;
    }

    public LibraryViewModel(IVmRepository repository, ILibraryDialogs dialogs, ILogger<LibraryViewModel> logger,
        IVmCreationService creation, QemuToolSelection tools, ILogger<CreationWizardViewModel> creationLogger) {
        _repository = repository;
        _dialogs = dialogs;
        _logger = logger;
        _creation = creation;
        _tools = tools;
        _creationLogger = creationLogger;
        Error = "";
        IsEmpty = true;
        ReloadCommand = new AsyncRelayCommand(() => RunAsync(ReloadAsync), () => !IsBusy);
        CreateCommand = new AsyncRelayCommand(() => RunAsync(CreateAsync), () => !IsBusy);
        AddCommand = new AsyncRelayCommand(() => RunAsync(() => EditAsync(null)), () => !IsBusy);
        EditCommand = new AsyncRelayCommand(() => RunAsync(() => EditAsync(SelectedMachine)), () => !IsBusy && SelectedMachine is not null);
        RemoveCommand = new AsyncRelayCommand(() => RunAsync(RemoveAsync), () => !IsBusy && SelectedMachine is not null);
        RegisterCommand = new AsyncRelayCommand(() => RunAsync(RegisterAsync), () => !IsBusy && SelectedRetainedId is not null);
    }

    public bool HasSelectedMachine => SelectedMachine is not null;
    public bool NoSelectedMachine => SelectedMachine is null;

    partial void OnSelectedMachineChanged(VmListItem? value) {
        OnPropertyChanged(nameof(HasSelectedMachine));
        OnPropertyChanged(nameof(NoSelectedMachine));
        NotifyCommands();
    }
    partial void OnSelectedRetainedIdChanged(Guid? value) => NotifyCommands();
    partial void OnIsBusyChanged(bool value) => NotifyCommands();

    private void NotifyCommands() {
        ReloadCommand?.NotifyCanExecuteChanged();
        CreateCommand?.NotifyCanExecuteChanged();
        AddCommand?.NotifyCanExecuteChanged();
        EditCommand?.NotifyCanExecuteChanged();
        RemoveCommand?.NotifyCanExecuteChanged();
        RegisterCommand?.NotifyCanExecuteChanged();
    }

    private async Task RunAsync(Func<Task> operation) {
        if (IsBusy)
            return;
        IsBusy = true;
        Error = "";
        try {
            await operation();
        } catch (Exception exception) when (IsExpectedFailure(exception)) {
            Error = exception.Message;
            _logger.LogWarning(exception, "Library operation failed.");
        } finally { IsBusy = false; }
    }

    private async Task ReloadAsync() {
        Guid? selected = SelectedMachine?.Snapshot.Configuration.Id;
        var library = await _repository.ListAsync();
        Machines.Clear();
        foreach (var snapshot in library.Machines)
            Machines.Add(new(snapshot, string.Join(Environment.NewLine, library.Issues.Where(issue => issue.Id == snapshot.Configuration.Id).Select(issue => issue.Message))));
        SelectedMachine = Machines.FirstOrDefault(machine => machine.Snapshot.Configuration.Id == selected) ?? Machines.FirstOrDefault();
        IsEmpty = Machines.Count == 0;
        RetainedIds.Clear();
        foreach (Guid id in library.UnregisteredIds)
            RetainedIds.Add(id);
        SelectedRetainedId = RetainedIds.Count == 0 ? null : RetainedIds[0];
        HasRetainedEntries = RetainedIds.Count != 0;
        Error = string.Join(Environment.NewLine, library.Issues.Select(issue => $"{issue.Id}: {issue.Message}"));
        _logger.LogInformation("Loaded {MachineCount} VM metadata entries and {IssueCount} issues.", Machines.Count, library.Issues.Count);
    }

    private async Task EditAsync(VmListItem? machine) {
        bool isNew = machine is null;
        var editor = new MetadataEditorViewModel(machine?.Snapshot ?? new(VmConfiguration.CreateMetadata(""), ""));
        bool saved = false;
        Guid? savedId = null;
        await _dialogs.EditAsync(editor, isNew, async configuration => {
            try {
                var snapshot = isNew ? await _repository.CreateAsync(configuration) : await _repository.SaveAsync(configuration, editor.Revision);
                saved = true;
                savedId = snapshot.Configuration.Id;
                return true;
            } catch (Exception exception) when (IsExpectedFailure(exception)) {
                editor.Error = exception.Message;
                _logger.LogWarning(exception, "VM metadata save failed for {VmId}.", configuration.Id);
                return false;
            }
        });
        if (saved) {
            await ReloadAsync();
            SelectedMachine = Machines.FirstOrDefault(item => item.Snapshot.Configuration.Id == savedId);
        }
    }

    private async Task CreateAsync() {
        var wizard = new CreationWizardViewModel(_creation, _tools, _creationLogger);
        await _dialogs.CreateAsync(wizard);
        await ReloadAsync();
        if (wizard.CreatedMachine is not null)
            SelectedMachine = Machines.FirstOrDefault(item => item.Snapshot.Configuration.Id == wizard.CreatedMachine.Configuration.Id);
    }

    private async Task RemoveAsync() {
        var machine = SelectedMachine;
        if (machine is null || !await _dialogs.ConfirmRemoveAsync(machine.Name))
            return;
        await _repository.RemoveAsync(machine.Snapshot.Configuration.Id);
        await ReloadAsync();
    }

    private async Task RegisterAsync() {
        if (SelectedRetainedId is not Guid id)
            return;
        await _repository.RegisterAsync(id);
        await ReloadAsync();
    }

    private static bool IsExpectedFailure(Exception exception) => exception is ConfigurationException or ConfigurationConflictException or IOException or UnauthorizedAccessException;
}
