using NaxUpdater.Core.Models;
using System.Diagnostics;
using System.Text.Json;

namespace NaxUpdater.Core.Services;

public sealed class MsixStoreUpdateProvider : IUpdateProvider
{
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly IStorePackageDeploymentService _store;
    private readonly HttpClient _httpClient;
    private readonly INativeStoreUpdateService? _nativeStore;
    private readonly StoreSearchCache? _searchCache;

    public MsixStoreUpdateProvider(
        HttpClient? httpClient = null,
        IStorePackageDeploymentService? store = null,
        INativeStoreUpdateService? nativeStore = null)
        : this(httpClient, store, nativeStore, store is null && nativeStore is null ? StoreSearchCache.Default : null)
    {
    }

    internal MsixStoreUpdateProvider(
        HttpClient? httpClient,
        IStorePackageDeploymentService? store,
        INativeStoreUpdateService? nativeStore,
        StoreSearchCache? searchCache)
    {
        _httpClient = httpClient ?? SharedHttpClient;
        _store = store ?? new StorePackageDeploymentService(_httpClient);
        _nativeStore = nativeStore ?? (store is null ? new NativeStoreUpdateService(_httpClient) : null);
        _searchCache = searchCache;
    }

    public string Id => "msix-store";
    public UpdateProviderDescriptor Descriptor { get; } = new(
        UpdateProviderAuthority.PlatformStore,
        100,
        "Exact installed MSIX package-family and Store product identity",
        [ManagementMode.Msix]);

    public bool CanHandle(InstalledApplication application) => application.ManagementMode == ManagementMode.Msix;
    public bool OwnsResultProviderId(string resultProviderId) => resultProviderId.Equals(Id, StringComparison.Ordinal);

    public async Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var packageFamily = PackageFamily(application);
        if (packageFamily is null)
        {
            return Result(application, null, UpdateStatus.ManagedExternally, null, null,
                "The installed MSIX package family could not be read.");
        }

        if (_nativeStore is not null && await _nativeStore.ReadQueueAsync(packageFamily, cancellationToken) is { } queued)
            return QueueResult(application, queued);

        var nativeAssessment = await CheckNativeCatalogAsync(application, packageFamily, cancellationToken);
        if (nativeAssessment is not null) return nativeAssessment;

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

    private async Task<UpdateCheckResult?> CheckNativeCatalogAsync(InstalledApplication application, string packageFamily,
        CancellationToken cancellationToken, string? expectedProductId = null)
    {
        if (_nativeStore is not null)
        {
            var product = await new MicrosoftStoreProductMetadataClient(_httpClient).ResolvePackageFamilyAsync(
                packageFamily, PackageArchitecture(application), application.NormalizedVersion, cancellationToken);
            if (product is not null)
            {
                if (expectedProductId is not null && product.Identity.ProductId != expectedProductId)
                    return Result(application, null, UpdateStatus.Error, null, product.Identity.ProductId, "The Store product does not match the publisher's identity.");
                // The Store broker answers native queries one at a time, so each one
                // lengthens the whole scan. When the official catalog publishes no
                // package newer than the installed one for this exact family and
                // architecture (any SKU), the native check can only report "no
                // update". Unknown or unparseable catalog versions still query natively;
                // queued Store operations were already detected before this point.
                if (product.LatestPublishedPackage is { } latest && Version.TryParse(application.NormalizedVersion, out _) &&
                    VersionOrder.Compare(latest.Version, application.NormalizedVersion) <= 0)
                    return Result(application, null, UpdateStatus.Current, null, product.Identity.ProductId,
                        "The official Store catalog publishes no newer package for the exact installed package family and architecture.");
                var catalogVersion = product.LatestPublishedPackage?.Version;
                var cacheKey = string.IsNullOrWhiteSpace(application.NormalizedVersion) ? null
                    : StoreSearchCache.Key(packageFamily, application.NormalizedVersion, catalogVersion);
                if (cacheKey is not null && _searchCache?.FindNoUpdate(cacheKey) is { } searchedAt)
                    return Result(application, null, UpdateStatus.Current, null, product.Identity.ProductId,
                        $"The native Microsoft Store check at {searchedAt.ToLocalTime():g} found no applicable update; the installed version and the Store catalog are unchanged since.");
                var offers = new List<(StoreProductIdentity Identity, NativeStoreOffer Offer)>();
                // One native request per admitted Store family. Parallel SKU
                // fan-out otherwise consumes all broker slots before later
                // families can start their own checks.
                foreach (var identity in new[] { product.Identity }.Concat(product.AlternateIdentities ?? []))
                    offers.Add((identity, await _nativeStore.CheckIdentityAsync(identity, cancellationToken)));
                if (offers.Select(o => o.Offer.QueueEntry).FirstOrDefault(q => q is not null) is { } queued)
                    return QueueResult(application, queued);
                var found = offers.FirstOrDefault(o => o.Offer.IsAvailable);
                if (found.Identity is null && offers.Any(o => o.Offer.CheckFailed)) return Result(application, null, UpdateStatus.Error, null, product.Identity.ProductId,
                    string.Join("; ", offers.Where(o => o.Offer.CheckFailed).Select(o => o.Offer.Error)));
                if (found.Identity is null)
                {
                    if (cacheKey is not null) _searchCache?.RecordNoUpdate(cacheKey);
                    // The catalog may list a newer package; without a Store offer for this
                    // installation it is not an update.
                    return Result(application, null, UpdateStatus.Current, null, product.Identity.ProductId,
                        "Native Microsoft Store check completed for the exact package family; no applicable update.")
                        with { AvailabilityReason = UpdateAvailabilityReason.NoApplicableStoreUpdate };
                }
                var package = await new MicrosoftStoreProductMetadataClient(_httpClient).GetPublishedPackageAsync(found.Identity.ProductId,
                        packageFamily, PackageArchitecture(application), application.NormalizedVersion, cancellationToken, found.Identity.SkuId);
                if (package is not null && VersionOrder.Compare(package.Version, application.NormalizedVersion) <= 0)
                    return Result(application, null, UpdateStatus.Error, null, product.Identity.ProductId,
                        "Windows Store reports an update, but the public catalog does not identify a newer target. The sources disagree; no reinstall or downgrade is offered.");
                if (package is null)
                    return Result(application, null, UpdateStatus.Error, null, product.Identity.ProductId,
                        "Windows Store returned an update, but its exact newer package version could not be verified.");
                var bindings = PackageProcessBindings(application, packageFamily);
                var nativePlan = new UpdateExecutionPlan(UpdateExecutionKind.NativeStorePackage, null, null, null, "Microsoft Store", null, [], false, [],
                    bindings.Names, StoreProductId: package.ProductId,
                    StorePackageFamilyName: packageFamily, StorePublisher: application.Publisher,
                    RunningExecutablePaths: bindings.Paths, NativeStoreTarget: package);
                return Result(application, nativePlan, UpdateStatus.Available, package.Version, product.Identity.ProductId,
                    "Native Microsoft Store update verified against the exact package family, architecture and published version.");
            }
        }

        return null;
    }

