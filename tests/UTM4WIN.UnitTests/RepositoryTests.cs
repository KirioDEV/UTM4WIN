using UTM4WIN.Application;
using UTM4WIN.Domain;
using UTM4WIN.Infrastructure;

namespace UTM4WIN.UnitTests;

[TestClass]
public sealed class RepositoryTests {
    private string _root = null!;
    private JsonVmRepository _repository = null!;

    [TestInitialize]
    public void Initialize() {
        _root = Path.Combine(Path.GetTempPath(), "UTM4WIN-tests-" + Guid.NewGuid().ToString("N"));
        _repository = new(_root);
    }

    [TestCleanup]
    public void Cleanup() {
        if (Directory.Exists(_root) && Path.GetDirectoryName(_root) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) &&
            Path.GetFileName(_root).StartsWith("UTM4WIN-tests-", StringComparison.Ordinal))
            Directory.Delete(_root, recursive: true);
    }

    private string VmPath(Guid id) => Path.Combine(_root, id.ToString("D"), "vm.json");

    [TestMethod]
    public async Task CreateEditReloadAndBackupPreserveIdentity() {
        var first = await _repository.CreateAsync(VmConfiguration.CreateMetadata("Debian"));
        byte[] original = await File.ReadAllBytesAsync(VmPath(first.Configuration.Id));
        var second = await _repository.SaveAsync(first.Configuration with {
            Name = "Debian renamed"
        }, first.Revision);
        var reloaded = (await new JsonVmRepository(_root).ListAsync()).Machines.Single();
        Assert.AreEqual(second.Configuration.Id, reloaded.Configuration.Id);
        Assert.AreEqual("Debian renamed", reloaded.Configuration.Name);
        Assert.AreEqual(second.Revision, reloaded.Revision);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(VmPath(first.Configuration.Id) + ".previous"));
    }

    [TestMethod]
    public async Task DuplicateCreateAndStaleSavePreserveLatestConfiguration() {
        var first = await _repository.CreateAsync(VmConfiguration.CreateMetadata("Debian"));
        await Assert.ThrowsAsync<ConfigurationConflictException>(() => _repository.CreateAsync(first.Configuration));
        await _repository.SaveAsync(first.Configuration with {
            Name = "Other edit"
        }, first.Revision);
        await Assert.ThrowsAsync<ConfigurationConflictException>(() => new JsonVmRepository(_root).SaveAsync(first.Configuration with { Name = "Stale edit" }, first.Revision));
        Assert.AreEqual("Other edit", (await _repository.ListAsync()).Machines.Single().Configuration.Name);
    }

    [TestMethod]
    public async Task RemovePreservesAllFilesAndAllowsExplicitRegistration() {
        var snapshot = await _repository.CreateAsync(VmConfiguration.CreateMetadata("Debian"));
        string directory = Path.GetDirectoryName(VmPath(snapshot.Configuration.Id))!;
        Directory.CreateDirectory(Path.Combine(directory, "disks"));
        string disk = Path.Combine(directory, "disks", "sentinel.qcow2");
        await File.WriteAllTextAsync(disk, "untouched sentinel, not a real image");
        await _repository.RemoveAsync(snapshot.Configuration.Id);
        var removed = await _repository.ListAsync();
        Assert.AreEqual(0, removed.Machines.Count);
        Assert.IsTrue(removed.UnregisteredIds.Contains(snapshot.Configuration.Id));
        Assert.AreEqual("untouched sentinel, not a real image", await File.ReadAllTextAsync(disk));
        Assert.IsTrue(File.Exists(VmPath(snapshot.Configuration.Id)));
        await _repository.RegisterAsync(snapshot.Configuration.Id);
        Assert.AreEqual(1, (await _repository.ListAsync()).Machines.Count);
        await Assert.ThrowsAsync<ConfigurationConflictException>(() => _repository.RegisterAsync(snapshot.Configuration.Id));
    }

    [TestMethod]
    public async Task MissingResourcesPreventSaveAndAreReportedWithoutHidingMetadata() {
        var first = await _repository.CreateAsync(VmConfiguration.CreateMetadata("Debian"));
        string directory = Path.GetDirectoryName(VmPath(first.Configuration.Id))!;
        Directory.CreateDirectory(Path.Combine(directory, "disks"));
        string disk = Path.Combine(directory, "disks", "system.qcow2");
        await File.WriteAllTextAsync(disk, "sentinel");
        var withDisk = await _repository.SaveAsync(first.Configuration with {
            Storage = [new() { Id = Guid.NewGuid(), Path = "disks/system.qcow2", Ownership = ResourceOwnership.Owned, Format = DiskFormat.Qcow2, IsReadOnly = false }]
        }, first.Revision);
        File.Delete(disk);
        var library = await _repository.ListAsync();
        Assert.AreEqual(1, library.Machines.Count);
        Assert.AreEqual(1, library.Issues.Count);
        await Assert.ThrowsAsync<FileNotFoundException>(() => _repository.SaveAsync(withDisk.Configuration with { Name = "Changed" }, withDisk.Revision));
        Assert.AreEqual(withDisk.Revision, (await _repository.ListAsync()).Machines.Single().Revision);
    }

    [TestMethod]
    public async Task InterruptedConfigWriteKeepsOriginalAndCleansOnlyOwnedTemporary() {
        var first = await _repository.CreateAsync(VmConfiguration.CreateMetadata("Debian"));
        var failing = new JsonVmRepository(_root, new AtomicFileWriter { BeforeCommit = _ => throw new IOException("Injected interruption") });
        await Assert.ThrowsAsync<IOException>(() => failing.SaveAsync(first.Configuration with { Name = "Interrupted" }, first.Revision));
        Assert.AreEqual(first.Revision, (await _repository.ListAsync()).Machines.Single().Revision);
        Assert.AreEqual(0, Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task InterruptedRegistrationRetainsRecoverableUnregisteredConfiguration() {
        var configuration = VmConfiguration.CreateMetadata("Debian");
        var failing = new JsonVmRepository(_root, new AtomicFileWriter {
            BeforeCommit = path => { if (Path.GetFileName(path) == "library.json") throw new IOException("Injected index interruption"); }
        });
        await Assert.ThrowsAsync<IOException>(() => failing.CreateAsync(configuration));
        var library = await _repository.ListAsync();
        Assert.AreEqual(0, library.Machines.Count);
        Assert.IsTrue(library.UnregisteredIds.Contains(configuration.Id));
        await _repository.RegisterAsync(configuration.Id);
        Assert.AreEqual(configuration.Id, (await _repository.ListAsync()).Machines.Single().Configuration.Id);
    }

    [TestMethod]
    public async Task CancellationBeforeCommitPreservesOriginal() {
        var first = await _repository.CreateAsync(VmConfiguration.CreateMetadata("Debian"));
        using var cancellation = new CancellationTokenSource();
        var cancelling = new JsonVmRepository(_root, new AtomicFileWriter { BeforeCommit = _ => cancellation.Cancel() });
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelling.SaveAsync(first.Configuration with { Name = "Canceled" }, first.Revision, cancellation.Token));
        Assert.AreEqual(first.Revision, (await _repository.ListAsync()).Machines.Single().Revision);
    }

    [TestMethod]
    public async Task SecondRepositoryCannotWriteThroughHeldLibraryLock() {
        var first = await _repository.CreateAsync(VmConfiguration.CreateMetadata("Debian"));
        using (var held = new FileStream(Path.Combine(_root, ".library.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => new JsonVmRepository(_root).SaveAsync(first.Configuration with { Name = "Blocked" }, first.Revision));
        Assert.AreEqual(first.Revision, (await _repository.ListAsync()).Machines.Single().Revision);
    }

    [TestMethod]
    public async Task NewerVmSchemaAndCorruptIndexAreNeverOverwritten() {
        var first = await _repository.CreateAsync(VmConfiguration.CreateMetadata("Debian"));
        string path = VmPath(first.Configuration.Id);
        string newer = (await File.ReadAllTextAsync(path)).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99");
        await File.WriteAllTextAsync(path, newer);
        Assert.AreEqual(1, (await _repository.ListAsync()).Issues.Count);
        await Assert.ThrowsAsync<ConfigurationException>(() => _repository.SaveAsync(first.Configuration, first.Revision));
        Assert.AreEqual(newer, await File.ReadAllTextAsync(path));
        string index = Path.Combine(_root, "library.json");
        await File.WriteAllTextAsync(index, "broken index");
        await Assert.ThrowsAsync<ConfigurationException>(() => _repository.CreateAsync(VmConfiguration.CreateMetadata("Other")));
        Assert.AreEqual("broken index", await File.ReadAllTextAsync(index));
    }

    [TestMethod]
    public async Task DuplicateIndexIdsAndDirectoryIdentityMismatchAreReported() {
        var first = await _repository.CreateAsync(VmConfiguration.CreateMetadata("Debian"));
        await File.WriteAllBytesAsync(VmPath(first.Configuration.Id), ConfigurationJson.Serialize(VmConfiguration.CreateMetadata("Wrong ID")));
        Assert.AreEqual(1, (await _repository.ListAsync()).Issues.Count);
        await File.WriteAllTextAsync(Path.Combine(_root, "library.json"), $"{{\"schemaVersion\":1,\"ids\":[\"{first.Configuration.Id}\",\"{first.Configuration.Id}\"]}}");
        await Assert.ThrowsAsync<ConfigurationException>(() => _repository.ListAsync());
    }
}
