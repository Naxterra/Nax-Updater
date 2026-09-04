using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

public interface IUpdateProvider
{
    string Id { get; }
    UpdateProviderDescriptor Descriptor { get; }
    bool OwnsResultProviderId(string resultProviderId) =>
        resultProviderId.Equals(Id, StringComparison.Ordinal);
    bool CanHandle(InstalledApplication application);
    Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken cancellationToken);
}
