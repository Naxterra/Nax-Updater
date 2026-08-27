using Microsoft.Data.Sqlite;
using NaxUpdater.Core.Internal;
using NaxUpdater.Core.Models;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.Management.Deployment;

namespace NaxUpdater.Core.Services;

public sealed partial class FederatedCatalogUpdateProvider(HttpClient httpClient, string? catalogIndexPath = null) : IUpdateProvider
{
    private readonly ConcurrentDictionary<string, CatalogIdentity> _identities = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<ScoopCandidate?>> _scoopCandidates = new(StringComparer.OrdinalIgnoreCase);

    public string Id => "federated-public-catalogs";

    public bool CanHandle(InstalledApplication application) => GetIdentity(application) is not null;

    public async Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        var identity = GetIdentity(application);
        if (identity is null)
        {
            return Error(application, "No exact catalog identity match was found.");
        }

        var scoop = string.IsNullOrWhiteSpace(identity.Moniker)
            ? null
            : await _scoopCandidates.GetOrAdd(identity.Moniker, _ => ReadScoopCandidateAsync(identity.Moniker, cancellationToken));
        var availableVersion = identity.LatestVersion;
        var source = "WinGet catalog";
        if (scoop is not null && VersionOrder.Compare(scoop.Version, availableVersion) > 0)
        {
            availableVersion = scoop.Version;
            source = "Scoop catalog";
        }

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
        var installer = SelectInstaller(manifest, architecture);
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

        if (VersionOrder.Compare(availableVersion, identity.LatestVersion) > 0)
        {
            installer = await TryPromoteVersionedInstallerAsync(installer, identity.LatestVersion, availableVersion, cancellationToken);
            if (installer is null)
            {
                return Result(
                    application,
                    identity,
                    availableVersion,
                    UpdateStatus.Available,
                    source,
                    "A fresher catalog reports an update, but no official checksum-backed installer could be derived safely.",
                    null);
            }
        }

        var signer = ResolveInstalledSigner(application, identity);

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
            !string.IsNullOrWhiteSpace(signer),
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

            var publisher = NormalizeCatalogValue(application.Publisher);
            var names = CatalogNameCandidates(application).ToArray();
            if (publisher.Length == 0 || names.Length == 0)
            {
                return null;
            }
            using var nameCommand = connection.CreateCommand();
            var nameParameters = names.Select((_, index) => $"$name{index}").ToArray();
            nameCommand.CommandText = $"""
                SELECT DISTINCT p.id, p.name, p.moniker, p.latest_version
                FROM packages p
                JOIN norm_names2 n ON n.package = p.rowid
                JOIN norm_publishers2 q ON q.package = p.rowid
                WHERE n.norm_name IN ({string.Join(",", nameParameters)})
                  AND q.norm_publisher = $publisher
                ORDER BY p.id
                """;
            for (var index = 0; index < names.Length; index++)
            {
                nameCommand.Parameters.AddWithValue(nameParameters[index], names[index]);
            }
            nameCommand.Parameters.AddWithValue("$publisher", publisher);
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

