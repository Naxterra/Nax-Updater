namespace NaxUpdater.Core.Services;

// Verified installers are cached under <cache>\<source>\<version>\ so an interrupted
// download can resume. Once the installed version is verified, the installer is never
// needed again (12 GiB had accumulated, 27 versions for the Electron feeds alone), so it
// is deleted together with its then empty folders. Driver packages stay, because an
// older driver installer can matter for a rollback.
internal static class UpdatePayloadCache
{
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
