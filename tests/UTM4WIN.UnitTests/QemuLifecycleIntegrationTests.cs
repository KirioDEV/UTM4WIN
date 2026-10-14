using System.Diagnostics;
using UTM4WIN.Application;
using UTM4WIN.Domain;
using UTM4WIN.Infrastructure;
using UTM4WIN.Virtualization.Qemu;

namespace UTM4WIN.UnitTests;

[TestClass]
[TestCategory("QemuIntegration")]
public sealed class QemuLifecycleIntegrationTests {
    [TestMethod]
    public async Task TwentyOwnedStartPauseResumeStopCyclesLeaveNoProcesses() {
        string executable = Environment.GetEnvironmentVariable("UTM4WIN_TEST_QEMU_SYSTEM") ?? "";
        string iso = Environment.GetEnvironmentVariable("UTM4WIN_TEST_ISO") ?? "";
        if (!File.Exists(executable) || !File.Exists(iso))
            Assert.Inconclusive("Select the explicitly approved QEMU system binary and disposable-test installation ISO.");
        string root = Path.Combine(Path.GetTempPath(), "UTM4WIN-lifecycle test, " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pids = new List<uint>();
        try {
            var repository = new JsonVmRepository(Path.Combine(root, "library"));
            var creation = new VmCreationService(repository, new QemuImgDiskService());
            var snapshot = await creation.CreateAsync(new(VmConfiguration.CreateMetadata("Cycle fixture") with {
                MemoryMiB = 256,
                CpuCount = 1
            },
                1, iso, Path.Combine(Path.GetDirectoryName(executable)!, "qemu-img.exe"), true));
            await using var sessions = new VmSessionService(repository, new QemuBackend());
            for (int cycle = 0; cycle < 20; cycle++) {
                snapshot = await repository.SaveAsync(snapshot.Configuration with {
                    ExecutionMode = cycle < 10 ? ExecutionMode.Tcg : ExecutionMode.Whpx
                }, snapshot.Revision);
                await sessions.StartAsync(snapshot, new(executable, false));
                Guid id = snapshot.Configuration.Id;
                Assert.AreEqual(VmSessionState.Running, sessions.Get(id).State);
                uint pid = sessions.Get(id).ProcessId!.Value;
                pids.Add(pid);
                await sessions.ControlAsync(id, VmControl.Pause);
                Assert.AreEqual(VmSessionState.Paused, sessions.Get(id).State);
                await sessions.ControlAsync(id, VmControl.Resume);
                Assert.AreEqual(VmSessionState.Running, sessions.Get(id).State);
                await Assert.ThrowsAsync<ConfigurationConflictException>(() => repository.AcquireAsync(snapshot));
                await Assert.ThrowsAsync<ConfigurationConflictException>(() => repository.SaveAsync(snapshot.Configuration, snapshot.Revision));
                await sessions.ForceStopAsync(id);
                Assert.IsFalse(sessions.Get(id).OwnsResources);
                Assert.IsFalse(IsAlive(pid));
            }
            Assert.AreEqual(0, (await repository.ListAsync()).Issues.Count);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => sessions.StartAsync(snapshot, new(executable, false), canceled.Token));
            Assert.IsFalse(sessions.Get(snapshot.Configuration.Id).OwnsResources);
            foreach (bool afterGreeting in new[] { false, true }) {
                using var startupCancellation = new CancellationTokenSource();
                uint canceledPid = 0;
                void CancelAtBoundary(uint pid) {
                    canceledPid = pid;
                    startupCancellation.Cancel();
                }
                var backend = afterGreeting ? new QemuBackend(null, CancelAtBoundary) : new QemuBackend(CancelAtBoundary);
                await using var canceledSessions = new VmSessionService(repository, backend);
                await Assert.ThrowsAsync<OperationCanceledException>(() => canceledSessions.StartAsync(snapshot, new(executable, false), startupCancellation.Token));
                Assert.IsTrue(canceledPid != 0);
                Assert.IsFalse(IsAlive(canceledPid));
                Assert.IsFalse(canceledSessions.Get(snapshot.Configuration.Id).OwnsResources);
                using var lease = await repository.AcquireAsync(snapshot);
            }
            await sessions.StartAsync(snapshot, new(executable, false));
            uint crashedPid = sessions.Get(snapshot.Configuration.Id).ProcessId!.Value;
            using (var process = Process.GetProcessById(checked((int)crashedPid)))
                process.Kill();
            using var crashDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (sessions.Get(snapshot.Configuration.Id).OwnsResources)
                await Task.Delay(20, crashDeadline.Token);
            Assert.AreEqual(VmSessionState.Faulted, sessions.Get(snapshot.Configuration.Id).State);
        } finally {
            Assert.IsFalse(pids.Any(IsAlive), "Only these test-owned process IDs are observed, never unrelated processes.");
            if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) && Path.GetFileName(root).StartsWith("UTM4WIN-lifecycle test, ", StringComparison.Ordinal))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CopiedDisposableInstalledGuestCompletesCooperativeShutdown() {
        string executable = Environment.GetEnvironmentVariable("UTM4WIN_TEST_QEMU_SYSTEM") ?? "";
        string source = Environment.GetEnvironmentVariable("UTM4WIN_TEST_DISPOSABLE_GUEST") ?? "";
        if (!File.Exists(executable) || !File.Exists(source))
            Assert.Inconclusive("Explicitly select an already qualified disposable BIOS guest image; the original is only read for a new copy.");
        string root = Path.Combine(Path.GetTempPath(), "UTM4WIN-installed guest " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            var repository = new JsonVmRepository(root);
            var snapshot = await repository.CreateAsync(VmConfiguration.CreateMetadata("Copied disposable Debian") with {
                MemoryMiB = 1536,
                CpuCount = 2
            });
            string directory = Path.Combine(root, snapshot.Configuration.Id.ToString(), "disks");
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, "system.qcow2");
            await using (var original = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var copy = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await original.CopyToAsync(copy);
            snapshot = await repository.SaveAsync(snapshot.Configuration with {
                Storage = [new() { Id = Guid.NewGuid(), Path = "disks/system.qcow2", Format = DiskFormat.Qcow2, Ownership = ResourceOwnership.Owned, IsReadOnly = false }]
            }, snapshot.Revision);
            await using var sessions = new VmSessionService(repository, new QemuBackend());
            await sessions.StartAsync(snapshot, new(executable, false, false));
            Guid id = snapshot.Configuration.Id;
            uint pid = sessions.Get(id).ProcessId!.Value;
            await Task.Delay(TimeSpan.FromSeconds(20));
            if (sessions.Get(id).OwnsResources)
                await sessions.ControlAsync(id, VmControl.Shutdown);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (sessions.Get(id).OwnsResources)
                await Task.Delay(100, deadline.Token);
            Assert.AreEqual(VmSessionState.Stopped, sessions.Get(id).State, sessions.Get(id).Error);
            Assert.IsFalse(IsAlive(pid));
        } finally {
            if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) && Path.GetFileName(root).StartsWith("UTM4WIN-installed guest ", StringComparison.Ordinal))
                Directory.Delete(root, recursive: true);
        }
    }

    private static bool IsAlive(uint id) {
        try {
            using var process = Process.GetProcessById(checked((int)id));
            return !process.HasExited;
        } catch (ArgumentException) { return false; }
    }
}
