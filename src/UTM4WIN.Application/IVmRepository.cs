using UTM4WIN.Domain;

namespace UTM4WIN.Application;

public sealed record VmSnapshot(VmConfiguration Configuration, string Revision);
public sealed record LibraryIssue(Guid? Id, string Message);
public sealed record VmLibrary(IReadOnlyList<VmSnapshot> Machines, IReadOnlyList<LibraryIssue> Issues, IReadOnlyList<Guid> UnregisteredIds);

public interface IVmRepository
{
    Task<VmLibrary> ListAsync(CancellationToken cancellationToken = default);
    Task<VmSnapshot> CreateAsync(VmConfiguration configuration, CancellationToken cancellationToken = default);
    Task<VmSnapshot> SaveAsync(VmConfiguration configuration, string expectedRevision, CancellationToken cancellationToken = default);
    Task RegisterAsync(Guid id, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class ConfigurationException(string message) : Exception(message);
public sealed class ConfigurationConflictException(string message) : Exception(message);

public sealed class VmDraft
{
    private readonly VmConfiguration _original;
    public VmDraft(VmSnapshot snapshot)
    {
        _original = snapshot.Configuration;
        Configuration = _original;
        Revision = snapshot.Revision;
    }

    public VmConfiguration Configuration { get; set; }
    public string Revision { get; }
    public bool HasChanges => Configuration != _original;
    public void Discard() => Configuration = _original;
}
