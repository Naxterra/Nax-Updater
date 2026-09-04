using Microsoft.Data.Sqlite;
using NaxUpdater.Core.Internal;
using NaxUpdater.Core.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Windows.Management.Deployment;

namespace NaxUpdater.Core.Services;

public sealed partial class WingetFallbackUpdateProvider(HttpClient httpClient, string? catalogIndexPath = null) : IUpdateProvider
{
    private readonly ConcurrentDictionary<string, CatalogIdentity> _identities = new(StringComparer.Ordinal);

    public string Id => "winget-fallback";
    public UpdateProviderDescriptor Descriptor { get; } = new(
        UpdateProviderAuthority.FallbackCatalog,
        50,
        "Fallback catalog used only when no installed or producer-owned source claims the application",
        [ManagementMode.Unmanaged, ManagementMode.Registry, ManagementMode.WindowsInstaller, ManagementMode.DirectVendor]);

    public bool CanHandle(InstalledApplication application) => GetIdentity(application) is not null;

    public async Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        var identity = GetIdentity(application);
        if (identity is null)
        {
            return Error(application, "No exact catalog identity match was found.");
        }

        var availableVersion = identity.LatestVersion;
        const string source = "WinGet fallback catalog";

        var installedVersion = CatalogInstalledVersion(application);
        if (VersionOrder.Compare(availableVersion, installedVersion) <= 0)
        {
            return Result(application, identity, availableVersion, UpdateStatus.Current, source, null, null);
        }

        if (!IsStrongCatalogIdentity(identity))
        {
            return Result(
                application,
                identity,
                availableVersion,
                UpdateStatus.Available,
                source,
                "A unique name-and-publisher catalog match found a newer version, but this weaker identity is intentionally not installable.",
                null);
        }

        var manifest = await ReadWingetInstallerManifestAsync(identity, cancellationToken);
        var architecture = DetectArchitecture(application.PrimaryInstallPath);
        var installer = SelectInstaller(manifest, architecture, CultureInfo.CurrentUICulture.Name);
        if (installer is null)
        {
            return Result(
                application,
                identity,
                availableVersion,
                UpdateStatus.Available,
                source,
                "A newer catalog version exists, but no compatible verified installer was found.",
                null);
        }

        var signer = ResolveInstalledSigner(application, identity);
        if (string.IsNullOrWhiteSpace(signer))
        {
            return Result(
                application,
                identity,
                availableVersion,
                UpdateStatus.Available,
                source,
                "A newer exact catalog version exists, but no trusted installed publisher could be bound to the downloaded installer. Automatic installation is blocked.",
                null);
        }

