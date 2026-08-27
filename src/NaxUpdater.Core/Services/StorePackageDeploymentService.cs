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
                for (var index = 0; index < result.Matches.Count; index++)
                {
                    var package = result.Matches[index].CatalogPackage;
                    if (!Contains(package.DefaultInstallVersion.PackageFamilyNames, packageFamilyName))
                    {
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
            if (resolved is null)
            {
                LastError = $"Store search found no product exposing package family {packageFamilyName}. {string.Join(" | ", diagnostics)}";
                return null;
            }
            return new StoreCatalogIdentity(resolved.Id, resolved.Name, packageFamilyName);
        }
        catch (Exception exception)
        {
            LastError = exception.ToString();
            return null;
        }
    }

    public async Task<UpdateExecutionResult> InstallOrUpdateAsync(
        string productId,
        string packageFamilyName,
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
                    if (candidate.Id.Equals(productId, StringComparison.OrdinalIgnoreCase) &&
                        Contains(candidate.DefaultInstallVersion.PackageFamilyNames, packageFamilyName))
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

public sealed record StoreCatalogIdentity(string ProductId, string Name, string PackageFamilyName);