    private async Task<ScoopCandidate?> ReadScoopCandidateAsync(string moniker, CancellationToken cancellationToken)
    {
        if (!SafePartRegex().IsMatch(moniker))
        {
            return null;
        }
        foreach (var bucket in new[] { "Main", "Extras" })
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://raw.githubusercontent.com/ScoopInstaller/{bucket}/master/bucket/{moniker}.json");
            request.Headers.UserAgent.ParseAdd("NaxUpdater/0.6");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                continue;
            }
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var root = document.RootElement;
            var version = root.TryGetProperty("version", out var versionElement) ? versionElement.GetString() : null;
            var homepage = root.TryGetProperty("homepage", out var homepageElement) ? homepageElement.GetString() : null;
            if (!string.IsNullOrWhiteSpace(version))
            {
                return new ScoopCandidate(version, homepage);
            }
        }
        return null;
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
        request.Headers.UserAgent.ParseAdd("NaxUpdater/0.6");
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
                        kind.Value,
                        uri,
                        hash,
                        arguments,
                        kind == UpdateExecutionKind.DownloadedZipMsi ? globalNestedPath : null));
                }
            }
            architecture = type = url = hash = null;
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

    private async Task<CatalogInstaller?> TryPromoteVersionedInstallerAsync(
        CatalogInstaller installer,
        string oldVersion,
        string newVersion,
        CancellationToken cancellationToken)
    {
        var oldUrl = installer.Uri.AbsoluteUri;
        if (!oldUrl.Contains(oldVersion, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var candidateUrl = oldUrl.Replace(oldVersion, newVersion, StringComparison.OrdinalIgnoreCase);
        if (!Uri.TryCreate(candidateUrl, UriKind.Absolute, out var candidateUri) ||
            candidateUri.Scheme != Uri.UriSchemeHttps ||
            !candidateUri.Host.Equals(installer.Uri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var checksumUri = new Uri(candidateUri, "SHASUMS256.txt");
        using var response = await httpClient.GetAsync(checksumUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        var checksums = await response.Content.ReadAsStringAsync(cancellationToken);
        var fileName = Path.GetFileName(candidateUri.LocalPath);
        var match = Regex.Match(
            checksums,
            $"(?im)^(?<hash>[0-9a-f]{{64}})\\s+\\*?{Regex.Escape(fileName)}\\s*$",
            RegexOptions.CultureInvariant);
        return match.Success
            ? installer with { Uri = candidateUri, Sha256 = match.Groups["hash"].Value }
            : null;
    }

    private static CatalogInstaller? SelectInstaller(IReadOnlyList<CatalogInstaller> installers, string architecture) =>
        installers
            .OrderByDescending(installer => installer.Architecture.Equals(architecture, StringComparison.OrdinalIgnoreCase) ? 100 :
                                            installer.Architecture.Equals("neutral", StringComparison.OrdinalIgnoreCase) ? 10 : 0)
            .FirstOrDefault(installer =>
                installer.Architecture.Equals(architecture, StringComparison.OrdinalIgnoreCase) ||
                installer.Architecture.Equals("neutral", StringComparison.OrdinalIgnoreCase));

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
            "Federated public catalogs",
            "neutral",
            "Vendor multi-language installer",
            DetectArchitecture(application.PrimaryInstallPath),
            "stable",
            $"https://github.com/microsoft/winget-pkgs/tree/master/manifests/{char.ToLowerInvariant(identity.Id[0])}/{string.Join('/', identity.Id.Split('.'))}",
            message ?? $"{identity.MatchKind} match to {identity.Id}; {source} reports the current version.",
            plan);

    private UpdateCheckResult Error(InstalledApplication application, string message) => new(
        application.Identity, application.DisplayName, application.NormalizedVersion, null, UpdateStatus.Error,
        Id, "Federated public catalogs", "neutral", "Vendor multi-language installer", "unknown", "unknown", null, message, null);

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
            if (key.EndsWith("_is1", StringComparison.OrdinalIgnoreCase) || ProductCodeRegex().IsMatch(key))
            {
                return key;
            }
        }
        return null;
    }

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

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafePartRegex();

    [GeneratedRegex(@"(?:\s+|\s*[-(]\s*)v?\d+(?:\.\d+)+(?:[^)]*)?\)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionSuffixRegex();

    [GeneratedRegex(@"\s*\((?:x64|x86|arm64|64-bit|32-bit)[^)]*\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArchitectureSuffixRegex();

    private sealed record CatalogIdentity(string Id, string Name, string Moniker, string LatestVersion, string MatchKind);
    private sealed record ScoopCandidate(string Version, string? Homepage);
    private sealed record CatalogInstaller(
        string Architecture,
        UpdateExecutionKind Kind,
        Uri Uri,
        string Sha256,
        IReadOnlyList<string> Arguments,
        string? NestedInstallerRelativePath = null);
}
