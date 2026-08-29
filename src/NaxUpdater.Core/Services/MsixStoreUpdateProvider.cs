using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

public sealed class MsixStoreUpdateProvider : IUpdateProvider
{
    private readonly StorePackageDeploymentService _store;

    public MsixStoreUpdateProvider(HttpClient? httpClient = null)
    {
        _store = new StorePackageDeploymentService(httpClient);
    }

    public string Id => "msix-store";

    public bool CanHandle(InstalledApplication application) => application.ManagementMode == ManagementMode.Msix;

    public async Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var packageFamily = PackageFamily(application);
        if (packageFamily is null)
        {
            return Result(application, null, UpdateStatus.ManagedExternally, null, null,
                "The installed MSIX package family could not be read.");
        }

        var availability = await _store.CheckForUpdateAsync(
            packageFamily,
            application.DisplayName,
            application.Publisher,
            application.NormalizedVersion,
            PackageArchitecture(application),
            cancellationToken);
        if (!availability.IsResolved)
        {
            return Result(application, null, UpdateStatus.ManagedExternally, null, null, availability.Error);
        }
        if (!availability.IsUpdateAvailable || string.IsNullOrWhiteSpace(availability.ProductId))
        {
            return Result(application, null, UpdateStatus.Current, null, availability.ProductId,
                "Microsoft Store reports no applicable update for the installed package.");
        }

        var plan = new UpdateExecutionPlan(
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
                StoreProductId: availability.ProductId,
                StorePackageFamilyName: packageFamily,
                StorePublisher: application.Publisher);
        return Result(application, plan, UpdateStatus.Available, availability.AvailableVersion, availability.ProductId,
            "Microsoft Store reports an applicable update for the exact installed package family.");
    }

    private UpdateCheckResult Result(
        InstalledApplication application,
        UpdateExecutionPlan? plan,
        UpdateStatus status,
        string? availableVersion,
        string? productId,
        string? message) => new(
            application.Identity,
            application.DisplayName,
            application.NormalizedVersion,
            availableVersion,
            status,
            Id,
            "Microsoft Store / MSIX",
            "application-managed",
            "Preserved by Microsoft Store/MSIX package",
            "provider-selected",
            "stable",
            string.IsNullOrWhiteSpace(productId) ? null : $"ms-windows-store://pdp/?ProductId={productId}",
            message,
            plan);

    private static string? PackageFamily(InstalledApplication application)
    {
        const string prefix = "msix:";
        return application.Identity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? application.Identity[prefix.Length..]
            : application.Evidence.FirstOrDefault(static item => item.Label == "MSIX package family")?.Value;
    }

    private static string? PackageArchitecture(InstalledApplication application) =>
        application.Evidence.FirstOrDefault(static item => item.Label == "MSIX package architecture")?.Value;
}
