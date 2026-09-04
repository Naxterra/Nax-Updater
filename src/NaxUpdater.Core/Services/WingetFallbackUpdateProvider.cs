using Microsoft.Data.Sqlite;
using NaxUpdater.Core.Internal;
using NaxUpdater.Core.Models;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Windows.Management.Deployment;

namespace NaxUpdater.Core.Services;

public sealed partial class WingetFallbackUpdateProvider(
    string? catalogIndexPath = null, IWingetPackageService? packageService = null) : IUpdateProvider, IUpdateProviderSourceRefresher
{
    private readonly IWingetPackageService _packages = packageService ?? new WingetPackageService();
    private readonly ConcurrentDictionary<string, CatalogIdentity> _identities = new(StringComparer.Ordinal);
    private string? _catalogRefreshError;

    public string Id => "winget-fallback";
    public UpdateProviderDescriptor Descriptor { get; } = new(
        UpdateProviderAuthority.FallbackCatalog,
        50,
        "Fallback catalog used only when no installed or producer-owned source claims the application",
        [ManagementMode.Unmanaged, ManagementMode.Registry, ManagementMode.WindowsInstaller, ManagementMode.DirectVendor]);

    public bool CanHandle(InstalledApplication application) => GetIdentity(application) is not null;

    async Task IUpdateProviderSourceRefresher.RefreshSourceAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(catalogIndexPath))
        {
            _catalogRefreshError = null;
            return;
        }

        try
        {
            var manager = new Microsoft.Management.Deployment.PackageManager();
            var reference = manager.GetPredefinedPackageCatalog(
                Microsoft.Management.Deployment.PredefinedPackageCatalog.OpenWindowsCatalog);
            reference.AcceptSourceAgreements = true;
            var refresh = await reference.RefreshPackageCatalogAsync().AsTask(cancellationToken);
            if (refresh.Status != Microsoft.Management.Deployment.RefreshPackageCatalogStatus.Ok)
            {
                _catalogRefreshError = $"{refresh.Status} {refresh.ExtendedErrorCode?.Message}".Trim();
                return;
            }

            _catalogRefreshError = null;
            _identities.Clear();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _catalogRefreshError = exception.Message;
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        var identity = GetIdentity(application);
        if (identity is null)
        {
            return Error(application, "No exact catalog identity match was found.");
        }

        if (!string.IsNullOrWhiteSpace(_catalogRefreshError))
        {
            return Error(application, $"The WinGet fallback catalog could not be refreshed; currency is not being claimed. {_catalogRefreshError}");
        }

        var availableVersion = identity.LatestVersion;
        const string source = "WinGet fallback catalog";

        var installedVersion = application.NormalizedVersion;
        if (string.IsNullOrWhiteSpace(installedVersion))
        {
            return Result(
                application,
                identity,
                availableVersion,
                UpdateStatus.NewerReleaseKnown,
                source,
                "The fallback catalog has a release, but the installed version is not meaningful enough to compare or update safely.",
                null);
        }

        var comparison = VersionOrder.Compare(availableVersion, installedVersion);
        if (comparison < 0)
        {
            return Result(
                application,
                identity,
                null,
                UpdateStatus.Current,
                source,
                $"The installed version {installedVersion} is newer than the fallback catalog version {availableVersion}; the older catalog value is not presented as an available release.",
                null);
        }
        if (comparison == 0)
        {
            return Result(application, identity, availableVersion, UpdateStatus.Current, source, null, null);
        }

        var offer = await _packages.AssessAsync(application, identity.Id, availableVersion, cancellationToken);
        var executable = InstalledApplicationMetadata.Executable(application);
        var plan = offer.Target is null ? null : new UpdateExecutionPlan(
            UpdateExecutionKind.WingetPackage, null, null, null, null, null, [], false, [],
            executable is null ? [] : [Path.GetFileNameWithoutExtension(executable)],
            RunningExecutablePaths: executable is null ? [] : [executable],
            WingetTarget: offer.Target);
        return Result(
            application,
            identity,
            availableVersion,
            UpdateStatus.Available,
            source,
            offer.Error ?? $"WinGet correlated the installed product with {identity.Id}; the official package manager will verify and apply version {availableVersion}.",
            plan);
    }

    private CatalogIdentity? GetIdentity(InstalledApplication application)
    {
        if (_identities.TryGetValue(application.Identity, out var cached))
        {
            return cached;
        }
        var discovered = FindWingetIdentity(application, ProductCode(application), catalogIndexPath);
        if (discovered is not null)
        {
            _identities.TryAdd(application.Identity, discovered);
        }
        return discovered;
    }

    private static CatalogIdentity? FindWingetIdentity(
        InstalledApplication application,
        string? productCode,
        string? catalogIndexPath)
    {
        var indexPath = !string.IsNullOrWhiteSpace(catalogIndexPath) && File.Exists(catalogIndexPath)
            ? catalogIndexPath
            : FindWingetIndex();
        if (indexPath is null)
        {
            return null;
        }
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = indexPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            connection.Open();
            if (!string.IsNullOrWhiteSpace(productCode))
            {
                using var productCommand = connection.CreateCommand();
                productCommand.CommandText = """
                    SELECT DISTINCT p.id, p.name, p.moniker, p.latest_version
                    FROM productcodes2 c
                    JOIN packages p ON p.rowid = c.package
                    WHERE upper(c.productcode) = upper($productCode)
                    ORDER BY p.id
                    """;
                productCommand.Parameters.AddWithValue("$productCode", productCode);
                using var productReader = productCommand.ExecuteReader();
                CatalogIdentity? productIdentity = null;
                while (productReader.Read())
                {
                    if (productIdentity is not null)
                    {
                        productIdentity = null;
                        break;
                    }
                    productIdentity = ReadIdentity(productReader, "registered product code");
                }
                if (productIdentity is not null)
                {
                    return productIdentity;
                }
            }

            var upgradeCode = UpgradeCode(application);
            var upgradeNames = CatalogNameCandidates(application).ToArray();
            if (!string.IsNullOrWhiteSpace(upgradeCode) && upgradeNames.Length > 0)
            {
                using var upgradeCommand = connection.CreateCommand();
                var upgradeNameParameters = upgradeNames.Select((_, index) => $"$upgradeName{index}").ToArray();
                upgradeCommand.CommandText = $"""
                    SELECT DISTINCT p.id, p.name, p.moniker, p.latest_version
                    FROM upgradecodes2 c
                    JOIN packages p ON p.rowid = c.package
                    JOIN norm_names2 n ON n.package = p.rowid
                    WHERE upper(c.upgradecode) = upper($upgradeCode)
                      AND n.norm_name IN ({string.Join(",", upgradeNameParameters)})
                    ORDER BY p.id
                    """;
                upgradeCommand.Parameters.AddWithValue("$upgradeCode", upgradeCode);
                for (var index = 0; index < upgradeNames.Length; index++)
                {
                    upgradeCommand.Parameters.AddWithValue(upgradeNameParameters[index], upgradeNames[index]);
                }
                using var upgradeReader = upgradeCommand.ExecuteReader();
                CatalogIdentity? upgradeIdentity = null;
                while (upgradeReader.Read())
                {
                    if (upgradeIdentity is not null)
                    {
                        upgradeIdentity = null;
                        break;
                    }
                    upgradeIdentity = ReadIdentity(upgradeReader, "MSI upgrade code");
                }
                if (upgradeIdentity is not null &&
                    upgradeNames.Contains(NormalizeCatalogValue(upgradeIdentity.Name), StringComparer.Ordinal))
                {
                    return upgradeIdentity;
                }
            }

            var packageFamily = PackageFamily(application);
            if (!string.IsNullOrWhiteSpace(packageFamily))
            {
                using var familyCommand = connection.CreateCommand();
                familyCommand.CommandText = """
                    SELECT p.id, p.name, p.moniker, p.latest_version
                    FROM pfns2 f
                    JOIN packages p ON p.rowid = f.package
                    WHERE lower(f.pfn) = lower($packageFamily)
                    ORDER BY p.id
                    """;
                familyCommand.Parameters.AddWithValue("$packageFamily", packageFamily);
                using var familyReader = familyCommand.ExecuteReader();
                CatalogIdentity? familyIdentity = null;
                while (familyReader.Read())
                {
                    if (familyIdentity is not null)
                    {
                        familyIdentity = null;
                        break;
                    }
                    familyIdentity = ReadIdentity(familyReader, "MSIX package family");
                }
                if (familyIdentity is not null)
                {
                    return familyIdentity;
                }
            }

            var publishers = CatalogPublisherCandidates(application.Publisher).ToArray();
            var names = CatalogNameCandidates(application).ToArray();
            if (publishers.Length == 0 || names.Length == 0)
            {
                return null;
            }
            using var nameCommand = connection.CreateCommand();
            var nameParameters = names.Select((_, index) => $"$name{index}").ToArray();
            var publisherParameters = publishers.Select((_, index) => $"$publisher{index}").ToArray();
            nameCommand.CommandText = $"""
                SELECT DISTINCT p.id, p.name, p.moniker, p.latest_version
                FROM packages p
                JOIN norm_names2 n ON n.package = p.rowid
                JOIN norm_publishers2 q ON q.package = p.rowid
                WHERE n.norm_name IN ({string.Join(",", nameParameters)})
                  AND q.norm_publisher IN ({string.Join(",", publisherParameters)})
                ORDER BY p.id
                """;
            for (var index = 0; index < names.Length; index++)
            {
                nameCommand.Parameters.AddWithValue(nameParameters[index], names[index]);
            }
            for (var index = 0; index < publishers.Length; index++)
            {
                nameCommand.Parameters.AddWithValue(publisherParameters[index], publishers[index]);
            }
            using var nameReader = nameCommand.ExecuteReader();
            CatalogIdentity? unique = null;
            while (nameReader.Read())
            {
                if (unique is not null)
                {
                    return null;
                }
                unique = ReadIdentity(nameReader, "unique name + publisher");
            }
            return unique;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static CatalogIdentity ReadIdentity(SqliteDataReader reader, string matchKind) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
        reader.GetString(3),
        matchKind);

    private static IEnumerable<string> CatalogNameCandidates(InstalledApplication application)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        Add(application.DisplayName);
        Add(VersionSuffixRegex().Replace(application.DisplayName, string.Empty));
        Add(ArchitectureSuffixRegex().Replace(application.DisplayName, string.Empty));
        foreach (var evidence in application.Evidence.Where(static item => item.Label == "Executable product"))
        {
            Add(evidence.Value);
        }
        return candidates;

        void Add(string? value)
        {
            var normalized = NormalizeCatalogValue(value);
            if (normalized.Length >= 3)
            {
                candidates.Add(normalized);
            }
        }
    }

    private static IEnumerable<string> CatalogPublisherCandidates(string? publisher)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        var normalized = NormalizeCatalogValue(publisher);
        if (normalized.Length >= 3)
        {
            candidates.Add(normalized);
        }
        string[] legalSuffixes =
        [
            "gesellschaftmitbeschraenkterhaftung",
            "gesellschaftmitbeschränkterhaftung",
            "incorporated",
            "corporation",
            "limited",
            "ptyltd",
            "gmbh",
            "llc",
            "ltd",
            "corp",
            "company",
            "plc",
            "inc"
        ];
        foreach (var suffix in legalSuffixes)
        {
            var normalizedSuffix = NormalizeCatalogValue(suffix);
            if (normalized.EndsWith(normalizedSuffix, StringComparison.Ordinal) &&
                normalized.Length - normalizedSuffix.Length >= 4)
            {
                candidates.Add(normalized[..^normalizedSuffix.Length]);
            }
        }
        return candidates;
    }

    private static string NormalizeCatalogValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : NativePathParser.NormalizeName(value);

    private static string? FindWingetIndex()
    {
        try
        {
            var package = new PackageManager()
                .FindPackagesForUser(string.Empty)
                .Where(static item =>
                    item.Id.Name.Equals("Microsoft.Winget.Source", StringComparison.OrdinalIgnoreCase) &&
                    item.Id.FamilyName.Equals("Microsoft.Winget.Source_8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static item => item.Id.Version.Major)
                .ThenByDescending(static item => item.Id.Version.Minor)
                .ThenByDescending(static item => item.Id.Version.Build)
                .ThenByDescending(static item => item.Id.Version.Revision)
                .FirstOrDefault();
            var path = package?.InstalledLocation is null
                ? null
                : Path.Combine(package.InstalledLocation.Path, "Public", "index.db");
            return path is not null && File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? DetectArchitecture(string? executablePath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
            {
                using var stream = File.OpenRead(executablePath);
                using var reader = new BinaryReader(stream);
                if (reader.ReadUInt16() == 0x5A4D)
                {
                    stream.Position = 0x3c;
                    var offset = reader.ReadInt32();
                    stream.Position = offset + 4;
                    return reader.ReadUInt16() switch { 0x8664 => "x64", 0xAA64 => "arm64", 0x014c => "x86", _ => null };
                }
            }
        }
        catch
        {
            // An update variant must not be guessed from the operating-system architecture.
        }
        return null;
    }

    private UpdateCheckResult Result(
        InstalledApplication application,
        CatalogIdentity identity,
        string? version,
        UpdateStatus status,
        string source,
        string? message,
        UpdateExecutionPlan? plan) => new(
            application.Identity,
            application.DisplayName,
            application.NormalizedVersion,
            version,
            status,
            Id,
            "WinGet fallback",
            string.IsNullOrWhiteSpace(plan?.WingetTarget?.Locale) ? "application-managed" : plan.WingetTarget.Locale,
            "Selected by Windows Package Manager for the installed application",
            plan?.WingetTarget?.Architecture.ToLowerInvariant() ?? DetectArchitecture(application.PrimaryInstallPath) ?? "unknown",
            "stable",
            $"https://github.com/microsoft/winget-pkgs/tree/master/manifests/{char.ToLowerInvariant(identity.Id[0])}/{string.Join('/', identity.Id.Split('.'))}",
            message ?? $"{identity.MatchKind} match to {identity.Id}; {source} reports the current version.",
            plan);

    private UpdateCheckResult Error(InstalledApplication application, string message) => new(
        application.Identity, application.DisplayName, application.NormalizedVersion, null, UpdateStatus.Error,
        Id, "WinGet fallback", "unknown", "Language was not resolved", "unknown", "unknown", null, message, null);

    private static string? ProductCode(InstalledApplication application)
    {
        if (application.RemovalPlan?.Kind == RemovalKind.WindowsInstaller)
        {
            var msiMatch = ProductCodeRegex().Match(application.RemovalPlan.Arguments ?? string.Empty);
            if (msiMatch.Success)
            {
                return msiMatch.Value;
            }
        }
        foreach (var evidence in application.Evidence.Where(static item => item.Label == "Uninstall registry"))
        {
            var separator = evidence.Value.LastIndexOf(" · ", StringComparison.Ordinal);
            var key = separator >= 0 ? evidence.Value[(separator + 3)..].Trim() : evidence.Value.Trim();
            if (key.EndsWith("_is1", StringComparison.OrdinalIgnoreCase) ||
                ProductCodeRegex().IsMatch(key) ||
                IsSafeRegisteredProductCode(key))
            {
                return key;
            }
        }
        return null;
    }

    private static bool IsSafeRegisteredProductCode(string value) =>
        value.Length is >= 3 and <= 200 &&
        !value.Any(char.IsControl) &&
        !value.Contains('\\') &&
        !value.Contains('/') &&
        !value.Contains(':');

    private static string? UpgradeCode(InstalledApplication application) => application.Evidence
        .FirstOrDefault(static item => item.Label == RegistryInventoryScanner.InstallerUpgradeFamilyEvidenceLabel)
        ?.Value;

    private static bool IsStrongCatalogIdentity(CatalogIdentity identity) =>
        identity.MatchKind.EndsWith("product code", StringComparison.Ordinal) ||
        identity.MatchKind.Equals("MSI upgrade code", StringComparison.Ordinal);

    private static string? PackageFamily(InstalledApplication application)
    {
        const string prefix = "msix:";
        if (application.Identity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return application.Identity[prefix.Length..];
        }
        return application.Evidence
            .FirstOrDefault(static item => item.Label == "MSIX package family")
            ?.Value;
    }

    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}", RegexOptions.CultureInvariant)]
    private static partial Regex ProductCodeRegex();

    [GeneratedRegex(@"(?:\s+|\s*[-(]\s*)v?\d+(?:\.\d+)+(?:[^)]*)?\)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionSuffixRegex();

    [GeneratedRegex(@"\s*\((?:x64|x86|arm64|64-bit|32-bit)[^)]*\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArchitectureSuffixRegex();

    private sealed record CatalogIdentity(string Id, string Name, string Moniker, string LatestVersion, string MatchKind);
}
