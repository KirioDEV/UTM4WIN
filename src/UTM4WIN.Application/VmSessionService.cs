using System.Collections.Concurrent;

namespace UTM4WIN.Application;

public sealed class VmSessionService(IVmExecutionLeaseProvider leases, IVmBackend backend) : IAsyncDisposable {
    private sealed class Entry(Guid id) {
        public readonly SemaphoreSlim Gate = new(1);
        public VmSessionStatus Status = new(id, VmSessionState.Stopped, false);
        public IVmExecutionLease? Lease;
        public IVmBackendSession? Backend;
        public Task? Monitor;
        public bool ExpectedExit;
        public DateTimeOffset? ShutdownRequested;
        public string LastDiagnostics = "";
    }
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private bool _disposed;
    public event Action<VmSessionStatus>? Changed;
    public IReadOnlyList<VmSessionStatus> Sessions => _entries.Values.Select(entry => Volatile.Read(ref entry.Status)).ToArray();
    public VmSessionStatus Get(Guid id) => _entries.TryGetValue(id, out var entry) ? Volatile.Read(ref entry.Status) : new(id, VmSessionState.Stopped, false);
    public string GetDiagnostics(Guid id) => _entries.TryGetValue(id, out var entry) ? entry.Backend?.Diagnostics ?? entry.LastDiagnostics : "";
    public IVmDisplaySession? GetDisplay(Guid id) => _entries.TryGetValue(id, out var entry) ? entry.Backend?.Display : null;

    private void Publish(Entry entry, VmSessionState state, string error = "") {
        var status = new VmSessionStatus(entry.Status.Id, state, entry.Lease is not null, error, entry.Backend?.ProcessId);
        if (status == entry.Status)
            return;
        Volatile.Write(ref entry.Status, status);
        Changed?.Invoke(status);
    }

