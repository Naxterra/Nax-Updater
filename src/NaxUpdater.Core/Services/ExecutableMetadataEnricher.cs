using NaxUpdater.Core.Internal;
using NaxUpdater.Core.Models;
using System.Diagnostics;

namespace NaxUpdater.Core.Services;

internal static class ExecutableMetadataEnricher
{
    public static InstalledApplication Finalize(ApplicationCandidate candidate)
    {
        ResolveDirectoryCandidates(candidate);
        var primaryPath = candidate.Paths
            .OrderByDescending(static path => path.Verified)
            .ThenByDescending(static path => path.Priority)
            .Select(static path => path.Path)
            .FirstOrDefault();
        var pathSource = candidate.Paths
            .OrderByDescending(static path => path.Verified)
            .ThenByDescending(static path => path.Priority)
            .FirstOrDefault()?.Source;

        if (!string.IsNullOrWhiteSpace(primaryPath) && File.Exists(primaryPath))
        {
            TryReadExecutableMetadata(candidate, primaryPath);
        }
        TryResolveInstallOrUpdateDate(candidate, primaryPath);

        var registeredNameVersion = RegisteredProductVersion(candidate.DisplayName, candidate.Publisher);
        var (version, versionSource) = SelectVersion(
            candidate.ProviderVersion,
            registeredNameVersion,
            candidate.ExecutableVersion,
            candidate.RegistryVersion);
        var normalizedVersion = VersionNormalizer.Normalize(version, candidate.Policy?.VersionNormalization);
        var confidence = DetermineConfidence(candidate, primaryPath, version);

        return new InstalledApplication(
            candidate.Identity,
            candidate.DisplayName,
            candidate.Publisher,
            version,
            normalizedVersion,
            versionSource,
            primaryPath,
            pathSource,
            candidate.InstalledOn,
            candidate.InstallDateSource,
            candidate.Scope,
            candidate.ManagementMode,
            confidence,
            candidate.IsSystemComponent,
            candidate.BlockedProviders.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            candidate.RemovalPlan,
            candidate.Evidence.ToArray());
    }

    private static void ResolveDirectoryCandidates(ApplicationCandidate candidate)
    {
        foreach (var path in candidate.Paths.ToArray())
        {
            if (!Directory.Exists(path.Path))
            {
                continue;
            }

            var executable = NativePathParser.FindLikelyExecutable(path.Path, candidate.DisplayName);
            if (executable is null)
            {
                continue;
            }
            candidate.Paths.Add(new PathCandidate(executable, $"{path.Source} executable", path.Priority + 5, true));
            candidate.Evidence.Add(new ApplicationEvidence(
                EvidenceKind.FileSystem,
                "Resolved application executable",
                executable,
                true));
        }
    }

    private static void TryReadExecutableMetadata(ApplicationCandidate candidate, string path)
    {
        try
        {
            var information = FileVersionInfo.GetVersionInfo(path);
            var version = FirstValue(information.ProductVersion, information.FileVersion);
            if (!string.IsNullOrWhiteSpace(version))
            {
                candidate.Evidence.Add(new ApplicationEvidence(
                    EvidenceKind.Executable,
                    "Executable version",
                    version,
                    true));
                if (IsApplicationExecutable(path, candidate.DisplayName, information.ProductName))
                {
                    candidate.ExecutableVersion = version;
                }
                else
                {
                    candidate.Evidence.Add(new ApplicationEvidence(
                        EvidenceKind.Executable,
                        "Executable version ignored",
                        $"{version} · metadata belongs to an installer, uninstaller, component, or unrelated executable",
                        true));
                }
            }
            if (string.IsNullOrWhiteSpace(candidate.Publisher) && !string.IsNullOrWhiteSpace(information.CompanyName))
            {
                candidate.Publisher = information.CompanyName.Trim();
            }
            if (!string.IsNullOrWhiteSpace(information.ProductName))
            {
                candidate.Evidence.Add(new ApplicationEvidence(
                    EvidenceKind.Executable,
                    "Executable product",
                    information.ProductName.Trim(),
                    true));
            }
        }
        catch
        {
            // A locked or unusual executable remains useful as path evidence.
        }
    }