        var plan = new UpdateExecutionPlan(
            installer.Kind,
            installer.Uri,
            Path.GetFileName(installer.Uri.LocalPath),
            installer.Sha256,
            signer,
            null,
            installer.Arguments,
            application.Scope == InstallScope.Machine,
            AllowedHosts(installer.Uri),
            installer.Kind is UpdateExecutionKind.DownloadedMsi or UpdateExecutionKind.DownloadedZipMsi
                ? []
                : RunningProcesses(application),
            null,
            true,
            true,
            NestedInstallerRelativePath: installer.NestedInstallerRelativePath);
        return Result(
            application,
            identity,
            availableVersion,
            UpdateStatus.Available,
            source,
            $"Exact MSI product-code match to {identity.Id}; installed state remains sourced from NaxUpdater.",
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
                    SELECT p.id, p.name, p.moniker, p.latest_version
                    FROM productcodes2 c
                    JOIN packages p ON p.rowid = c.package
                    WHERE upper(c.productcode) = upper($productCode)
                    ORDER BY p.id
                    LIMIT 1
                    """;
                productCommand.Parameters.AddWithValue("$productCode", productCode);
                using var productReader = productCommand.ExecuteReader();
                if (productReader.Read())
                {
                    return ReadIdentity(productReader, "registered product code");
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
                .Where(static item => item.Id.Name.Equals("Microsoft.Winget.Source", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static item => item.Id.Version.Major)
                .ThenByDescending(static item => item.Id.Version.Minor)
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

    private async Task<IReadOnlyList<CatalogInstaller>> ReadWingetInstallerManifestAsync(
        CatalogIdentity identity,
        CancellationToken cancellationToken)
    {
        var segments = identity.Id.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        var path = string.Join('/', segments);
        var uri = $"https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/{char.ToLowerInvariant(identity.Id[0])}/{path}/{Uri.EscapeDataString(identity.LatestVersion)}/{Uri.EscapeDataString(identity.Id)}.installer.yaml";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("NaxUpdater/0.16.1");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }
        var yaml = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseWingetInstallers(yaml);
    }

    private static IReadOnlyList<CatalogInstaller> ParseWingetInstallers(string yaml)
    {
        var installers = new List<CatalogInstaller>();
        string? globalType = null;
        string? globalNestedType = null;
        string? globalNestedPath = null;
        string? architecture = null;
        string? locale = null;
        string? type = null;
        string? url = null;
        string? hash = null;

        void Commit()
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
                hash?.Length == 64 && hash.All(Uri.IsHexDigit))
            {
                var installerType = type ?? globalType;
                var normalizedType = installerType?.ToLowerInvariant();
                var kind = normalizedType switch
                {
                    "msi" or "wix" => UpdateExecutionKind.DownloadedMsi,
                    "exe" or "inno" or "nullsoft" or "burn" => UpdateExecutionKind.DownloadedExe,
                    "zip" when globalNestedType?.Equals("msi", StringComparison.OrdinalIgnoreCase) == true &&
                               !string.IsNullOrWhiteSpace(globalNestedPath) => UpdateExecutionKind.DownloadedZipMsi,
                    _ => (UpdateExecutionKind?)null
                };
                if (kind.HasValue)
                {
                    var arguments = normalizedType switch
                    {
                        "msi" or "wix" => new[] { "/qn", "/norestart" },
                        "zip" when kind == UpdateExecutionKind.DownloadedZipMsi => new[] { "/qn", "/norestart" },
                        "inno" => new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" },
                        "nullsoft" => new[] { "/S" },
                        _ => []
                    };
                    installers.Add(new CatalogInstaller(
                        architecture ?? "neutral",
                        locale,
                        kind.Value,
                        uri,
                        hash,
                        arguments,
                        kind == UpdateExecutionKind.DownloadedZipMsi ? globalNestedPath : null));
                }
            }
            architecture = locale = type = url = hash = null;
        }

        foreach (var rawLine in yaml.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("- Architecture:", StringComparison.OrdinalIgnoreCase))
            {
                Commit();
                architecture = Scalar(line);
            }
            else if (line.StartsWith("Architecture:", StringComparison.OrdinalIgnoreCase)) architecture = Scalar(line);
            else if (line.StartsWith("InstallerLocale:", StringComparison.OrdinalIgnoreCase)) locale = Scalar(line);
            else if (line.StartsWith("InstallerUrl:", StringComparison.OrdinalIgnoreCase)) url = Scalar(line);
            else if (line.StartsWith("InstallerSha256:", StringComparison.OrdinalIgnoreCase)) hash = Scalar(line);
            else if (line.StartsWith("NestedInstallerType:", StringComparison.OrdinalIgnoreCase)) globalNestedType = Scalar(line);
            else if (line.StartsWith("- RelativeFilePath:", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("RelativeFilePath:", StringComparison.OrdinalIgnoreCase)) globalNestedPath = Scalar(line);
            else if (line.StartsWith("InstallerType:", StringComparison.OrdinalIgnoreCase))
            {
                if (architecture is null) globalType = Scalar(line); else type = Scalar(line);
            }
        }
        Commit();
        return installers;
    }

    internal static CatalogInstaller? SelectInstaller(
        IReadOnlyList<CatalogInstaller> installers,
        string architecture,
        string preferredLocale) =>
        installers
            .Where(installer => LocaleCompatible(installer.Locale, preferredLocale))
            .OrderByDescending(installer =>
                (installer.Architecture.Equals(architecture, StringComparison.OrdinalIgnoreCase) ? 100 :
                 installer.Architecture.Equals("neutral", StringComparison.OrdinalIgnoreCase) ? 10 : 0) +
                LocaleScore(installer.Locale, preferredLocale))
            .FirstOrDefault(installer =>
                installer.Architecture.Equals(architecture, StringComparison.OrdinalIgnoreCase) ||
                installer.Architecture.Equals("neutral", StringComparison.OrdinalIgnoreCase));

    private static bool LocaleCompatible(string? locale, string preferredLocale)
    {
        // The fallback catalog does not prove the language of the installed application.
        // Only an installer explicitly declared neutral/multilingual is eligible.
        return string.IsNullOrWhiteSpace(locale) ||
               locale.Equals("neutral", StringComparison.OrdinalIgnoreCase);
    }

    private static int LocaleScore(string? locale, string preferredLocale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return 10;
        if (locale.Equals(preferredLocale, StringComparison.OrdinalIgnoreCase)) return 50;
        var preferredLanguage = preferredLocale.Split('-', 2)[0];
        var installerLanguage = locale.Split('-', 2)[0];
        return installerLanguage.Equals(preferredLanguage, StringComparison.OrdinalIgnoreCase) ? 40 : 0;
    }

    private static string? ResolveInstalledSigner(InstalledApplication application, CatalogIdentity identity)
    {
        var signer = NativeAuthenticodeVerifier.GetTrustedSigner(application.PrimaryInstallPath ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(signer))
        {
            return signer;
        }
        try
        {
            var installRoot = !string.IsNullOrWhiteSpace(application.PrimaryInstallPath) && Directory.Exists(application.PrimaryInstallPath)
                ? application.PrimaryInstallPath
                : !string.IsNullOrWhiteSpace(application.PrimaryInstallPath)
                    ? Path.GetDirectoryName(application.PrimaryInstallPath)
                    : null;
            if (!string.IsNullOrWhiteSpace(installRoot) && Directory.Exists(installRoot))
            {
                foreach (var executable in Directory.EnumerateFiles(installRoot, "*.exe", SearchOption.AllDirectories).Take(128))
                {
                    signer = NativeAuthenticodeVerifier.GetTrustedSigner(executable);
                    if (!string.IsNullOrWhiteSpace(signer))
                    {
                        return signer;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Continue with PATH-based command discovery.
        }
        var commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in new[] { identity.Moniker, identity.Id.Split('.').LastOrDefault() ?? string.Empty })
        {
            var normalized = Regex.Replace(value, @"[^A-Za-z0-9]", string.Empty);
            if (normalized.Length > 0) commands.Add(normalized);
            if (normalized.EndsWith("lts", StringComparison.OrdinalIgnoreCase)) commands.Add(normalized[..^3]);
            if (normalized.EndsWith("js", StringComparison.OrdinalIgnoreCase)) commands.Add(normalized[..^2]);
        }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var command in commands)
            {
                var path = Path.Combine(directory.Trim().Trim('"'), command + ".exe");
                signer = NativeAuthenticodeVerifier.GetTrustedSigner(path);
                if (!string.IsNullOrWhiteSpace(signer))
                {
                    return signer;
                }
            }
        }
        return null;
    }

    private static string DetectArchitecture(string? executablePath)
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
                    return reader.ReadUInt16() switch { 0x8664 => "x64", 0xAA64 => "arm64", 0x014c => "x86", _ => "x64" };
                }
            }
        }
        catch
        {
            // Use the operating-system architecture when executable metadata is unavailable.
        }
        return System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? "arm64"
            : "x64";
    }

    private UpdateCheckResult Result(
        InstalledApplication application,
        CatalogIdentity identity,
        string version,
        UpdateStatus status,
        string source,
        string? message,
        UpdateExecutionPlan? plan) => new(
            application.Identity,
            application.DisplayName,
            CatalogInstalledVersion(application),
            version,
            status,
            Id,
            "WinGet fallback",
            "neutral",
            "Vendor multi-language installer",
            DetectArchitecture(application.PrimaryInstallPath),
            "stable",
            $"https://github.com/microsoft/winget-pkgs/tree/master/manifests/{char.ToLowerInvariant(identity.Id[0])}/{string.Join('/', identity.Id.Split('.'))}",
            message ?? $"{identity.MatchKind} match to {identity.Id}; {source} reports the current version.",
            plan);

    private UpdateCheckResult Error(InstalledApplication application, string message) => new(
        application.Identity, application.DisplayName, application.NormalizedVersion, null, UpdateStatus.Error,
        Id, "WinGet fallback", "neutral", "Vendor multi-language installer", "unknown", "unknown", null, message, null);

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

    private static string? CatalogInstalledVersion(InstalledApplication application)
    {
        var versions = new List<string>();
        if (!string.IsNullOrWhiteSpace(application.NormalizedVersion))
        {
            versions.Add(application.NormalizedVersion);
        }
        versions.AddRange(application.Evidence
            .Where(static item => item.Label == "Registry version" && !string.IsNullOrWhiteSpace(item.Value))
            .Select(static item => item.Value.Trim()));
        return versions.OrderByDescending(static version => version, Comparer<string>.Create(VersionOrder.Compare)).FirstOrDefault();
    }

    private static IReadOnlyList<string> AllowedHosts(Uri uri) =>
        new[] { uri.Host, "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" }
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static IReadOnlyList<string> RunningProcesses(InstalledApplication application)
    {
        var process = Path.GetFileNameWithoutExtension(application.PrimaryInstallPath);
        return string.IsNullOrWhiteSpace(process) ? [] : [process];
    }

    private static string Scalar(string line) => line[(line.IndexOf(':') + 1)..].Trim().Trim('"', '\'');

    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}", RegexOptions.CultureInvariant)]
    private static partial Regex ProductCodeRegex();

    [GeneratedRegex(@"(?:\s+|\s*[-(]\s*)v?\d+(?:\.\d+)+(?:[^)]*)?\)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionSuffixRegex();

    [GeneratedRegex(@"\s*\((?:x64|x86|arm64|64-bit|32-bit)[^)]*\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArchitectureSuffixRegex();

    private sealed record CatalogIdentity(string Id, string Name, string Moniker, string LatestVersion, string MatchKind);
    internal sealed record CatalogInstaller(
        string Architecture,
        string? Locale,
        UpdateExecutionKind Kind,
        Uri Uri,
        string Sha256,
        IReadOnlyList<string> Arguments,
        string? NestedInstallerRelativePath = null);
}