    public async Task StartAsync(VmSnapshot snapshot, VmStartOptions options, CancellationToken cancellationToken = default) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var entry = _entries.GetOrAdd(snapshot.Configuration.Id, id => new(id));
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (entry.Lease is not null || entry.Backend is not null)
                throw new ConfigurationConflictException("This VM still owns a session. Stop it before starting again.");
            Publish(entry, VmSessionState.Starting);
            entry.ExpectedExit = false;
            entry.ShutdownRequested = null;
            entry.LastDiagnostics = "";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            try {
                entry.Lease = await leases.AcquireAsync(snapshot, deadline.Token).ConfigureAwait(false);
                Publish(entry, VmSessionState.Starting);
                entry.Backend = await backend.StartAsync(entry.Lease, options, deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                string state = await entry.Backend.QueryStatusAsync(deadline.Token).ConfigureAwait(false);
                if (state != "running")
                    throw new IOException("QEMU did not confirm a running virtual CPU.");
                Publish(entry, VmSessionState.Running);
                var started = entry.Backend;
                entry.Monitor = Task.Run(() => MonitorAsync(entry, started));
            } catch (Exception error) {
                bool uncertain = error is VmCleanupException;
                if (entry.Backend is not null) {
                    try {
                        await entry.Backend.ForceStopAsync(CancellationToken.None).ConfigureAwait(false);
                        await ReleaseAsync(entry).ConfigureAwait(false);
                    } catch (Exception cleanup) { uncertain = true; error = new VmCleanupException($"{error.Message} Cleanup: {cleanup.Message}"); }
                }
                if (!uncertain) {
                    entry.Lease?.Dispose();
                    entry.Lease = null;
                }
                Publish(entry, error is OperationCanceledException && !uncertain ? VmSessionState.Stopped : VmSessionState.Faulted, error.Message);
                throw;
            }
        } finally { entry.Gate.Release(); }
    }

    public async Task ControlAsync(Guid id, VmControl control, CancellationToken cancellationToken = default) {
        if (!_entries.TryGetValue(id, out var entry))
            throw new ConfigurationConflictException("This VM has no active session.");
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            var session = entry.Backend ?? throw new ConfigurationConflictException("This VM has no controllable process.");
            VmSessionState current = entry.Status.State;
            bool allowed = control switch {
                VmControl.Pause => current == VmSessionState.Running,
                VmControl.Resume => current == VmSessionState.Paused,
                VmControl.Shutdown => current is VmSessionState.Running or VmSessionState.Paused or VmSessionState.Stopping,
                _ => false
            };
            if (!allowed)
                throw new ConfigurationConflictException("This action is unavailable in the current VM state.");
            Publish(entry, control switch {
                VmControl.Pause => VmSessionState.Pausing,
                VmControl.Resume => VmSessionState.Resuming,
                _ => VmSessionState.Stopping
            });
            if (control == VmControl.Shutdown)
                entry.ShutdownRequested ??= DateTimeOffset.UtcNow;
            try {
                // A paused guest cannot handle its ACPI shutdown request until resumed.
                if (control == VmControl.Shutdown && current == VmSessionState.Paused)
                    await session.ExecuteAsync(VmControl.Resume, cancellationToken).ConfigureAwait(false);
                await session.ExecuteAsync(control, cancellationToken).ConfigureAwait(false);
                await ReconcileAsync(entry, cancellationToken).ConfigureAwait(false);
            } catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException) {
                // Command cancellation is not rollback. Reconcile using an independent deadline.
                try {
                    await ReconcileAsync(entry, CancellationToken.None).ConfigureAwait(false);
                } catch { Publish(entry, VmSessionState.Disconnected, "Control connection lost. The VM still owns its files; force stop remains available."); }
                throw;
            }
        } finally { entry.Gate.Release(); }
    }

    public async Task ForceStopAsync(Guid id, CancellationToken cancellationToken = default) {
        if (!_entries.TryGetValue(id, out var entry))
            return;
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            if (entry.Backend is null) {
                if (entry.Lease is not null)
                    throw new VmCleanupException("Uncertain startup cleanup requires manual recovery; files remain locked.");
                return;
            }
            Publish(entry, VmSessionState.Stopping);
            entry.ExpectedExit = true;
            await entry.Backend.ForceStopAsync(cancellationToken).ConfigureAwait(false);
            await ReleaseAsync(entry).ConfigureAwait(false);
            Publish(entry, VmSessionState.Stopped);
        } catch (Exception error) { Publish(entry, VmSessionState.Faulted, error.Message); throw; } finally { entry.Gate.Release(); }
    }

    private async Task ReconcileAsync(Entry entry, CancellationToken cancellationToken) {
        var session = entry.Backend!;
        if (session.Completion.IsCompleted) {
            await FinishExitAsync(entry).ConfigureAwait(false);
            return;
        }
        string state = await session.QueryStatusAsync(cancellationToken).ConfigureAwait(false);
        if (state == "shutdown") {
            entry.ExpectedExit = true;
            Publish(entry, VmSessionState.Stopping);
            try {
                await session.ExecuteAsync(VmControl.Quit, cancellationToken).ConfigureAwait(false);
            } catch (IOException) { /* QMP quit may close the stream before replying. Process exit is required. */ }
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            await FinishExitAsync(entry).ConfigureAwait(false);
        } else if (entry.Status.State == VmSessionState.Stopping && entry.ShutdownRequested is { } requested && DateTimeOffset.UtcNow - requested > TimeSpan.FromSeconds(30)) {
            Publish(entry, VmSessionState.Stopping, "The guest has not shut down after 30 seconds. Continue waiting, retry Shut down, or explicitly Force stop. Files remain owned.");
        } else if (entry.Status.State != VmSessionState.Stopping) {
            Publish(entry, state switch {
                "running" => VmSessionState.Running,
                "paused" => VmSessionState.Paused,
                _ => VmSessionState.Faulted
            },
                state is "running" or "paused" ? "" : $"Unsupported QEMU state: {state}. Files remain owned.");
        }
    }

    private async Task MonitorAsync(Entry entry, IVmBackendSession started) {
        while (true) {
            await Task.Delay(500).ConfigureAwait(false);
            await entry.Gate.WaitAsync().ConfigureAwait(false);
            try {
                if (!ReferenceEquals(entry.Backend, started))
                    return;
                if (entry.Backend.Completion.IsCompleted) {
                    await FinishExitAsync(entry).ConfigureAwait(false);
                    return;
                }
                if (entry.Status.State == VmSessionState.Disconnected)
                    continue;
                try {
                    await ReconcileAsync(entry, CancellationToken.None).ConfigureAwait(false);
                } catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException) {
                    Publish(entry, VmSessionState.Disconnected, "Control connection lost. Files remain owned. " + error.Message);
                }
            } finally { entry.Gate.Release(); }
        }
    }

    private async Task FinishExitAsync(Entry entry) {
        uint code = await entry.Backend!.Completion.ConfigureAwait(false);
        bool expected = entry.ExpectedExit && code == 0;
        await ReleaseAsync(entry).ConfigureAwait(false);
        Publish(entry, expected ? VmSessionState.Stopped : VmSessionState.Faulted,
            expected ? "" : $"QEMU exited unexpectedly ({code}). {entry.LastDiagnostics}");
    }

    private static async Task ReleaseAsync(Entry entry) {
        if (entry.Backend is not null) {
            await entry.Backend.Completion.ConfigureAwait(false);
            await entry.Backend.DisposeAsync().ConfigureAwait(false);
            entry.LastDiagnostics = entry.Backend.Diagnostics;
            entry.Backend = null;
        }
        entry.Lease?.Dispose();
        entry.Lease = null;
    }

    public async ValueTask DisposeAsync() {
        _disposed = true;
        foreach (var entry in _entries.Values)
            await ForceStopAsync(entry.Status.Id).ConfigureAwait(false);
    }
}
