using NaxUpdater.Core.Models;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NaxUpdater.Core.Services;

public sealed partial class FirefoxMetadataDetector(string? firefoxDataRoot = null)
{
    private readonly string _firefoxDataRoot = firefoxDataRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Mozilla",
        "Firefox");

    public FirefoxInstallProfile Detect(InstalledApplication application)
    {
        var installDirectory = ResolveInstallDirectory(application.PrimaryInstallPath);
        var displayMatch = DisplayNameMetadataRegex().Match(application.DisplayName);
        var architecture = displayMatch.Success
            ? displayMatch.Groups["architecture"].Value.ToLowerInvariant()
            : DetectArchitectureFromPath(installDirectory);
        var packagedLanguage = displayMatch.Success
            ? displayMatch.Groups["language"].Value
            : "unknown";
        var channel = ReadChannel(installDirectory);
        if (application.InstalledVersion?.Contains("esr", StringComparison.OrdinalIgnoreCase) == true)
        {
            channel = "esr";
        }

        var profile = FindMatchingProfile(installDirectory);
        var requestedLocales = profile is null ? [] : ReadRequestedLocales(profile);
        IReadOnlySet<string> activeLanguagePacks = profile is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : ReadActiveLanguagePacks(profile);
        var effectiveLanguage = SelectEffectiveLanguage(requestedLocales, activeLanguagePacks, packagedLanguage);
        var languageSource = effectiveLanguage.Source;
        var warning = !packagedLanguage.Equals("unknown", StringComparison.OrdinalIgnoreCase) &&
                      !effectiveLanguage.Language.Equals(packagedLanguage, StringComparison.OrdinalIgnoreCase)
            ? $"The installed package reports {packagedLanguage}, but Firefox actively requests {effectiveLanguage.Language}. The {effectiveLanguage.Language} installer will be used."
            : null;

        return new FirefoxInstallProfile(
            installDirectory,
            architecture,
            packagedLanguage,
            effectiveLanguage.Language,
            languageSource,
            channel,
            profile,
            warning);
    }

    private string? FindMatchingProfile(string installDirectory)
    {
        if (!Directory.Exists(_firefoxDataRoot))
        {
            return null;
        }

        var candidatePaths = new List<string>();
        AddProfilePaths(Path.Combine(_firefoxDataRoot, "installs.ini"), "Default", candidatePaths);
        AddProfilePaths(Path.Combine(_firefoxDataRoot, "profiles.ini"), "Path", candidatePaths);
        foreach (var profile in candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var compatibilityPath = Path.Combine(profile, "compatibility.ini");
            if (!File.Exists(compatibilityPath))
            {
                continue;
            }
            try
            {
                var matchingDirectory = File.ReadLines(compatibilityPath)
                    .Where(static line => line.StartsWith("LastPlatformDir=", StringComparison.OrdinalIgnoreCase) ||
                                          line.StartsWith("LastAppDir=", StringComparison.OrdinalIgnoreCase))
                    .Select(static line => line[(line.IndexOf('=') + 1)..].Trim())
                    .Any(path => PathsEqual(path, installDirectory));
                if (matchingDirectory)
                {
                    return profile;
                }
            }
            catch
            {
                // Continue with the default-profile order.
            }
        }
        return candidatePaths.FirstOrDefault(Directory.Exists);
    }

    private void AddProfilePaths(string iniPath, string key, ICollection<string> target)
    {
        if (!File.Exists(iniPath))
        {
            return;
        }
        try
        {
            var prefix = key + "=";
            foreach (var line in File.ReadLines(iniPath).Where(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                var value = line[prefix.Length..].Trim().Replace('/', Path.DirectorySeparatorChar);
                var path = Path.IsPathRooted(value) ? value : Path.Combine(_firefoxDataRoot, value);
                if (Directory.Exists(path))
                {
                    target.Add(Path.GetFullPath(path));
                }
            }
        }
        catch
        {
            // Profile metadata is optional evidence.
        }
    }

    private static IReadOnlyList<string> ReadRequestedLocales(string profile)
    {
        var prefsPath = Path.Combine(profile, "prefs.js");
        if (!File.Exists(prefsPath))
        {
            return [];
        }
        try
        {
            var match = File.ReadLines(prefsPath)
                .Select(static line => LocalePreferenceRegex().Match(line))
                .FirstOrDefault(static candidate => candidate.Success);
            return match is null || !match.Success
                ? []
                : match.Groups["locales"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlySet<string> ReadActiveLanguagePacks(string profile)
    {
        var extensionsPath = Path.Combine(profile, "extensions.json");
        if (!File.Exists(extensionsPath))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(extensionsPath));
            var locales = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!document.RootElement.TryGetProperty("addons", out var addons))
            {
                return locales;
            }
            foreach (var addon in addons.EnumerateArray())
            {
                var type = addon.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
                var active = addon.TryGetProperty("active", out var activeValue) && activeValue.ValueKind == JsonValueKind.True;
                var id = addon.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                if (!active || !string.Equals(type, "locale", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }
                var match = LanguagePackIdRegex().Match(id);
                if (match.Success)
                {
                    locales.Add(match.Groups["locale"].Value);
                }
            }
            return locales;
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static (string Language, string Source) SelectEffectiveLanguage(
        IReadOnlyList<string> requestedLocales,
        IReadOnlySet<string> activeLanguagePacks,
        string packagedLanguage)
    {
        foreach (var requested in requestedLocales)
        {
            var matchingPack = activeLanguagePacks.FirstOrDefault(pack => LocaleMatches(pack, requested));
            if (!string.IsNullOrWhiteSpace(matchingPack))
            {
                return (matchingPack, "Active Firefox language pack in the default installation profile");
            }
            if (LocaleMatches(packagedLanguage, requested))
            {
                return (packagedLanguage, "Firefox default-profile locale preference");
            }
        }
        if (!string.IsNullOrWhiteSpace(packagedLanguage) && !packagedLanguage.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return (packagedLanguage, "Firefox installed-package locale");
        }
        return ("unknown", "Firefox language could not be verified");
    }

    private static string ReadChannel(string installDirectory)
    {
        var path = Path.Combine(installDirectory, "defaults", "pref", "channel-prefs.js");
        if (!File.Exists(path))
        {
            return "unknown";
        }
        try
        {
            var match = File.ReadLines(path)
                .Select(static line => ChannelRegex().Match(line))
                .FirstOrDefault(static candidate => candidate.Success);
            return match is null || !match.Success ? "unknown" : match.Groups["channel"].Value;
        }
        catch
        {
            return "unknown";
        }
    }

    private static string ResolveInstallDirectory(string? primaryPath)
    {
        if (string.IsNullOrWhiteSpace(primaryPath))
        {
            return string.Empty;
        }
        return File.Exists(primaryPath) ? Path.GetDirectoryName(primaryPath) ?? string.Empty : primaryPath;
    }

    private static string DetectArchitectureFromPath(string installDirectory)
    {
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86) && installDirectory.StartsWith(programFilesX86, StringComparison.OrdinalIgnoreCase))
        {
            return "x86";
        }
        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => "x86"
        };
    }

    private static bool LocaleMatches(string first, string second)
    {
        return first.Equals(second, StringComparison.OrdinalIgnoreCase) ||
               first.StartsWith(second + '-', StringComparison.OrdinalIgnoreCase) ||
               second.StartsWith(first + '-', StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [GeneratedRegex(@"^Mozilla Firefox \((?<architecture>x64|x86|arm64)\s+(?<language>[^)]+)\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DisplayNameMetadataRegex();

    [GeneratedRegex("user_pref\\(\"intl\\.locale\\.requested\",\\s*\"(?<locales>[^\"]*)\"\\);", RegexOptions.CultureInvariant)]
    private static partial Regex LocalePreferenceRegex();

    [GeneratedRegex(@"^langpack-(?<locale>[^@]+)@firefox\.mozilla\.org$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LanguagePackIdRegex();

    [GeneratedRegex("pref\\(\"app\\.update\\.channel\",\\s*\"(?<channel>[^\"]+)\"\\);", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelRegex();
}

public sealed record FirefoxInstallProfile(
    string InstallDirectory,
    string Architecture,
    string PackagedLanguage,
    string EffectiveLanguage,
    string LanguageSource,
    string Channel,
    string? ProfileDirectory,
    string? Warning);
