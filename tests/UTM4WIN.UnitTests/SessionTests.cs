using UTM4WIN.Application;
using UTM4WIN.Domain;

namespace UTM4WIN.UnitTests;

[TestClass]
public sealed class SessionTests {
    [TestMethod]
    public async Task DisposalWaitsForInFlightLeaseAcquisitionAndStopsItsProcess() {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new Lease { AcquireBarrier = barrier.Task };
        var backend = new Backend();
        var service = new VmSessionService(lease, backend);
        var startup = service.StartAsync(lease.Snapshot, new("unused"));
        await lease.Entered.Task;
        var disposal = service.DisposeAsync().AsTask();
        Assert.IsFalse(disposal.IsCompleted);
        barrier.SetResult();
        await Task.WhenAll(startup, disposal);
        Assert.IsTrue(lease.Disposed);
        Assert.IsTrue(backend.Session.Completion.IsCompleted);
        Assert.IsFalse(service.Get(lease.Snapshot.Configuration.Id).OwnsResources);
    }

    [TestMethod]
    public async Task CanceledPauseReconcilesExecutedCommandInsteadOfAssumingRollback() {
        var lease = new Lease();
        var backend = new Backend();
        await using var service = new VmSessionService(lease, backend);
        await service.StartAsync(lease.Snapshot, new("unused"));
        backend.Session.CancelAfterControl = true;
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ControlAsync(lease.Snapshot.Configuration.Id, VmControl.Pause));
        Assert.AreEqual(VmSessionState.Paused, service.Get(lease.Snapshot.Configuration.Id).State);
        Assert.IsTrue(service.Get(lease.Snapshot.Configuration.Id).OwnsResources);
        Assert.IsFalse(lease.Disposed);
    }

    [TestMethod]
    public async Task ShutdownAcknowledgmentKeepsOwnershipUntilConfirmedProcessExit() {
        var lease = new Lease();
        var backend = new Backend();
        await using var service = new VmSessionService(lease, backend);
        Guid id = lease.Snapshot.Configuration.Id;
        await service.StartAsync(lease.Snapshot, new("unused"));
        Assert.AreEqual(VmSessionState.Running, service.Get(id).State);
        await Assert.ThrowsAsync<ConfigurationConflictException>(() => service.StartAsync(lease.Snapshot, new("unused")));
        await service.ControlAsync(id, VmControl.Pause);
        Assert.AreEqual(VmSessionState.Paused, service.Get(id).State);
        await service.ControlAsync(id, VmControl.Shutdown);
        Assert.AreEqual(VmSessionState.Stopping, service.Get(id).State);
        Assert.IsTrue(service.Get(id).OwnsResources);
        Assert.IsFalse(lease.Disposed);
        backend.Session.State = "shutdown";
        await WaitAsync(() => !service.Get(id).OwnsResources);
        Assert.IsTrue(lease.Disposed);
        Assert.AreEqual(VmSessionState.Stopped, service.Get(id).State);
    }

    [TestMethod]
    public async Task DisconnectPreservesLocksAndExplicitForceStopReleasesThem() {
        var lease = new Lease();
        var backend = new Backend();
        await using var service = new VmSessionService(lease, backend);
        Guid id = lease.Snapshot.Configuration.Id;
        await service.StartAsync(lease.Snapshot, new("unused"));
        backend.Session.Disconnected = true;
        await Assert.ThrowsAsync<IOException>(() => service.ControlAsync(id, VmControl.Pause));
        Assert.AreEqual(VmSessionState.Disconnected, service.Get(id).State);
        Assert.IsTrue(service.Get(id).OwnsResources);
        Assert.IsFalse(lease.Disposed);
        await service.ForceStopAsync(id);
        Assert.IsTrue(lease.Disposed);
        Assert.IsFalse(service.Get(id).OwnsResources);
    }

    [TestMethod]
    public async Task StartupCancellationReleasesLeaseOnlyAfterBackendCleanup() {
        var lease = new Lease();
        var backend = new Backend { FailStart = true };
        await using var service = new VmSessionService(lease, backend);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.StartAsync(lease.Snapshot, new("unused")));
        Assert.IsTrue(lease.Disposed);
        Assert.IsFalse(service.Get(lease.Snapshot.Configuration.Id).OwnsResources);
    }

    [TestMethod]
    public async Task UncertainStartupCleanupRetainsLeaseAndRejectsRestart() {
        var lease = new Lease();
        var backend = new Backend { UncertainStart = true };
        var service = new VmSessionService(lease, backend);
        await Assert.ThrowsAsync<VmCleanupException>(() => service.StartAsync(lease.Snapshot, new("unused")));
        Assert.IsFalse(lease.Disposed);
        Assert.IsTrue(service.Get(lease.Snapshot.Configuration.Id).OwnsResources);
        await Assert.ThrowsAsync<ConfigurationConflictException>(() => service.StartAsync(lease.Snapshot, new("unused")));
        lease.Dispose(); // Test double has no actual process; production does not release uncertain leases.
    }

    [TestMethod]
    public async Task UnexpectedExitIsFaultedAndReleasesOnlyAnExitedProcess() {
        var lease = new Lease();
        var backend = new Backend();
        await using var service = new VmSessionService(lease, backend);
        Guid id = lease.Snapshot.Configuration.Id;
        await service.StartAsync(lease.Snapshot, new("unused"));
        backend.Session.Exit.TrySetResult(17);
        await WaitAsync(() => !service.Get(id).OwnsResources);
        Assert.AreEqual(VmSessionState.Faulted, service.Get(id).State);
        StringAssert.Contains(service.Get(id).Error, "17");
        Assert.IsTrue(lease.Disposed);
    }

    [TestMethod]
    public async Task GuestResetRestartsInNewProcessWithoutInstallationMediaAndKeepsLease() {
        var lease = new Lease();
        var backend = new Backend();
        await using var service = new VmSessionService(lease, backend);
        Guid id = lease.Snapshot.Configuration.Id;
        await service.StartAsync(lease.Snapshot, new("unused", false, true, true));
        var first = backend.Session;
        first.ShutdownReason = "guest-reset";
        first.State = "shutdown";
        await WaitAsync(() => backend.Starts.Count == 2 && service.Get(id).State == VmSessionState.Running);
        Assert.IsTrue(first.Completion.IsCompleted);
        Assert.AreNotSame(first, backend.Session);
        Assert.IsFalse(backend.Starts[1].BootInstallationMedia);
        Assert.IsTrue(backend.Starts[1].IntegratedDisplay);
        Assert.IsTrue(service.Get(id).OwnsResources);
        Assert.IsFalse(lease.Disposed);
        Assert.AreEqual("", service.Get(id).Error);
    }

    [TestMethod]
    public async Task GuestPowerOffWithoutRequestStopsAndReleases() {
        var lease = new Lease();
        var backend = new Backend();
        await using var service = new VmSessionService(lease, backend);
        Guid id = lease.Snapshot.Configuration.Id;
        await service.StartAsync(lease.Snapshot, new("unused"));
        backend.Session.ShutdownReason = "guest-shutdown";
        backend.Session.State = "shutdown";
        await WaitAsync(() => !service.Get(id).OwnsResources);
        Assert.AreEqual(VmSessionState.Stopped, service.Get(id).State);
        Assert.AreEqual(1, backend.Starts.Count);
        Assert.IsTrue(lease.Disposed);
    }

    [TestMethod]
    public async Task RequestedShutdownIsNotTurnedIntoRestartByGuestReset() {
        var lease = new Lease();
        var backend = new Backend();
        await using var service = new VmSessionService(lease, backend);
        Guid id = lease.Snapshot.Configuration.Id;
        await service.StartAsync(lease.Snapshot, new("unused"));
        await service.ControlAsync(id, VmControl.Shutdown);
        backend.Session.ShutdownReason = "guest-reset";
        backend.Session.State = "shutdown";
        await WaitAsync(() => !service.Get(id).OwnsResources);
        Assert.AreEqual(VmSessionState.Stopped, service.Get(id).State);
        Assert.AreEqual(1, backend.Starts.Count);
    }

    [TestMethod]
    public async Task RapidResetLoopIsStoppedAsFaulted() {
        var lease = new Lease();
        var backend = new Backend();
        await using var service = new VmSessionService(lease, backend);
        Guid id = lease.Snapshot.Configuration.Id;
        await service.StartAsync(lease.Snapshot, new("unused"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (service.Get(id).State != VmSessionState.Faulted) {
            var current = backend.Session;
            if (current.State == "running") {
                current.ShutdownReason = "guest-reset";
                current.State = "shutdown";
            }
            await Task.Delay(20, timeout.Token);
        }
        StringAssert.Contains(service.Get(id).Error, "restarted repeatedly");
        Assert.AreEqual(4, backend.Starts.Count);
        Assert.IsFalse(service.Get(id).OwnsResources);
        Assert.IsTrue(lease.Disposed);
        Assert.IsTrue(backend.Session.Completion.IsCompleted);
    }

    [TestMethod]
    public async Task UnrequestedPauseIsFaultedRetainsOwnershipAndRejectsResume() {
        var lease = new Lease();
        var backend = new Backend();
        await using var service = new VmSessionService(lease, backend);
        Guid id = lease.Snapshot.Configuration.Id;
        await service.StartAsync(lease.Snapshot, new("unused"));
        backend.Session.State = "paused";
        await WaitAsync(() => service.Get(id).State == VmSessionState.Faulted);
        await Task.Delay(1200); // Further monitor reconciliations must not report a resumable pause.
        Assert.AreEqual(VmSessionState.Faulted, service.Get(id).State);
        StringAssert.Contains(service.Get(id).Error, "unexpectedly");
        Assert.IsTrue(service.Get(id).OwnsResources);
        await Assert.ThrowsAsync<ConfigurationConflictException>(() => service.ControlAsync(id, VmControl.Resume));
        await service.ForceStopAsync(id);
        Assert.IsTrue(lease.Disposed);
        Assert.IsFalse(service.Get(id).OwnsResources);
    }

    private static async Task WaitAsync(Func<bool> condition) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }
    private sealed class Lease : IVmExecutionLease, IVmExecutionLeaseProvider {
        public bool Disposed;
        public Task AcquireBarrier = Task.CompletedTask;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public VmSnapshot Snapshot { get; } = new(VmConfiguration.CreateMetadata("Test"), "revision");
        public string Directory => "unused";
        public IReadOnlyList<ExecutionResource> Resources => [];
        public void Dispose() => Disposed = true;
        public async Task<IVmExecutionLease> AcquireAsync(VmSnapshot snapshot, CancellationToken cancellationToken = default) {
            Entered.TrySetResult();
            await AcquireBarrier;
            return this;
        }
    }
    private sealed class Backend : IVmBackend {
        // The most recently started session; each start after the first creates a new one, like a new QEMU process.
        public volatile Session Session = new();
        public readonly List<VmStartOptions> Starts = [];
        public bool FailStart, UncertainStart;
        public Task<IVmBackendSession> StartAsync(IVmExecutionLease lease, VmStartOptions options, CancellationToken cancellationToken) {
            if (FailStart)
                throw new OperationCanceledException();
            if (UncertainStart)
                throw new VmCleanupException("Injected uncertain cleanup");
            lock (Starts) {
                if (Starts.Count > 0)
                    Session = new();
                Starts.Add(options);
            }
            return Task.FromResult<IVmBackendSession>(Session);
        }
    }
    private sealed class Session : IVmBackendSession {
        public volatile string State = "running";
        public string? ShutdownReason { get; set; }
        public bool Disconnected;
        public bool CancelAfterControl;
        public readonly TaskCompletionSource<uint> Exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public uint ProcessId => 1;
        public Task<uint> Completion => Exit.Task;
        public string Diagnostics => "inert test session";
        public Task<string> QueryStatusAsync(CancellationToken cancellationToken) => Disconnected ? throw new IOException("Injected disconnect") : Task.FromResult(State);
        public Task ExecuteAsync(VmControl control, CancellationToken cancellationToken) {
            if (Disconnected)
                throw new IOException("Injected disconnect");
            if (control == VmControl.Pause)
                State = "paused";
            if (control == VmControl.Resume)
                State = "running";
            if (control == VmControl.Quit)
                Exit.TrySetResult(0);
            if (CancelAfterControl)
                throw new OperationCanceledException();
            return Task.CompletedTask;
        }
        public Task ForceStopAsync(CancellationToken cancellationToken) {
            Exit.TrySetResult(0);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() {
            Assert.IsTrue(Exit.Task.IsCompleted);
            return ValueTask.CompletedTask;
        }
    }
}
