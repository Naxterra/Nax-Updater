using Microsoft.Management.Deployment;
using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

public interface IStorePackageDeploymentService
{
    string? LastError { get; }
    Task<StoreUpdateAvailability> CheckForUpdateAsync(
        string packageFamilyName,
        string? installedDisplayName,
        string? installedPublisher,
        string? installedVersion,
        string? installedArchitecture,
        CancellationToken cancellationToken);
    Task<StoreCatalogIdentity?> ResolveAsync(
        string packageFamilyName,
        string? installedDisplayName,
        string? installedPublisher,
        CancellationToken cancellationToken);
    Task<UpdateExecutionResult> InstallOrUpdateAsync(
        string productId,
        string packageFamilyName,
        string? installedDisplayName,
        string? installedPublisher,
        string expectedVersion,
        CancellationToken cancellationToken);
}

public sealed class StorePackageDeploymentService : IStorePackageDeploymentService
{
    public const string NoApplicableUpdateMessage = "Microsoft Store no longer reports an applicable update for this package.";

    private readonly object _connectionLock = new();
    private readonly SemaphoreSlim _catalogQuerySlots = new(12, 12);
    private Task<(PackageManager Manager, PackageCatalog Catalog)>? _storeConnection;
    private Task<(PackageManager Manager, PackageCatalog Catalog)>? _storeUpdateConnection;
    public StorePackageDeploymentService(HttpClient? httpClient = null) { }

    public string? LastError { get; private set; }

    public async Task<StoreUpdateAvailability> CheckForUpdateAsync(
        string packageFamilyName,
        string? installedDisplayName,
        string? installedPublisher,
        string? installedVersion,
        string? installedArchitecture,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(packageFamilyName))
        {
            return new StoreUpdateAvailability(false, false, null, null, "The installed package family is missing.");
        }

        await _catalogQuerySlots.WaitAsync(cancellationToken);
        try
        {
            var identity = await ResolveAsync(
                packageFamilyName,
                installedDisplayName,
                installedPublisher,
                cancellationToken);
            if (identity is null)
            {
                return new StoreUpdateAvailability(false, false, null, null,
                    LastError ?? "No exact Microsoft Store product matched the installed package family.");
            }

            var (_, catalog) = await GetStoreUpdateConnectionAsync(cancellationToken);
            var options = new FindPackagesOptions { ResultLimit = 5 };
            options.Selectors.Add(new PackageMatchFilter
            {
                Field = PackageMatchField.Id,
                Option = PackageFieldMatchOption.EqualsCaseInsensitive,
                Value = identity.ProductId
            });
            var result = await catalog.FindPackagesAsync(options).AsTask(cancellationToken);
            if (result.Status != FindPackagesResultStatus.Ok)
            {
                return new StoreUpdateAvailability(false, false, null, null,
                    $"Microsoft Store catalog lookup returned {result.Status}: {result.ExtendedErrorCode?.Message}");
            }

            CatalogPackage? package = null;
            for (var index = 0; index < result.Matches.Count; index++)
            {
                var candidate = result.Matches[index].CatalogPackage;
                if (!candidate.Id.Equals(identity.ProductId, StringComparison.OrdinalIgnoreCase) ||
                    !Contains(candidate.DefaultInstallVersion.PackageFamilyNames, packageFamilyName) &&
                    (candidate.InstalledVersion is null ||
                     !Contains(candidate.InstalledVersion.PackageFamilyNames, packageFamilyName)))
                {
                    continue;
                }
                if (package is not null && !package.Id.Equals(candidate.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return new StoreUpdateAvailability(false, false, null, null,
                        $"Multiple Store products matched installed package family {packageFamilyName}.");
                }
                package = candidate;
            }
            if (package is null)
            {
                return new StoreUpdateAvailability(false, false, null, null,
                    "No exact Store catalog product was correlated with the installed package family.");
            }

            var isUpdateAvailable = package.IsUpdateAvailable;
            var availableVersion = isUpdateAvailable ? StoreVersion(package) : null;
            return new StoreUpdateAvailability(
                true,
                isUpdateAvailable,
                identity.ProductId,
                availableVersion,
                null);
        }
        catch (Exception exception)
        {
            return new StoreUpdateAvailability(false, false, null, null, exception.Message);
        }
        finally
        {
            _catalogQuerySlots.Release();
        }
    }

