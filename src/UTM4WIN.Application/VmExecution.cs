using UTM4WIN.Domain;

namespace UTM4WIN.Application;

public enum VmSessionState {
    Stopped, Starting, Running, Pausing, Paused, Resuming, Stopping, Disconnected, Faulted
}
public enum VmControl {
    Pause, Resume, Shutdown, Quit
}
public sealed record VmStartOptions(string Executable, bool ExternalDisplay = true, bool BootInstallationMedia = true, bool IntegratedDisplay = false);
public sealed record VmSessionStatus(Guid Id, VmSessionState State, bool OwnsResources, string Error = "", uint? ProcessId = null);
public sealed record ExecutionResource(StorageResource Configuration, string FullPath);

public interface IVmExecutionLease : IDisposable {
    VmSnapshot Snapshot {
        get;
    }
    string Directory {
        get;
    }
    IReadOnlyList<ExecutionResource> Resources {
        get;
    }
}

public interface IVmExecutionLeaseProvider {
    Task<IVmExecutionLease> AcquireAsync(VmSnapshot snapshot, CancellationToken cancellationToken = default);
}

public interface IVmBackend {
    // Failed startup must confirm child exit before throwing, or report cleanup uncertainty.
    Task<IVmBackendSession> StartAsync(IVmExecutionLease lease, VmStartOptions options, CancellationToken cancellationToken);
}

public interface IVmBackendSession : IAsyncDisposable {
    IVmDisplaySession? Display => null;
    uint ProcessId {
        get;
    }
    Task<uint> Completion {
        get;
    }
    string Diagnostics {
        get;
    }
    Task<string> QueryStatusAsync(CancellationToken cancellationToken);
    Task ExecuteAsync(VmControl control, CancellationToken cancellationToken);
    Task ForceStopAsync(CancellationToken cancellationToken);
}

public sealed class VmCleanupException(string message) : IOException(message);
