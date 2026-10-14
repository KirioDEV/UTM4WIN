using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using UTM4WIN.Application;

namespace UTM4WIN.App.ViewModels;

public sealed partial class SessionViewModel : ObservableObject {
    private readonly VmSessionService _sessions;
    private readonly LibraryViewModel _library;
    private readonly LibraryDialogs _dialogs;
    private readonly QemuToolSelection _tools;
    private readonly ILogger<SessionViewModel> _logger;
    private CancellationTokenSource? _startup;
    private string _actionError = "";
    public Action<Action> Dispatch { get; set; } = callback => callback();
    public Action<VmSnapshot, IVmDisplaySession>? ShowConsole {
        get; set;
    }
    public IRelayCommand OpenConsoleCommand {
        get;
    }
    [ObservableProperty]
    public partial string StatusLabel {
        get; set;
    }
    [ObservableProperty]
    public partial string Error {
        get; set;
    }
    [ObservableProperty]
    public partial bool OwnsResources {
        get; set;
    }
    public IAsyncRelayCommand StartCommand {
        get;
    }
    public IAsyncRelayCommand PauseCommand {
        get;
    }
    public IAsyncRelayCommand ResumeCommand {
        get;
    }
    public IAsyncRelayCommand ShutdownCommand {
        get;
    }
    public IAsyncRelayCommand ForceStopCommand {
        get;
    }
    public IRelayCommand CancelStartCommand {
        get;
    }
    public IAsyncRelayCommand DiagnosticsCommand {
        get;
    }
    public bool HasActiveSessions => _sessions.Sessions.Any(status => status.OwnsResources);

    public SessionViewModel(VmSessionService sessions, LibraryViewModel library, LibraryDialogs dialogs, QemuToolSelection tools, ILogger<SessionViewModel> logger) {
        _sessions = sessions;
        _library = library;
        _dialogs = dialogs;
        _tools = tools;
        _logger = logger;
        StatusLabel = "No VM selected";
        OpenConsoleCommand = new RelayCommand(OpenConsole, () => !library.IsBusy && sessions.GetDisplay(Current.Id) is not null);
        Error = "";
        StartCommand = new AsyncRelayCommand(() => RunAsync(StartAsync), () => !library.IsBusy && library.SelectedMachine is { } machine && machine.Storage.Count > 0 && !sessions.Get(machine.Snapshot.Configuration.Id).OwnsResources);
        PauseCommand = new AsyncRelayCommand(() => RunAsync(() => ControlAsync(VmControl.Pause)), () => CanControl(VmSessionState.Running));
        ResumeCommand = new AsyncRelayCommand(() => RunAsync(() => ControlAsync(VmControl.Resume)), () => CanControl(VmSessionState.Paused));
        ShutdownCommand = new AsyncRelayCommand(() => RunAsync(() => ControlAsync(VmControl.Shutdown)), () => !library.IsBusy && Current.State is VmSessionState.Running or VmSessionState.Paused or VmSessionState.Stopping);
        ForceStopCommand = new AsyncRelayCommand(() => RunAsync(ForceStopAsync), () => !library.IsBusy && Current.OwnsResources);
        CancelStartCommand = new RelayCommand(() => _startup?.Cancel(), () => _startup is not null);
        DiagnosticsCommand = new AsyncRelayCommand(() => RunAsync(() => _dialogs.ShowDiagnosticsAsync(_sessions.GetDiagnostics(Current.Id))), () => !library.IsBusy && library.SelectedMachine is not null);
        library.PropertyChanged += (_, args) => {
            if (args.PropertyName == nameof(LibraryViewModel.SelectedMachine))
                _actionError = "";
            if (args.PropertyName is nameof(LibraryViewModel.SelectedMachine) or nameof(LibraryViewModel.IsBusy))
                Refresh();
        };
        sessions.Changed += status => Dispatch(() => { logger.LogInformation("VM {VmId}: {State}, owns resources: {OwnsResources}", status.Id, status.State, status.OwnsResources); Refresh(); });
    }

