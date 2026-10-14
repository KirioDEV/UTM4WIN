using Microsoft.VisualStudio.TestTools.UnitTesting;
using UTM4WIN.Application;
using UTM4WIN.Domain;
using UTM4WIN.Infrastructure;

namespace UTM4WIN.UnitTests;

[TestClass]
public sealed class CreationTests
{
    private string _scratch = null!;
    private string _root = null!;
    private string _iso = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "UTM4WIN-creation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratch);
        _root = Path.Combine(_scratch, "library");
        _iso = Path.Combine(_scratch, "fixture.iso");
        byte[] image = new byte[17 * 2048];
        "CD001"u8.CopyTo(image.AsSpan(16 * 2048 + 1));
        image[16 * 2048] = 1; image[16 * 2048 + 6] = 1;
        await File.WriteAllBytesAsync(_iso, image);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_scratch) && Path.GetDirectoryName(_scratch) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) &&
            Path.GetFileName(_scratch).StartsWith("UTM4WIN-creation-", StringComparison.Ordinal)) Directory.Delete(_scratch, recursive: true);
    }

    private VmCreationRequest Request() => new(VmConfiguration.CreateMetadata("Linux"), 32, _iso, Path.Combine(_scratch, "qemu-img.exe"), true);
    private string DiskPath(Guid id) => Path.Combine(_root, id.ToString("D"), "disks", "system.qcow2");

    [TestMethod]
    public async Task CreationRegistersExactlyOnceAndKeepsIsoUnchanged()
    {
        var request = Request();
        byte[] iso = await File.ReadAllBytesAsync(_iso);
        var repository = new JsonVmRepository(_root);
        var disks = new FakeDisks();
        var service = new VmCreationService(repository, disks);
        var snapshot = await service.CreateAsync(request);
        var library = await new JsonVmRepository(_root).ListAsync();
        Assert.AreEqual(1, library.Machines.Count);
        Assert.AreEqual(snapshot.Configuration.Id, library.Machines.Single().Configuration.Id);
        Assert.AreEqual(2, snapshot.Configuration.Storage.Length);
        Assert.AreEqual(ResourceOwnership.Owned, snapshot.Configuration.Storage[0].Ownership);
        Assert.AreEqual(ResourceOwnership.External, snapshot.Configuration.Storage[1].Ownership);
        Assert.IsTrue(snapshot.Configuration.Storage[1].IsReadOnly);
        Assert.AreEqual(32L * 1024 * 1024 * 1024, disks.RequestedSize);
        CollectionAssert.AreEqual(iso, await File.ReadAllBytesAsync(_iso));
        await Assert.ThrowsAsync<ConfigurationConflictException>(() => service.CreateAsync(request));
        Assert.AreEqual(1, disks.Calls);
    }

    [TestMethod]
    public async Task CollisionPreservesExistingDirectoryAndDisk()
    {
        var request = Request();
        string disk = DiskPath(request.Configuration.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
        await File.WriteAllTextAsync(disk, "existing user sentinel");
        var disks = new FakeDisks();
        await Assert.ThrowsAsync<ConfigurationConflictException>(() => new VmCreationService(new JsonVmRepository(_root), disks).CreateAsync(request));
        Assert.AreEqual("existing user sentinel", await File.ReadAllTextAsync(disk));
        Assert.AreEqual(0, disks.Calls);
    }

    [TestMethod]
    public async Task MissingInvalidAndInaccessibleIsoNeverReachDiskTool()
    {
        var request = Request();
        var disks = new FakeDisks();
        var service = new VmCreationService(new JsonVmRepository(_root), disks);
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.CreateAsync(request with { IsoPath = Path.Combine(_scratch, "missing.iso") }));
        using (var exclusive = new FileStream(_iso, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => service.CreateAsync(request));
        await File.WriteAllTextAsync(_iso, "invalid ISO fixture");
        await Assert.ThrowsAsync<ConfigurationException>(() => service.CreateAsync(request));
        Assert.AreEqual(0, disks.Calls);
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, request.Configuration.Id.ToString("D"))));
    }

    [TestMethod]
    public async Task DiskFullFailureRollsBackNewArtifactsAndPreservesOtherData()
    {
        var request = Request();
        var repository = new JsonVmRepository(_root);
        var existing = await repository.CreateAsync(VmConfiguration.CreateMetadata("Existing"));
        string existingPath = Path.Combine(_root, existing.Configuration.Id.ToString("D"), "vm.json");
        byte[] original = await File.ReadAllBytesAsync(existingPath);
        await Assert.ThrowsAsync<IOException>(() => new VmCreationService(repository, new FakeDisks { Failure = new IOException("Injected disk full") }).CreateAsync(request));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, request.Configuration.Id.ToString("D"))));
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(existingPath));
        Assert.AreEqual(1, (await repository.ListAsync()).Machines.Count);
    }

    [TestMethod]
    public async Task CancellationAfterDiskCreationRollsBackWithoutRegistration()
    {
        var request = Request();
        using var cancellation = new CancellationTokenSource();
        var repository = new JsonVmRepository(_root);
        var disks = new FakeDisks { AfterCreation = cancellation.Cancel };
        await Assert.ThrowsAsync<OperationCanceledException>(() => new VmCreationService(repository, disks).CreateAsync(request, cancellationToken: cancellation.Token));
        Assert.IsFalse(File.Exists(DiskPath(request.Configuration.Id)));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, request.Configuration.Id.ToString("D"))));
        Assert.AreEqual(0, (await repository.ListAsync()).Machines.Count);
    }

    [TestMethod]
    public async Task MetadataWriteFailureDiscardsOnlyOperationDisk()
    {
        var request = Request();
        var repository = new JsonVmRepository(_root, new AtomicFileWriter { BeforeCommit = _ => throw new IOException("Injected metadata interruption") });
        await Assert.ThrowsAsync<IOException>(() => new VmCreationService(repository, new FakeDisks()).CreateAsync(request));
        Assert.IsFalse(File.Exists(DiskPath(request.Configuration.Id)));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, request.Configuration.Id.ToString("D"))));
        Assert.IsTrue(File.Exists(_iso));
    }

    [TestMethod]
    public async Task InterruptedRegistrationRetainsDiskAndConfigurationForRecovery()
    {
        var request = Request();
        var repository = new JsonVmRepository(_root, new AtomicFileWriter
        {
            BeforeCommit = path => { if (Path.GetFileName(path) == "library.json") throw new IOException("Injected index interruption"); }
        });
        var error = await Assert.ThrowsAsync<IOException>(() => new VmCreationService(repository, new FakeDisks()).CreateAsync(request));
        Assert.IsTrue(error.Message.Contains("saved but registration failed", StringComparison.Ordinal));
        Assert.IsTrue(File.Exists(DiskPath(request.Configuration.Id)));
        var recovery = new JsonVmRepository(_root);
        var library = await recovery.ListAsync();
        Assert.AreEqual(0, library.Machines.Count);
        Assert.IsTrue(library.UnregisteredIds.Contains(request.Configuration.Id));
        await recovery.RegisterAsync(request.Configuration.Id);
        Assert.AreEqual(1, (await recovery.ListAsync()).Machines.Count);
    }

    [TestMethod]
    public async Task RollbackLeavesUnexpectedFilesForManualInspection()
    {
        var request = Request();
        string directory = Path.Combine(_root, request.Configuration.Id.ToString("D"));
        var repository = new JsonVmRepository(_root, new AtomicFileWriter { BeforeCommit = _ => throw new IOException("Interrupted") });
        var disks = new FakeDisks { AfterCreation = () => File.WriteAllText(Path.Combine(directory, "unexpected.txt"), "preserve") };
        await Assert.ThrowsAsync<IOException>(() => new VmCreationService(repository, disks).CreateAsync(request));
        Assert.IsFalse(File.Exists(DiskPath(request.Configuration.Id)));
        Assert.AreEqual("preserve", await File.ReadAllTextAsync(Path.Combine(directory, "unexpected.txt")));
    }

    [TestMethod]
    public async Task CancellationAfterMetadataCommitRetainsRecoverableDisk()
    {
        var request = Request();
        using var cancellation = new CancellationTokenSource();
        var repository = new JsonVmRepository(_root, new AtomicFileWriter
        {
            BeforeCommit = path => { if (Path.GetFileName(path) == "library.json") cancellation.Cancel(); }
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() => new VmCreationService(repository, new FakeDisks()).CreateAsync(request, cancellationToken: cancellation.Token));
        Assert.IsTrue(File.Exists(DiskPath(request.Configuration.Id)));
        var recovery = new JsonVmRepository(_root);
        Assert.IsTrue((await recovery.ListAsync()).UnregisteredIds.Contains(request.Configuration.Id));
        await recovery.RegisterAsync(request.Configuration.Id);
        Assert.AreEqual(1, (await recovery.ListAsync()).Machines.Count);
    }

    [TestMethod]
    public async Task OccupiedLibraryPathPreservesExistingFileAndDoesNotCreateDisk()
    {
        var request = Request();
        await File.WriteAllTextAsync(_root, "existing file sentinel");
        var disks = new FakeDisks();
        await Assert.ThrowsAsync<IOException>(() => new VmCreationService(new JsonVmRepository(_root), disks).CreateAsync(request));
        Assert.AreEqual("existing file sentinel", await File.ReadAllTextAsync(_root));
        Assert.AreEqual(0, disks.Calls);
    }

    [TestMethod]
    public void CreationRequiresExplicitCleanupConsentAndWholeBoundedCapacity()
    {
        var request = Request();
        Assert.Throws<ConfigurationException>(() => VmCreationValidator.Validate(request with { DiscardIncompleteDisk = false }));
        Assert.Throws<ConfigurationException>(() => VmCreationValidator.Validate(request with { DiskSizeGiB = 0 }));
        Assert.Throws<ConfigurationException>(() => VmCreationValidator.Validate(request with { DiskSizeGiB = 2049 }));
    }

    private sealed class FakeDisks : IDiskImageService
    {
        public int Calls { get; private set; }
        public long RequestedSize { get; private set; }
        public Exception? Failure { get; init; }
        public Action? AfterCreation { get; init; }
        public Task<ICreatedDisk> CreateAsync(string executable, string destination, long sizeBytes, CancellationToken cancellationToken = default)
        {
            Calls++; RequestedSize = sizeBytes;
            var guard = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
            try
            {
                guard.Write("inert disk fixture"u8);
                guard.Flush();
                if (Failure is not null) throw Failure;
                AfterCreation?.Invoke();
                return Task.FromResult<ICreatedDisk>(new FakeDisk(destination, guard));
            }
            catch { guard.Dispose(); File.Delete(destination); throw; }
        }
    }

    private sealed class FakeDisk(string path, FileStream guard) : ICreatedDisk
    {
        private bool _retained;
        public void Retain() => _retained = true;
        public ValueTask DisposeAsync() { guard.Dispose(); if (!_retained) File.Delete(path); return ValueTask.CompletedTask; }
    }
}
