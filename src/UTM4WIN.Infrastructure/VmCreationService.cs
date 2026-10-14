using UTM4WIN.Application;

namespace UTM4WIN.Infrastructure;

public sealed class VmCreationService(JsonVmRepository repository, IDiskImageService diskImages) : IVmCreationService
{
    public Task<VmSnapshot> CreateAsync(VmCreationRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        repository.CreateWithDiskAsync(request, diskImages, progress, cancellationToken);
}
