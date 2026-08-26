using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

public interface IUpdateProvider
{
    string Id { get; }
    bool CanHandle(InstalledApplication application);
    Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken cancellationToken);
}
