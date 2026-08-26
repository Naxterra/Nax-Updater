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

        var (version, versionSource) = !string.IsNullOrWhiteSpace(candidate.ProviderVersion)
            ? (candidate.ProviderVersion, "Native provider")
            : !string.IsNullOrWhiteSpace(candidate.ExecutableVersion)
                ? (candidate.ExecutableVersion, "Executable metadata")
                : (candidate.RegistryVersion, string.IsNullOrWhiteSpace(candidate.RegistryVersion) ? null : "Uninstall registry");
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
                candidate.ExecutableVersion = version;
                candidate.Evidence.Add(new ApplicationEvidence(
                    EvidenceKind.Executable,
                    "Executable version",
                    version,
                    true));
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

    private static string? FirstValue(params string?[] values) => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