    private UpdateCheckResult QueueResult(InstalledApplication application, NativeStoreQueueEntry queued)
    {
        if (queued.State == Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallState.Error &&
            queued.ErrorCode == NativeStoreUpdateService.PackageIdentityConflictCode)
            return Result(application, null, UpdateStatus.Error, null, queued.ProductId, NativeStoreUpdateService.PackageIdentityConflictMessage);
        var reason = queued.State switch
        {
            Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallState.Downloading or
            Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallState.Installing or
            Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallState.RestoringData => UpdateAvailabilityReason.StoreUpdating,
            Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallState.Paused or
            Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallState.PausedLowBattery or
            Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallState.PausedWiFiRecommended or
            Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallState.PausedWiFiRequired => UpdateAvailabilityReason.StorePaused,
            _ => UpdateAvailabilityReason.StoreQueued
        };
        var canBind = Version.TryParse(application.NormalizedVersion, out _) && !string.IsNullOrWhiteSpace(queued.ProductId);
        var target = canBind ? new StoreQueueTarget(queued.ProductId, queued.PackageFamilyName, application.NormalizedVersion!) : null;
        var bindings = PackageProcessBindings(application, queued.PackageFamilyName);
        var plan = target is null || queued.MayAffectOtherItems ? null : new UpdateExecutionPlan(
            UpdateExecutionKind.NativeStoreQueue, null, null, null, "Microsoft Store", null, [], false, [], bindings.Names,
            StoreProductId: queued.ProductId, StorePackageFamilyName: queued.PackageFamilyName,
            RunningExecutablePaths: bindings.Paths,
            ProcessPolicy: bindings.Names.Count > 0 ? UpdateProcessPolicy.CloseBeforeApply : UpdateProcessPolicy.PlatformManaged,
            StoreQueueTarget: target);
        return Result(application, plan, UpdateStatus.StoreQueued, null, queued.ProductId,
            $"Microsoft Store queue: {queued.State} (0x{queued.ErrorCode:X8}). " +
            (plan is not null ? "NaxUpdater can resume this existing operation or track it if already running, then independently reread the installed package version."
                : "The queue identity cannot be bound safely, or Windows reports that resuming it affects other packages."))
            with { AvailabilityReason = reason, Applicability = plan is null ? UpdateApplicability.Unknown : UpdateApplicability.Applicable };
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

    // Store deployments run with AllowForcedAppRestart off, so a package whose app
    // is still open fails with ERROR_PACKAGES_IN_USE (0x80073D02). Bind the
    // executables the package manifest declares so the transaction closes them first.
    private static (IReadOnlyList<string> Names, IReadOnlyList<string> Paths) PackageProcessBindings(
        InstalledApplication application, string packageFamily)
    {
        var root = application.PrimaryInstallPath;
        if (string.IsNullOrWhiteSpace(root)) return ([], []);
        if (File.Exists(root) && Path.GetExtension(root).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return ([Path.GetFileNameWithoutExtension(root)], [Path.GetFullPath(root)]);
        if (!Directory.Exists(root)) return ([], []);
        var packageRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var paths = new List<string>();
        try
        {
            foreach (var executable in MsixManifestInspector.Inspect(Path.Combine(packageRoot, "AppxManifest.xml"), packageRoot).DeclaredExecutables)
            {
                if (Path.IsPathRooted(executable)) continue;
                var fullPath = Path.GetFullPath(Path.Combine(packageRoot, executable.Replace('/', Path.DirectorySeparatorChar)));
                if (fullPath.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath) &&
                    Path.GetExtension(fullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    paths.Add(fullPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return ([], []);
        }
        var distinct = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return (distinct.Select(static path => Path.GetFileNameWithoutExtension(path)).ToArray(), distinct);
    }
}
