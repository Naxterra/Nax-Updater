using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

public sealed class MsixStoreUpdateProvider : IUpdateProvider
{
    public string Id => "msix-store";

    public bool CanHandle(InstalledApplication application) => application.ManagementMode == ManagementMode.Msix;

    public Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var packageFamily = PackageFamily(application);
        var plan = packageFamily is null
            ? null
            : new UpdateExecutionPlan(
                UpdateExecutionKind.StorePackage,
                null,
                null,
                null,
                "Microsoft Store",
                null,
                [],
                false,
                [],
                [],
                StoreProductId: null,
                StorePackageFamilyName: packageFamily);
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
            "The installed package family will be resolved to an exact Microsoft Store Product ID when Store update is selected.",
            plan));
    }

    private static string? PackageFamily(InstalledApplication application)
    {
        const string prefix = "msix:";
        return application.Identity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? application.Identity[prefix.Length..]
            : application.Evidence.FirstOrDefault(static item => item.Label == "MSIX package family")?.Value;
    }
}
