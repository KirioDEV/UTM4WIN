using System.Collections.Immutable;
using UTM4WIN.Application;
using UTM4WIN.Domain;
using UTM4WIN.Infrastructure;
using UTM4WIN.Virtualization.Qemu;

namespace UTM4WIN.UnitTests;

[TestClass]
public sealed class ExecutionLeaseTests {
    [TestMethod]
    public void OversizedGuestAllocationsFailBeforeBackendLaunch() {
        var configuration = VmConfiguration.CreateMetadata("Capacity") with {
            MemoryMiB = 2048,
            CpuCount = 2
        };
        Assert.Throws<ConfigurationException>(() => HostCapabilities.ValidateCapacity(configuration, 2147483648, 8));
        Assert.Throws<ConfigurationException>(() => HostCapabilities.ValidateCapacity(configuration, 8589934592, 1));
        HostCapabilities.ValidateCapacity(configuration, 8589934592, 8);
    }

    [TestMethod]
    public async Task ActiveLeaseBlocksParallelStartSaveAndRemoveButAllowsListing() {
        string root = Path.Combine(Path.GetTempPath(), "UTM4WIN-lease-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try {
            var repository = new JsonVmRepository(root);
            var snapshot = await repository.CreateAsync(VmConfiguration.CreateMetadata("Lease test"));
            string disks = Path.Combine(root, snapshot.Configuration.Id.ToString(), "disks");
            Directory.CreateDirectory(disks);
            await File.WriteAllTextAsync(Path.Combine(disks, "system.qcow2"), "inert fixture, never executed");
            snapshot = await repository.SaveAsync(snapshot.Configuration with {
                Storage = [new() { Id = Guid.NewGuid(), Path = "disks/system.qcow2", Format = DiskFormat.Qcow2, Ownership = ResourceOwnership.Owned, IsReadOnly = false }]
            }, snapshot.Revision);
            var other = new JsonVmRepository(root);
            using (var lease = await repository.AcquireAsync(snapshot)) {
                await Assert.ThrowsAsync<ConfigurationConflictException>(() => other.AcquireAsync(snapshot));
                await Assert.ThrowsAsync<ConfigurationConflictException>(() => other.SaveAsync(snapshot.Configuration with { Name = "Changed" }, snapshot.Revision));
                await Assert.ThrowsAsync<ConfigurationConflictException>(() => other.RemoveAsync(snapshot.Configuration.Id));
                Assert.AreEqual(1, (await other.ListAsync()).Machines.Count);
                using var writable = new FileStream(lease.Resources[0].FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
            await repository.RemoveAsync(snapshot.Configuration.Id);
            Assert.IsTrue(File.Exists(Path.Combine(disks, "system.qcow2")));
        } finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void LaunchArgumentsPinProfileAndEscapeQemuOptionSyntax() {
        var configuration = VmConfiguration.CreateMetadata("Comma, test") with {
            ExecutionMode = ExecutionMode.Tcg
        };
        using var lease = new ArgumentLease(configuration);
        var arguments = QemuBackend.BuildArguments(lease, new("unused", false, false));
        CollectionAssert.Contains(arguments, QemuBackend.QualifiedMachine);
        CollectionAssert.Contains(arguments, "tcg");
        CollectionAssert.Contains(arguments, "Comma,, test");
        using var disk = System.Text.Json.JsonDocument.Parse(arguments[Array.IndexOf(arguments, "-blockdev") + 1]);
        Assert.AreEqual(@"C:\safe, folder\雪.qcow2", disk.RootElement.GetProperty("file").GetProperty("filename").GetString());
        Assert.IsFalse(arguments.Contains("-vnc"));
        CollectionAssert.Contains(arguments, "stdio");
        Assert.Throws<ConfigurationException>(() => QemuBackend.BuildArguments(lease, new("unused", false, true)));
        Assert.Throws<ConfigurationException>(() => QemuBackend.RequireRuntimePath(@"C:\雪\disk.qcow2"));
    }

    private sealed class ArgumentLease(VmConfiguration configuration) : IVmExecutionLease {
        public VmSnapshot Snapshot => new(configuration, "revision");
        public string Directory => @"C:\safe, folder";
        public IReadOnlyList<ExecutionResource> Resources => [new(new() { Id = Guid.NewGuid(), Path = "disks/disk.qcow2", Ownership = ResourceOwnership.Owned, Format = DiskFormat.Qcow2, IsReadOnly = false }, @"C:\safe, folder\雪.qcow2")];
        public void Dispose() {
        }
    }
}