    public async Task<StoreCatalogIdentity?> ResolveAsync(
        string packageFamilyName,
        string? installedDisplayName,
        string? installedPublisher,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(packageFamilyName))
        {
            return null;
        }
        try
        {
            var (_, catalog) = await GetStoreConnectionAsync(cancellationToken);
            CatalogPackage? resolved = null;
            CatalogPackage? metadataFallback = null;
            var diagnostics = new List<string>();
            var familyOptions = new FindPackagesOptions { ResultLimit = 5 };
            familyOptions.Selectors.Add(new PackageMatchFilter
            {
                Field = PackageMatchField.PackageFamilyName,
                Option = PackageFieldMatchOption.EqualsCaseInsensitive,
                Value = packageFamilyName
            });
            var familyResult = await catalog.FindPackagesAsync(familyOptions).AsTask(cancellationToken);
            if (familyResult.Status == FindPackagesResultStatus.Ok)
            {
                for (var index = 0; index < familyResult.Matches.Count; index++)
                {
                    var candidate = familyResult.Matches[index].CatalogPackage;
                    if (Contains(candidate.DefaultInstallVersion.PackageFamilyNames, packageFamilyName))
                    {
                        resolved = candidate;
                        break;
                    }
                }
            }
            foreach (var query in SearchCandidates(packageFamilyName, installedDisplayName))
            {
                if (resolved is not null)
                {
                    break;
                }
                var options = new FindPackagesOptions { ResultLimit = 25 };
                options.Selectors.Add(new PackageMatchFilter
                {
                    Field = PackageMatchField.Name,
                    Option = PackageFieldMatchOption.EqualsCaseInsensitive,
                    Value = query
                });
                var result = await catalog.FindPackagesAsync(options).AsTask(cancellationToken);
                if (result.Status != FindPackagesResultStatus.Ok)
                {
                    diagnostics.Add($"{query}:{result.Status}");
                    continue;
                }
                diagnostics.Add($"{query}:{result.Matches.Count}");
                for (var index = 0; index < result.Matches.Count; index++)
                {
                    var package = result.Matches[index].CatalogPackage;
                    var packageFamilies = package.DefaultInstallVersion.PackageFamilyNames;
                    if (!Contains(packageFamilies, packageFamilyName))
                    {
                        diagnostics.Add($"{query}->{package.Id}:{package.Name}:pfns={Join(packageFamilies)}:publisher={CatalogPublisher(package)}");
                        if (package.Name.Equals(query, StringComparison.OrdinalIgnoreCase) &&
                            PublisherMatches(package, installedPublisher))
                        {
                            if (metadataFallback is not null && !metadataFallback.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase))
                            {
                                metadataFallback = null;
                                diagnostics.Add($"{query}:ambiguous metadata fallback");
                            }
                            else
                            {
                                metadataFallback = package;
                            }
                        }
                        continue;
                    }
                    if (resolved is not null && !resolved.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        LastError = $"Multiple Store products expose package family {packageFamilyName}.";
                        return null;
                    }
                    resolved = package;
                }
                if (resolved is not null)
                {
                    break;
                }
            }
            resolved ??= metadataFallback;
            if (resolved is null)
            {
                LastError = $"Store search found no product exposing package family {packageFamilyName}. {string.Join(" | ", diagnostics)}";
                return null;
            }
            return new StoreCatalogIdentity(
                resolved.Id,
                resolved.Name,
                packageFamilyName,
                Contains(resolved.DefaultInstallVersion.PackageFamilyNames, packageFamilyName));
        }
        catch (Exception exception)
        {
            LastError = exception.ToString();
            return null;
        }
    }

    private static bool PublisherMatches(CatalogPackage package, string? installedPublisher)
    {
        if (string.IsNullOrWhiteSpace(installedPublisher))
        {
            return false;
        }
        try
        {
            var catalogPublisher = package.DefaultInstallVersion.GetCatalogPackageMetadata().Publisher;
            var installed = Normalize(installedPublisher);
            var catalog = Normalize(catalogPublisher);
            return installed.Length >= 4 && catalog.Length >= 4 &&
                   (installed.Contains(catalog, StringComparison.Ordinal) || catalog.Contains(installed, StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    private static string? CatalogPublisher(CatalogPackage package)
    {
        try
        {
            return package.DefaultInstallVersion.GetCatalogPackageMetadata().Publisher;
        }
        catch
        {
            return null;
        }
    }

    private static string Join(IReadOnlyList<string> values)
    {
        var items = new string[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            items[index] = values[index];
        }
        return string.Join(',', items);
    }

    private static string Normalize(string? value) => string.Concat(
        (value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant));

    public async Task<UpdateExecutionResult> InstallOrUpdateAsync(
        string productId,
        string packageFamilyName,
        string? installedDisplayName,
        string? installedPublisher,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            var (manager, catalog) = await GetStoreUpdateConnectionAsync(cancellationToken);
            var package = await FindExactPackageAsync(
                catalog,
                productId,
                packageFamilyName,
                installedDisplayName,
                installedPublisher,
                cancellationToken);
            if (package is null)
            {
                return new UpdateExecutionResult(-1, false, NoApplicableUpdateMessage);
            }
            var offeredVersion = StoreVersion(package);
            if (string.IsNullOrWhiteSpace(offeredVersion) ||
                !offeredVersion.Equals(expectedVersion, StringComparison.OrdinalIgnoreCase))
            {
                return new UpdateExecutionResult(
                    -1,
                    false,
                    $"Microsoft Store changed the applicable target from {expectedVersion} to {offeredVersion ?? "unknown"}; the approved update was not submitted.");
            }

            var installOptions = CreateInstallOptions();
            PackageVersionId? selectedVersion = null;
            for (var index = 0; index < package.AvailableVersions.Count; index++)
            {
                var key = package.AvailableVersions[index];
                if (key.Version.Equals(expectedVersion, StringComparison.OrdinalIgnoreCase))
                {
                    selectedVersion = key;
                    break;
                }
            }
            if (selectedVersion is null)
                return new UpdateExecutionResult(-1, false, "Microsoft Store no longer exposes the approved package version.");
            installOptions.PackageVersionId = selectedVersion;
            var result = await manager.UpgradePackageAsync(package, installOptions)
                .AsTask(cancellationToken);

            var success = result.Status == InstallResultStatus.Ok;
            var error = success
                ? null
                : result.Status == InstallResultStatus.NoApplicableUpgrade
                    ? $"{NoApplicableUpdateMessage} The installed package was not changed."
                    : $"Microsoft Store deployment returned {result.Status}: {result.ExtendedErrorCode?.Message}";
            return new UpdateExecutionResult(
                result.RebootRequired ? 3010 : unchecked((int)result.InstallerErrorCode),
                success,
                error);
        }
        catch (Exception exception)
        {
            return new UpdateExecutionResult(-1, false, exception.Message);
        }
    }

    internal static InstallOptions CreateInstallOptions() => new()
    {
        AcceptPackageAgreements = true,
        AllowUpgradeToUnknownVersion = false,
        Force = false,
        PackageInstallMode = PackageInstallMode.Silent,
        PackageInstallScope = PackageInstallScope.Any,
        CorrelationData = "{\"caller\":\"NaxUpdater\"}"
    };

    private static async Task<CatalogPackage?> FindExactPackageAsync(
        PackageCatalog catalog,
        string productId,
        string packageFamilyName,
        string? installedDisplayName,
        string? installedPublisher,
        CancellationToken cancellationToken)
    {
        var options = new FindPackagesOptions { ResultLimit = 5 };
        options.Selectors.Add(new PackageMatchFilter
        {
            Field = PackageMatchField.Id,
            Option = PackageFieldMatchOption.EqualsCaseInsensitive,
            Value = productId
        });
        var find = await catalog.FindPackagesAsync(options).AsTask(cancellationToken);
        CatalogPackage? package = null;
        if (find.Status != FindPackagesResultStatus.Ok)
        {
            return null;
        }
        for (var index = 0; index < find.Matches.Count; index++)
        {
            var candidate = find.Matches[index].CatalogPackage;
            var familyMatch = candidate.InstalledVersion is not null &&
                              Contains(candidate.InstalledVersion.PackageFamilyNames, packageFamilyName);
            var defaultFamilyMatch = Contains(candidate.DefaultInstallVersion.PackageFamilyNames, packageFamilyName);
            if (!candidate.Id.Equals(productId, StringComparison.OrdinalIgnoreCase) ||
                (!familyMatch && !defaultFamilyMatch))
            {
                continue;
            }
            if (package is not null)
            {
                return null;
            }
            package = candidate;
        }
        return package;
    }

    private async Task<(PackageManager Manager, PackageCatalog Catalog)> GetStoreConnectionAsync(CancellationToken cancellationToken)
    {
        Task<(PackageManager Manager, PackageCatalog Catalog)> connection;
        lock (_connectionLock)
        {
            _storeConnection ??= ConnectStoreCoreAsync();
            connection = _storeConnection;
        }
        return await connection.WaitAsync(cancellationToken);
    }

    private async Task<(PackageManager Manager, PackageCatalog Catalog)> GetStoreUpdateConnectionAsync(CancellationToken cancellationToken)
    {
        Task<(PackageManager Manager, PackageCatalog Catalog)> connection;
        lock (_connectionLock)
        {
            _storeUpdateConnection ??= ConnectStoreUpdateCoreAsync();
            connection = _storeUpdateConnection;
        }
        return await connection.WaitAsync(cancellationToken);
    }

    private static async Task<(PackageManager Manager, PackageCatalog Catalog)> ConnectStoreCoreAsync()
    {
        var manager = new PackageManager();
        var reference = manager.GetPredefinedPackageCatalog(PredefinedPackageCatalog.MicrosoftStore);
        reference.AcceptSourceAgreements = true;
        var connection = await reference.ConnectAsync().AsTask();
        if (connection.Status != ConnectResultStatus.Ok || connection.PackageCatalog is null)
        {
            throw new InvalidOperationException($"Microsoft Store catalog connection failed: {connection.Status} {connection.ExtendedErrorCode?.Message}");
        }
        return (manager, connection.PackageCatalog);
    }

    private static async Task<(PackageManager Manager, PackageCatalog Catalog)> ConnectStoreUpdateCoreAsync()
    {
        var manager = new PackageManager();
        var store = manager.GetPredefinedPackageCatalog(PredefinedPackageCatalog.MicrosoftStore);
        store.AcceptSourceAgreements = true;
        var options = new CreateCompositePackageCatalogOptions
        {
            CompositeSearchBehavior = CompositeSearchBehavior.RemotePackagesFromAllCatalogs,
            InstalledScope = PackageInstallScope.Any
        };
        options.Catalogs.Add(store);
        var reference = manager.CreateCompositePackageCatalog(options);
        var connection = await reference.ConnectAsync().AsTask();
        if (connection.Status != ConnectResultStatus.Ok || connection.PackageCatalog is null)
        {
            throw new InvalidOperationException($"Microsoft Store update catalog connection failed: {connection.Status} {connection.ExtendedErrorCode?.Message}");
        }
        return (manager, connection.PackageCatalog);
    }

    private static string? StoreVersion(CatalogPackage package)
    {
        try
        {
            var version = package.DefaultInstallVersion.Version;
            return string.IsNullOrWhiteSpace(version) || version.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                ? null
                : version;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> SearchCandidates(string packageFamilyName, string? displayName)
    {
        var results = new List<string>();
        var identity = packageFamilyName.Split('_', 2)[0];
        var humanized = System.Text.RegularExpressions.Regex.Replace(identity, @"[._-]+", " ");
        humanized = System.Text.RegularExpressions.Regex.Replace(humanized, @"([a-z0-9])([A-Z])", "$1 $2");
        humanized = System.Text.RegularExpressions.Regex.Replace(humanized, @"([A-Z]+)([A-Z][a-z])", "$1 $2");
        var words = humanized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1)
        {
            Add(string.Join(' ', words.Skip(1)));
        }
        Add(displayName);
        Add(humanized);
        return results;

        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Trim().Length >= 3)
            {
                var trimmed = value.Trim();
                if (!results.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                {
                    results.Add(trimmed);
                }
            }
        }
    }

    private static bool Contains(IReadOnlyList<string> values, string expected)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index].Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}

public sealed record StoreCatalogIdentity(
    string ProductId,
    string Name,
    string PackageFamilyName,
    bool PackageFamilyMatched);

public sealed record StoreUpdateAvailability(
    bool IsResolved,
    bool IsUpdateAvailable,
    string? ProductId,
    string? AvailableVersion,
    string? Error);
