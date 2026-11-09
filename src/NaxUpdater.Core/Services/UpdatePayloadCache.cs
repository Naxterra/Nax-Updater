using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

// Verified installers are cached under <cache>\<source>\<version>\ so an interrupted
// download can resume. Once the installed version is verified, the installer is never
// needed again (12 GiB had accumulated, 27 versions for the Electron feeds alone), so it
// is deleted together with its then empty folders. Driver packages stay, because an
// older driver installer can matter for a rollback.
public static class UpdatePayloadCache
{
    // Removes the cached installers that no current offer needs. The installer was deleted only
    // when its update was verified at once, so versions that were confirmed after a restart or by
    // a later scan, attempts that a newer release superseded and the segments of interrupted
    // downloads of old versions stayed for good. Driver packages are never removed.
    public static void Sweep(string cacheRoot, IEnumerable<UpdateCheckResult> currentOffers)
    {
        if (!Directory.Exists(cacheRoot)) return;
        var keep = currentOffers.Where(static offer => offer.ExecutionPlan is not null)
            .Select(static offer => Path.Combine(UpdatePackageDownloader.SanitizePathSegment(offer.ProviderId),
                UpdatePackageDownloader.SanitizePathSegment(offer.AvailableVersion ?? "unknown")))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in Directory.EnumerateDirectories(cacheRoot))
        {
            if (Path.GetFileName(provider).StartsWith("manufacturer-driver", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                // A junction or link in the cache points to a folder elsewhere. Its content is
                // not the cache's, so the sweep neither reads nor deletes through it.
                if (IsLink(provider)) continue;
                foreach (var version in Directory.EnumerateDirectories(provider))
                {
                    if (!IsLink(version) && !keep.Contains(Path.Combine(Path.GetFileName(provider), Path.GetFileName(version))))
                        Directory.Delete(version, recursive: true);
                }
                if (!Directory.EnumerateFileSystemEntries(provider).Any()) Directory.Delete(provider);
            }
            // A file that is in use is tried again after the next check.
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool IsLink(string directory) => File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint);

    public static void ReleaseCompleted(string cacheRoot, string installerPath, string providerId)
    {
        if (providerId.StartsWith("manufacturer-driver", StringComparison.OrdinalIgnoreCase)) return;
        var root = Path.GetFullPath(cacheRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var file = Path.GetFullPath(installerPath);
        if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            if (File.Exists(file)) File.Delete(file);
            for (var directory = Path.GetDirectoryName(file);
                 directory is not null && directory.Length >= root.Length && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any();
                 directory = Path.GetDirectoryName(directory))
                Directory.Delete(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
