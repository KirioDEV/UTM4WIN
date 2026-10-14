using System.Text.Json;
using UTM4WIN.Application;
using UTM4WIN.Domain;

namespace UTM4WIN.Infrastructure;

public sealed class JsonVmRepository : IVmRepository {
    private const int MaximumDocumentBytes = 1024 * 1024;
    private readonly string _root;
    private readonly AtomicFileWriter _writer;
    private sealed record LibraryIndex {
        public required int SchemaVersion {
            get; init;
        }
        public required Guid[] Ids {
            get; init;
        }
    }

    public JsonVmRepository(string libraryRoot) : this(libraryRoot, new AtomicFileWriter()) { }

    internal JsonVmRepository(string libraryRoot, AtomicFileWriter writer) {
        if (!Path.IsPathFullyQualified(libraryRoot) || libraryRoot.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("A local, absolute library path is required.", nameof(libraryRoot));
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        _writer = writer;
    }

    public Task<VmLibrary> ListAsync(CancellationToken cancellationToken = default) => WithLockAsync(async () => {
        var index = await ReadIndexAsync(cancellationToken);
        var machines = new List<VmSnapshot>();
        var issues = new List<LibraryIssue>();
        foreach (Guid id in index.Ids) {
            cancellationToken.ThrowIfCancellationRequested();
            try {
                var snapshot = await ReadVmAsync(id, cancellationToken);
                machines.Add(snapshot);
                try {
                    CheckResources(snapshot.Configuration);
                } catch (Exception exception) when (IsDataFailure(exception)) { issues.Add(new(id, exception.Message)); }
            } catch (Exception exception) when (IsDataFailure(exception)) { issues.Add(new(id, exception.Message)); }
        }
        var registered = index.Ids.ToHashSet();
        var unregistered = Directory.EnumerateDirectories(_root)
            .Select(Path.GetFileName).Select(name => Guid.TryParseExact(name, "D", out Guid id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty && !registered.Contains(id)).Order().ToArray();
        return new VmLibrary(machines.OrderBy(vm => vm.Configuration.Name, StringComparer.OrdinalIgnoreCase).ToArray(), issues, unregistered);
    }, cancellationToken);

    public Task<VmSnapshot> CreateAsync(VmConfiguration configuration, CancellationToken cancellationToken = default) => WithLockAsync(async () => {
        byte[] bytes = ConfigurationJson.Serialize(configuration);
        var index = await ReadIndexAsync(cancellationToken);
        string directory = VmDirectory(configuration.Id);
        PathSafety.Check(directory);
        if (index.Ids.Contains(configuration.Id) || Directory.Exists(directory) || File.Exists(directory))
            throw new ConfigurationConflictException("This VM ID already has a library entry or retained files. Nothing was overwritten.");
        CheckResources(configuration);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        // A failed second commit leaves identifiable, unregistered metadata for explicit recovery.
        await _writer.WriteAsync(VmPath(configuration.Id), bytes, cancellationToken);
        await WriteIndexAsync(index with {
            Ids = [.. index.Ids, configuration.Id]
        }, cancellationToken);
        return ConfigurationJson.Deserialize(bytes);
    }, cancellationToken);

    public Task<VmSnapshot> SaveAsync(VmConfiguration configuration, string expectedRevision, CancellationToken cancellationToken = default) => WithLockAsync(async () => {
        byte[] bytes = ConfigurationJson.Serialize(configuration);
        var index = await ReadIndexAsync(cancellationToken);
        if (!index.Ids.Contains(configuration.Id))
            throw new ConfigurationConflictException("This VM is no longer registered. Reload the library.");
        var current = await ReadVmAsync(configuration.Id, cancellationToken);
        if (!StringComparer.Ordinal.Equals(current.Revision, expectedRevision))
            throw new ConfigurationConflictException("The configuration changed since editing began. Your draft is preserved; reload before saving again.");
        CheckResources(configuration);
        await _writer.WriteAsync(VmPath(configuration.Id), bytes, cancellationToken);
        return ConfigurationJson.Deserialize(bytes);
    }, cancellationToken);

    internal Task<VmSnapshot> CreateWithDiskAsync(VmCreationRequest request, IDiskImageService diskImages,
        IProgress<string>? progress, CancellationToken cancellationToken) => WithLockAsync(async () => {
            VmCreationValidator.Validate(request);
            var configuration = request.Configuration with {
                Storage = [new() { Id = Guid.NewGuid(), Path = "disks/system.qcow2", Ownership = ResourceOwnership.Owned, Format = DiskFormat.Qcow2, IsReadOnly = false },
                new() { Id = Guid.NewGuid(), Path = request.IsoPath, Ownership = ResourceOwnership.External, Format = DiskFormat.Iso, IsReadOnly = true }]
            };
            byte[] bytes = ConfigurationJson.Serialize(configuration);
            var index = await ReadIndexAsync(cancellationToken);
            var updatedIndex = index with {
                Ids = [.. index.Ids, configuration.Id]
            };
            string directory = VmDirectory(configuration.Id);
            string disks = Path.Combine(directory, "disks");
            PathSafety.Check(directory);
            if (index.Ids.Contains(configuration.Id) || Directory.Exists(directory) || File.Exists(directory))
                throw new ConfigurationConflictException("This VM ID already has retained files or a library entry. Nothing was overwritten.");
            ValidateIndex(updatedIndex);
            progress?.Report("Validating installation media…");
            PathSafety.Check(request.IsoPath);
            await using var iso = new FileStream(request.IsoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            await CheckIsoAsync(iso, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            bool committed = false;
            try {
                Directory.CreateDirectory(disks);
                progress?.Report($"Creating sparse {request.DiskSizeGiB} GiB disk…");
                await using var disk = await diskImages.CreateAsync(request.QemuImgPath, Path.Combine(disks, "system.qcow2"),
                    checked((long)request.DiskSizeGiB * 1024 * 1024 * 1024), cancellationToken);
                // The disk lease already validates and pins its new file; reopening with Read sharing
                // would conflict with its write guard. The external ISO is also pinned above.
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report("Saving and registering the virtual machine…");
                await _writer.WriteAsync(VmPath(configuration.Id), bytes, cancellationToken);
                committed = true;
                disk.Retain();
                await WriteIndexAsync(updatedIndex, cancellationToken);
                return ConfigurationJson.Deserialize(bytes);
            } catch (Exception exception) when (exception is not OperationCanceledException) {
                throw new IOException(committed
                    ? $"VM {configuration.Id} was saved but registration failed. Close the wizard, reload and register its retained metadata. {exception.Message}"
                    : $"VM creation failed for {configuration.Id}. {exception.Message}", exception);
            } finally {
                // Never recursively delete: only empty directories made by this operation can be removed.
                if (!committed) {
                    PathSafety.Check(disks);
                    if (Directory.Exists(disks) && !Directory.EnumerateFileSystemEntries(disks).Any())
                        Directory.Delete(disks);
                    PathSafety.Check(directory);
                    if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                        Directory.Delete(directory);
                }
            }
        }, cancellationToken);

    private static async Task CheckIsoAsync(FileStream stream, CancellationToken cancellationToken) {
        byte[] descriptor = new byte[7];
        for (int sector = 16; sector < 32 && stream.Length >= sector * 2048L + descriptor.Length; sector++) {
            stream.Position = sector * 2048L;
            await stream.ReadExactlyAsync(descriptor, cancellationToken);
            if (descriptor.AsSpan(1, 5).SequenceEqual("CD001"u8) && descriptor[6] == 1)
                return;
        }
        throw new ConfigurationException("The selected ISO has no supported ISO 9660 volume descriptor. Choose Linux installation media.");
    }

    public Task RegisterAsync(Guid id, CancellationToken cancellationToken = default) => WithLockAsync(async () => {
        var index = await ReadIndexAsync(cancellationToken);
        if (index.Ids.Contains(id))
            throw new ConfigurationConflictException("This VM is already registered.");
        var vm = await ReadVmAsync(id, cancellationToken);
        CheckResources(vm.Configuration);
        await WriteIndexAsync(index with {
            Ids = [.. index.Ids, id]
        }, cancellationToken);
        return true;
    }, cancellationToken);

    public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default) => WithLockAsync(async () => {
        var index = await ReadIndexAsync(cancellationToken);
        if (!index.Ids.Contains(id))
            throw new ConfigurationConflictException("This VM is no longer registered.");
        await WriteIndexAsync(index with {
            Ids = index.Ids.Where(existing => existing != id).ToArray()
        }, cancellationToken);
        return true;
    }, cancellationToken);

    private Task<T> WithLockAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken) => Task.Run(async () => {
        cancellationToken.ThrowIfCancellationRequested();
        PathSafety.Check(_root);
        Directory.CreateDirectory(_root);
        string lockPath = Path.Combine(_root, ".library.lock");
        PathSafety.Check(lockPath);
        using var libraryLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        PathSafety.Check(_root);
        PathSafety.Check(lockPath);
        return await operation().ConfigureAwait(false);
    }, cancellationToken);

    private string VmDirectory(Guid id) {
        if (id == Guid.Empty)
            throw new ConfigurationException("A VM must have a nonempty ID.");
        return Path.Combine(_root, id.ToString("D"));
    }

    private string VmPath(Guid id) => Path.Combine(VmDirectory(id), "vm.json");

    private async Task<VmSnapshot> ReadVmAsync(Guid id, CancellationToken cancellationToken) {
        var snapshot = ConfigurationJson.Deserialize(await ReadBytesAsync(VmPath(id), cancellationToken));
        if (snapshot.Configuration.Id != id)
            throw new ConfigurationException("The directory and configuration IDs do not match.");
        return snapshot;
    }

    private async Task<LibraryIndex> ReadIndexAsync(CancellationToken cancellationToken) {
        string path = Path.Combine(_root, "library.json");
        PathSafety.Check(path);
        if (!File.Exists(path)) {
            if (Directory.Exists(path))
                throw new ConfigurationException("The library index path is occupied by a directory.");
            return new() {
                SchemaVersion = ConfigurationJson.CurrentSchemaVersion,
                Ids = []
            };
        }
        try {
            byte[] bytes = await ReadBytesAsync(path, cancellationToken);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (!document.RootElement.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out int version))
                throw new ConfigurationException("The library index has no valid schema version.");
            ConfigurationJson.RequireSchema(version);
            var index = JsonSerializer.Deserialize<LibraryIndex>(bytes, ConfigurationJson.Options) ?? throw new ConfigurationException("Empty library index.");
            ValidateIndex(index);
            return index;
        } catch (JsonException exception) { throw new ConfigurationException($"Invalid library index: {exception.Message}. Restore the previous index explicitly; no files were overwritten."); } catch (InvalidOperationException) { throw new ConfigurationException("Invalid library index document."); }
    }

    private Task WriteIndexAsync(LibraryIndex index, CancellationToken cancellationToken) {
        ValidateIndex(index);
        return _writer.WriteAsync(Path.Combine(_root, "library.json"), JsonSerializer.SerializeToUtf8Bytes(index, ConfigurationJson.Options), cancellationToken);
    }

    private static void ValidateIndex(LibraryIndex index) {
        if (index.Ids is null || index.Ids.Length > 1000 || index.Ids.Any(id => id == Guid.Empty) || index.Ids.Distinct().Count() != index.Ids.Length)
            throw new ConfigurationException("The index must contain at most 1000 unique, nonempty VM IDs.");
    }

    private static async Task<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken) {
        PathSafety.Check(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > MaximumDocumentBytes)
            throw new ConfigurationException("Metadata exceeds the 1 MiB document limit.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return bytes;
    }

    private void CheckResources(VmConfiguration configuration) {
        foreach (var resource in configuration.Storage) {
            string path = resource.Ownership == ResourceOwnership.Owned
                ? Path.Combine(VmDirectory(configuration.Id), resource.Path.Replace('/', Path.DirectorySeparatorChar)) : resource.Path;
            PathSafety.Check(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
    }

    private static bool IsDataFailure(Exception exception) => exception is ConfigurationException or IOException or UnauthorizedAccessException;
}