    private VmSessionStatus Current => _library.SelectedMachine is { } machine ? _sessions.Get(machine.Snapshot.Configuration.Id) : new(Guid.Empty, VmSessionState.Stopped, false);
    private bool CanControl(VmSessionState state) => !_library.IsBusy && Current.State == state;
    private void Refresh() {
        var status = Current;
        StatusLabel = status.Id == Guid.Empty ? "No VM selected" : status.State.ToString();
        OwnsResources = status.OwnsResources;
        Error = status.Error.Length > 0 ? status.Error : _actionError;
        OnPropertyChanged(nameof(HasActiveSessions));
        StartCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        ShutdownCommand.NotifyCanExecuteChanged();
        ForceStopCommand.NotifyCanExecuteChanged();
        CancelStartCommand.NotifyCanExecuteChanged();
        DiagnosticsCommand.NotifyCanExecuteChanged();
        OpenConsoleCommand.NotifyCanExecuteChanged();
        _library.RefreshSessionCommands();
    }

    private async Task RunAsync(Func<Task> action) {
        if (_library.IsBusy)
            return;
        _actionError = "";
        _library.IsBusy = true;
        Error = "";
        try {
            await action();
        } catch (Exception error) when (error is IOException or ConfigurationException or ConfigurationConflictException or OperationCanceledException or TimeoutException) {
            _actionError = error is OperationCanceledException ? "Startup canceled; owned process cleanup completed or its recovery status is shown." : error.Message;
            _logger.LogWarning(error, "VM action failed.");
        } finally { _library.IsBusy = false; Refresh(); }
    }

    private async Task StartAsync() {
        var machine = _library.SelectedMachine!;
        string candidate = _tools.SystemToolPath;
        if (candidate.Length == 0 && _tools.ImageToolPath.Length > 0)
            candidate = Path.Combine(Path.GetDirectoryName(_tools.ImageToolPath)!, "qemu-system-x86_64.exe");
        var options = await _dialogs.ConfirmStartAsync(machine.Name, candidate);
        if (options is null)
            return;
        _tools.SystemToolPath = options.Executable;
        _startup = new();
        Refresh();
        try {
            await _sessions.StartAsync(machine.Snapshot, options, _startup.Token);
            if (_sessions.GetDisplay(machine.Snapshot.Configuration.Id) is { } display)
                ShowConsole?.Invoke(machine.Snapshot, display);
        } finally { _startup.Dispose(); _startup = null; }
    }

    private void OpenConsole() {
        if (_library.SelectedMachine is { } machine && _sessions.GetDisplay(machine.Snapshot.Configuration.Id) is { } display)
            ShowConsole?.Invoke(machine.Snapshot, display);
    }

    private async Task ControlAsync(VmControl control) {
        Guid id = _library.SelectedMachine!.Snapshot.Configuration.Id;
        await _sessions.ControlAsync(id, control);
        if (control == VmControl.Shutdown && _sessions.Get(id).OwnsResources)
            _actionError = "Shutdown requested. Continue waiting, retry Shut down, or choose Force stop if the guest does not respond.";
    }
    private async Task ForceStopAsync() {
        var machine = _library.SelectedMachine!;
        if (await _dialogs.ConfirmForceStopAsync(machine.Name))
            await _sessions.ForceStopAsync(machine.Snapshot.Configuration.Id);
    }

    public async Task<bool> PrepareCloseAsync() {
        if (_library.IsBusy)
            return false;
        if (!HasActiveSessions)
            return true;
        _library.IsBusy = true;
        try {
            var choice = await _dialogs.ConfirmCloseSessionsAsync();
            if (choice == Microsoft.UI.Xaml.Controls.ContentDialogResult.None)
                return false;
            if (choice == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary) {
                foreach (var status in _sessions.Sessions.Where(status => status.OwnsResources))
                    await _sessions.ControlAsync(status.Id, VmControl.Shutdown);
                _actionError = "Shutdown requested. Wait for the guests to exit, or choose Force stop if a guest does not respond.";
                return false;
            }
            foreach (var status in _sessions.Sessions.Where(status => status.OwnsResources))
                await _sessions.ForceStopAsync(status.Id);
            return true;
        } catch (Exception error) when (error is IOException or TimeoutException or ConfigurationConflictException) { _actionError = error.Message; return false; } finally { _library.IsBusy = false; Refresh(); }
    }
}
