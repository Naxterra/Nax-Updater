using Microsoft.Management.Deployment;

namespace NaxUpdater.Core.Services;

public sealed class StorePackageDeploymentService
{
    private readonly object _connectionLock = new();
    private Task<(PackageManager Manager, PackageCatalog Catalog)>? _storeConnection;

    public string? LastError { get; private set; }

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
            foreach (var query in SearchCandidates(packageFamilyName, installedDisplayName))
            {
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
        CancellationToken cancellationToken)
    {
        try
        {
            var (manager, catalog) = await GetStoreConnectionAsync(cancellationToken);
            var options = new FindPackagesOptions { ResultLimit = 5 };
            options.Selectors.Add(new PackageMatchFilter
            {
                Field = PackageMatchField.Id,
                Option = PackageFieldMatchOption.EqualsCaseInsensitive,
                Value = productId
            });
            var find = await catalog.FindPackagesAsync(options).AsTask(cancellationToken);
            CatalogPackage? package = null;
            if (find.Status == FindPackagesResultStatus.Ok)
            {
                for (var index = 0; index < find.Matches.Count; index++)
                {
                    var candidate = find.Matches[index].CatalogPackage;
                    var familyMatch = Contains(candidate.DefaultInstallVersion.PackageFamilyNames, packageFamilyName);
                    var metadataMatch = !string.IsNullOrWhiteSpace(installedDisplayName) &&
                                        candidate.Name.Equals(installedDisplayName, StringComparison.OrdinalIgnoreCase) &&
                                        PublisherMatches(candidate, installedPublisher);
                    if (candidate.Id.Equals(productId, StringComparison.OrdinalIgnoreCase) && (familyMatch || metadataMatch))
                    {
                        if (package is not null)
                        {
                            package = null;
                            break;
                        }
                        package = candidate;
                    }
                }
            }
            if (package is null)
            {
                return new UpdateExecutionResult(-1, false, "The exact Microsoft Store product could not be resolved from its package family.");
            }

            var installOptions = new InstallOptions
            {
                AcceptPackageAgreements = true,
                AllowUpgradeToUnknownVersion = true,
                PackageInstallMode = PackageInstallMode.Silent,
                PackageInstallScope = PackageInstallScope.Any,
                CorrelationData = "{\"caller\":\"NaxUpdater\"}"
            };
            var result = await manager.InstallPackageAsync(package, installOptions).AsTask(cancellationToken);
            var success = result.Status is InstallResultStatus.Ok or InstallResultStatus.NoApplicableUpgrade;
            var error = success
                ? null
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