    internal static bool IsApplicationExecutable(string path, string displayName, string? productName)
    {
        if (!Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            path.Contains($"{Path.DirectorySeparatorChar}Package Cache{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fileName = Path.GetFileNameWithoutExtension(path);
        string[] helperMarkers =
        [
            "uninstall", "unins", "installer", "bootstrap", "setup", "prounstl", "kminst", "autoinstall"
        ];
        if (helperMarkers.Any(marker => fileName.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var normalizedDisplay = NativePathParser.NormalizeName(displayName);
        var normalizedProduct = NativePathParser.NormalizeName(productName ?? string.Empty);
        var normalizedFile = NativePathParser.NormalizeName(fileName);
        return Correlates(normalizedDisplay, normalizedProduct) || Correlates(normalizedDisplay, normalizedFile);

        static bool Correlates(string display, string candidate) =>
            candidate.Length >= 3 &&
            (display.Contains(candidate, StringComparison.Ordinal) || candidate.Contains(display, StringComparison.Ordinal));
    }

    private static void TryResolveInstallOrUpdateDate(ApplicationCandidate candidate, string? primaryPath)
    {
        if (candidate.InstalledOn.HasValue || string.IsNullOrWhiteSpace(primaryPath))
        {
            return;
        }

        try
        {
            var directory = Directory.Exists(primaryPath)
                ? primaryPath
                : File.Exists(primaryPath)
                    ? Path.GetDirectoryName(primaryPath)
                    : null;
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            var modified = new DateTimeOffset(Directory.GetLastWriteTime(directory));
            if (modified.Year < 2000 || modified > DateTimeOffset.Now.AddDays(1))
            {
                return;
            }

            candidate.InstalledOn = modified;
            candidate.InstallDateSource = "Installation folder modified date";
            candidate.Evidence.Add(new ApplicationEvidence(
                EvidenceKind.FileSystem,
                "Install or update date fallback",
                modified.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Date fallback is optional and must never make inventory scanning fail.
        }
    }

    private static ConfidenceLevel DetermineConfidence(ApplicationCandidate candidate, string? path, string? version)
    {
        var hasVerifiedPath = !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path));
        var hasVersion = !string.IsNullOrWhiteSpace(version);
        var hasStrongProvider = candidate.ManagementMode is ManagementMode.ZeroInstall or ManagementMode.Msix;
        if ((hasVerifiedPath && hasVersion) || (hasStrongProvider && hasVersion))
        {
            return ConfidenceLevel.High;
        }
        return hasVerifiedPath || hasVersion ? ConfidenceLevel.Medium : ConfidenceLevel.Low;
    }

    internal static (string? Version, string? Source) SelectVersion(
        string? providerVersion,
        string? executableVersion,
        string? registryVersion) => SelectVersion(providerVersion, null, executableVersion, registryVersion);

    internal static (string? Version, string? Source) SelectVersion(
        string? providerVersion,
        string? registeredNameVersion,
        string? executableVersion,
        string? registryVersion)
    {
        if (IsMeaningfulVersion(providerVersion))
        {
            return (providerVersion!.Trim(), "Native provider");
        }
        if (IsMeaningfulVersion(registeredNameVersion))
        {
            return (registeredNameVersion!.Trim(), "Uninstall registry");
        }
        var executableIsMeaningful = IsMeaningfulVersion(executableVersion);
        var registryIsMeaningful = IsMeaningfulVersion(registryVersion);
        if (executableIsMeaningful && registryIsMeaningful)
        {
            return VersionOrder.Compare(executableVersion, registryVersion) >= 0
                ? (executableVersion!.Trim(), "Executable metadata")
                : (registryVersion!.Trim(), "Uninstall registry");
        }
        if (executableIsMeaningful)
        {
            return (executableVersion!.Trim(), "Executable metadata");
        }
        if (registryIsMeaningful)
        {
            return (registryVersion!.Trim(), "Uninstall registry");
        }
        return (null, null);
    }

    internal static string? RegisteredProductVersion(string displayName, string? publisher)
    {
        if (publisher?.Contains("Python Software Foundation", StringComparison.OrdinalIgnoreCase) == true)
        {
            var python = System.Text.RegularExpressions.Regex.Match(
                displayName,
                @"^Python\s+(?<version>\d+\.\d+\.\d+)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (python.Success)
            {
                return python.Groups["version"].Value;
            }
        }

        if (publisher?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true)
        {
            var sdk = System.Text.RegularExpressions.Regex.Match(
                displayName,
                @"^Microsoft\s+\.NET\s+SDK\s+(?<version>\d+\.\d+\.\d+(?:-[^\s(]+)?)\s+\(",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (sdk.Success)
            {
                return sdk.Groups["version"].Value;
            }

            var desktopRuntime = System.Text.RegularExpressions.Regex.Match(
                displayName,
                @"^Microsoft\s+Windows\s+Desktop\s+Runtime\s+-\s+(?<version>\d+\.\d+\.\d+)\s+\(",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (desktopRuntime.Success)
            {
                return desktopRuntime.Groups["version"].Value;
            }
        }
        return null;
    }

    internal static bool IsMeaningfulVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Trim().Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var numericParts = System.Text.RegularExpressions.Regex.Matches(value, @"\d+");
        return numericParts.Count > 0 && numericParts.Any(static part =>
            !part.Value.All(static character => character == '0'));
    }

    private static string? FirstValue(params string?[] values) => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
