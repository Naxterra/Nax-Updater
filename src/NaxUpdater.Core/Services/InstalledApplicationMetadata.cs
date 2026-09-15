using NaxUpdater.Core.Internal;
using NaxUpdater.Core.Models;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace NaxUpdater.Core.Services;

internal static class InstalledApplicationMetadata
{
    public static string? Executable(InstalledApplication application)
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(application.PrimaryInstallPath)) paths.Add(application.PrimaryInstallPath);
        paths.AddRange(application.Evidence.Where(static e => e.Verified &&
            e.Label is "Resolved application executable" or "Display icon path" or "Install location").Select(static e => e.Value));
        // Some installers (e.g. GitHub CLI's MSI) register an uninstall entry
        // without InstallLocation or DisplayIcon, leaving no path evidence at
        // all. Fall back to the conventional per-scope install directory named
        // after the display name before giving up.
        paths.AddRange(ConventionalInstallDirectories(application).Where(Directory.Exists));
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(path) && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(path);
            var root = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (root is null || root.Equals(Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var directory in new[] { root, Path.Combine(root, "ui"), Path.Combine(root, "bin") })
            {
                var candidate = NativePathParser.FindLikelyExecutable(directory, application.DisplayName);
                if (candidate is null) continue;
                var metadata = FileVersionInfo.GetVersionInfo(candidate);
                if (ExecutableMetadataEnricher.IsApplicationExecutable(candidate, application.DisplayName, metadata.ProductName))
                    return Path.GetFullPath(candidate);
            }
        }
        return null;
    }

    private static IEnumerable<string> ConventionalInstallDirectories(InstalledApplication application)
    {
        if (string.IsNullOrWhiteSpace(application.DisplayName)) yield break;
        if (application.Scope == InstallScope.Machine)
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), application.DisplayName);
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), application.DisplayName);
        }
        else
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", application.DisplayName);
        }
    }

    public static string? Architecture(InstalledApplication application)
    {
        var explicitArchitecture = Regex.Match(application.DisplayName, @"\((?<arch>x64|x86|arm64|64-bit|32-bit)(?:[ )])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (explicitArchitecture.Success)
            return explicitArchitecture.Groups["arch"].Value.ToLowerInvariant() switch
            {
                "64-bit" => "x64",
                "32-bit" => "x86",
                var value => value
            };
        return NodeJsUpdateProvider.DetectArchitecture(Executable(application));
    }

    public static string? InstallDirectory(InstalledApplication application)
    {
        var reported = application.Evidence.FirstOrDefault(static evidence =>
            evidence.Verified && evidence.Label == "Install location" && System.IO.Directory.Exists(evidence.Value))?.Value;
        var directory = reported ?? Path.GetDirectoryName(Executable(application));
        if (string.IsNullOrWhiteSpace(directory)) return null;
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (full.Equals(windows, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            full.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase)) return null;
        return full;
    }
}
