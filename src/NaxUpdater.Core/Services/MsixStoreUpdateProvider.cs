using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

public sealed class MsixStoreUpdateProvider : IUpdateProvider
{
    public string Id => "msix-store";

    public bool CanHandle(InstalledApplication application) => application.ManagementMode == ManagementMode.Msix;

    public Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new UpdateCheckResult(
            application.Identity,
            application.DisplayName,
            application.NormalizedVersion,
            null,
            UpdateStatus.ManagedExternally,
            Id,
            "Microsoft Store / MSIX",
            "application-managed",
            "Preserved by Microsoft Store/MSIX package",
            "provider-selected",
            "stable",
            "ms-windows-store://downloadsandupdates",
            "Windows services this package through Microsoft Store or its registered MSIX deployment source.",
            null));
    }
}
