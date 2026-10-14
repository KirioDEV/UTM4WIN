using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using UTM4WIN.Application;
using UTM4WIN.Domain;

namespace UTM4WIN.App.ViewModels;

public interface ILibraryDialogs
{
    Task EditAsync(MetadataEditorViewModel editor, bool isNew, Func<VmConfiguration, Task<bool>> save);
    Task<bool> ConfirmRemoveAsync(string name);
}

public sealed record VmListItem(VmSnapshot Snapshot)
{
    public override string ToString() => Name;
    public string Name => Snapshot.Configuration.Name;
    public string Details => $"x86_64 · {Snapshot.Configuration.CpuCount} CPUs · {Snapshot.Configuration.MemoryMiB} MiB · {Snapshot.Configuration.ExecutionMode.ToString().ToUpperInvariant()}";
}

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly IVmRepository _repository;
    private readonly ILibraryDialogs _dialogs;
    private readonly ILogger<LibraryViewModel> _logger;
    [ObservableProperty] public partial VmListItem? SelectedMachine { get; set; }
    [ObservableProperty] public partial Guid? SelectedRetainedId { get; set; }
    [ObservableProperty] public partial string Error { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; }
    [ObservableProperty] public partial bool HasRetainedEntries { get; set; }
    public ObservableCollection<VmListItem> Machines { get; } = [];
    public ObservableCollection<Guid> RetainedIds { get; } = [];
    public IAsyncRelayCommand ReloadCommand { get; }
    public IAsyncRelayCommand AddCommand { get; }
    public IAsyncRelayCommand EditCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }
    public IAsyncRelayCommand RegisterCommand { get; }

    public LibraryViewModel(IVmRepository repository, ILibraryDialogs dialogs, ILogger<LibraryViewModel> logger)
    {
        _repository = repository;
        _dialogs = dialogs;
        _logger = logger;
        Error = "";
        IsEmpty = true;
        ReloadCommand = new AsyncRelayCommand(() => RunAsync(ReloadAsync), () => !IsBusy);
        AddCommand = new AsyncRelayCommand(() => RunAsync(() => EditAsync(null)), () => !IsBusy);
        EditCommand = new AsyncRelayCommand(() => RunAsync(() => EditAsync(SelectedMachine)), () => !IsBusy && SelectedMachine is not null);
        RemoveCommand = new AsyncRelayCommand(() => RunAsync(RemoveAsync), () => !IsBusy && SelectedMachine is not null);
        RegisterCommand = new AsyncRelayCommand(() => RunAsync(RegisterAsync), () => !IsBusy && SelectedRetainedId is not null);
    }

    partial void OnSelectedMachineChanged(VmListItem? value) => NotifyCommands();
    partial void OnSelectedRetainedIdChanged(Guid? value) => NotifyCommands();
    partial void OnIsBusyChanged(bool value) => NotifyCommands();

    private void NotifyCommands()
    {
        ReloadCommand?.NotifyCanExecuteChanged();
        AddCommand?.NotifyCanExecuteChanged();
        EditCommand?.NotifyCanExecuteChanged();
        RemoveCommand?.NotifyCanExecuteChanged();
        RegisterCommand?.NotifyCanExecuteChanged();
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (IsBusy) return;
        IsBusy = true;
        Error = "";
        try { await operation(); }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Error = exception.Message;
            _logger.LogWarning(exception, "Library operation failed.");
        }
        finally { IsBusy = false; }
    }

    private async Task ReloadAsync()
    {
        Guid? selected = SelectedMachine?.Snapshot.Configuration.Id;
        var library = await _repository.ListAsync();
        Machines.Clear();
        foreach (var snapshot in library.Machines) Machines.Add(new(snapshot));
        SelectedMachine = Machines.FirstOrDefault(machine => machine.Snapshot.Configuration.Id == selected);
        IsEmpty = Machines.Count == 0;
        RetainedIds.Clear();
        foreach (Guid id in library.UnregisteredIds) RetainedIds.Add(id);
        SelectedRetainedId = RetainedIds.Count == 0 ? null : RetainedIds[0];
        HasRetainedEntries = RetainedIds.Count != 0;
        Error = string.Join(Environment.NewLine, library.Issues.Select(issue => $"{issue.Id}: {issue.Message}"));
        _logger.LogInformation("Loaded {MachineCount} VM metadata entries and {IssueCount} issues.", Machines.Count, library.Issues.Count);
    }

    private async Task EditAsync(VmListItem? machine)
    {
        bool isNew = machine is null;
        var editor = new MetadataEditorViewModel(machine?.Snapshot ?? new(VmConfiguration.CreateMetadata(""), ""));
        bool saved = false;
        Guid? savedId = null;
        await _dialogs.EditAsync(editor, isNew, async configuration =>
        {
            try
            {
                var snapshot = isNew ? await _repository.CreateAsync(configuration) : await _repository.SaveAsync(configuration, editor.Revision);
                saved = true;
                savedId = snapshot.Configuration.Id;
                return true;
            }
            catch (Exception exception) when (IsExpectedFailure(exception))
            {
                editor.Error = exception.Message;
                _logger.LogWarning(exception, "VM metadata save failed for {VmId}.", configuration.Id);
                return false;
            }
        });
        if (saved)
        {
            await ReloadAsync();
            SelectedMachine = Machines.FirstOrDefault(item => item.Snapshot.Configuration.Id == savedId);
        }
    }

    private async Task RemoveAsync()
    {
        var machine = SelectedMachine;
        if (machine is null || !await _dialogs.ConfirmRemoveAsync(machine.Name)) return;
        await _repository.RemoveAsync(machine.Snapshot.Configuration.Id);
        await ReloadAsync();
    }

    private async Task RegisterAsync()
    {
        if (SelectedRetainedId is not Guid id) return;
        await _repository.RegisterAsync(id);
        await ReloadAsync();
    }

    private static bool IsExpectedFailure(Exception exception) => exception is ConfigurationException or ConfigurationConflictException or IOException or UnauthorizedAccessException;
}
