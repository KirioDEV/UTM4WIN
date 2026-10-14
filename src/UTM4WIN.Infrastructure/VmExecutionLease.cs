using UTM4WIN.Application;
using UTM4WIN.Domain;

namespace UTM4WIN.Infrastructure;

public sealed partial class JsonVmRepository {
    private FileStream LockSession(Guid id) {
        string path = Path.Combine(VmDirectory(id), ".session.lock");
        PathSafety.Check(path);
        try {
            return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        } catch (IOException error) { throw new ConfigurationConflictException("This VM is active or its session lock is unavailable. Stop it before editing, removing or starting again. " + error.Message); }
    }

    public Task<IVmExecutionLease> AcquireAsync(VmSnapshot snapshot, CancellationToken cancellationToken = default) => WithLockAsync<IVmExecutionLease>(async () => {
        var index = await ReadIndexAsync(cancellationToken);
        Guid id = snapshot.Configuration.Id;
        if (!index.Ids.Contains(id))
            throw new ConfigurationConflictException("This VM is no longer registered.");
        var guards = new List<FileStream>();
        try {
            guards.Add(LockSession(id));
            var current = await ReadVmAsync(id, cancellationToken);
            if (current.Revision != snapshot.Revision)
                throw new ConfigurationConflictException("VM settings changed. Reload before starting.");
            var configuration = current.Configuration;
            if (configuration.Network != NetworkMode.None || configuration.Storage.Length is < 1 or > 2 ||
                configuration.Storage.Count(resource => resource.Ownership == ResourceOwnership.Owned && resource.Format == DiskFormat.Qcow2 && !resource.IsReadOnly) != 1 ||
                configuration.Storage.Any(resource => resource.Ownership == ResourceOwnership.External && (resource.Format != DiskFormat.Iso || !resource.IsReadOnly)) ||
                configuration.Storage.Count(resource => resource.Format == DiskFormat.Iso) != configuration.Storage.Length - 1)
                throw new ConfigurationException("Starting requires one managed writable qcow2 disk and optionally one read-only external ISO, with networking disabled.");
            guards.Add(new(VmPath(id), FileMode.Open, FileAccess.Read, FileShare.Read));
            var resources = new List<ExecutionResource>();
            foreach (var resource in configuration.Storage) {
                string path = resource.Ownership == ResourceOwnership.Owned
                    ? Path.GetFullPath(Path.Combine(VmDirectory(id), resource.Path.Replace('/', Path.DirectorySeparatorChar))) : resource.Path;
                PathSafety.Check(path);
                guards.Add(new(path, FileMode.Open, FileAccess.Read, resource.IsReadOnly ? FileShare.Read : FileShare.ReadWrite));
                resources.Add(new(resource, path));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new ExecutionLease(current, VmDirectory(id), resources, guards);
        } catch { foreach (var guard in guards) guard.Dispose(); throw; }
    }, cancellationToken);

    private sealed class ExecutionLease(VmSnapshot snapshot, string directory, IReadOnlyList<ExecutionResource> resources, List<FileStream> guards) : IVmExecutionLease {
        public VmSnapshot Snapshot => snapshot;
        public string Directory => directory;
        public IReadOnlyList<ExecutionResource> Resources => resources;
        public void Dispose() {
            foreach (var guard in guards)
                guard.Dispose();
            guards.Clear();
        }
    }
}
