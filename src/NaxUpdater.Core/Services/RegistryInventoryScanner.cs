using Microsoft.Win32;
using NaxUpdater.Core.Internal;
using NaxUpdater.Core.Models;
using System.Globalization;

namespace NaxUpdater.Core.Services;

internal sealed partial class RegistryInventoryScanner
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string InstallerUpgradeCodesPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UpgradeCodes";
    internal const string InstallerUpgradeFamilyEvidenceLabel = "Windows Installer upgrade family";

    public IReadOnlyList<ApplicationCandidate> Scan(ICollection<InventoryIssue> issues)
    {
        var results = new List<ApplicationCandidate>();
        var installerUpgradeFamilies = ReadInstallerUpgradeFamilies(issues);
        ScanHive(RegistryHive.LocalMachine, RegistryView.Registry64, InstallScope.Machine, installerUpgradeFamilies, results, issues);
        ScanHive(RegistryHive.LocalMachine, RegistryView.Registry32, InstallScope.Machine, installerUpgradeFamilies, results, issues);
        ScanHive(RegistryHive.CurrentUser, RegistryView.Registry64, InstallScope.CurrentUser, installerUpgradeFamilies, results, issues);
        ScanHive(RegistryHive.CurrentUser, RegistryView.Registry32, InstallScope.CurrentUser, installerUpgradeFamilies, results, issues);
        return results;
    }

    private static void ScanHive(
        RegistryHive hive,
        RegistryView view,
        InstallScope scope,
        IReadOnlyDictionary<string, string> installerUpgradeFamilies,
        ICollection<ApplicationCandidate> results,
        ICollection<InventoryIssue> issues)
    {
        var sourceName = $"{hive} {view}";
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = baseKey.OpenSubKey(UninstallPath);
            if (uninstallKey is null)
            {
                return;
            }

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                try
                {
                    using var subKey = uninstallKey.OpenSubKey(subKeyName);
                    if (subKey is null || GetInt32(subKey, "SystemComponent") == 1)
                    {
                        continue;
                    }

                    var displayName = GetString(subKey, "DisplayName");
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        continue;
                    }

                    var releaseType = GetString(subKey, "ReleaseType");
                    if (releaseType is not null &&
                        (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
                         releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var publisher = GetString(subKey, "Publisher");
                    var version = GetString(subKey, "DisplayVersion");
                    var installLocation = CleanPath(GetString(subKey, "InstallLocation"));
                    var displayIcon = NativePathParser.FromDisplayIcon(GetString(subKey, "DisplayIcon"));
                    var uninstallString = GetString(subKey, "UninstallString");
                    var installDateText = GetString(subKey, "InstallDate");
                    var installedOn = ParseInstallDate(installDateText);
                    var isWindowsInstaller = GetInt32(subKey, "WindowsInstaller") == 1 ||
                                             uninstallString?.Contains("msiexec", StringComparison.OrdinalIgnoreCase) == true;
                    var removalPlan = CreateRemovalPlan(
                        subKeyName,
                        uninstallString,
                        isWindowsInstaller,
                        scope,
                        GetInt32(subKey, "NoRemove") == 1);

                    var candidate = new ApplicationCandidate
                    {
                        Identity = $"registry:{hive}:{view}:{subKeyName}",
                        DisplayName = displayName,
                        Publisher = publisher,
                        RegistryVersion = version,
                        UninstallString = uninstallString,
                        InstalledOn = installedOn,
                        InstallDateSource = installedOn.HasValue ? "Uninstall registry" : null,
                        Scope = scope,
                        ManagementMode = isWindowsInstaller ? ManagementMode.WindowsInstaller : ManagementMode.Registry,
                        RemovalPlan = removalPlan
                    };
                    candidate.Evidence.Add(new ApplicationEvidence(
                        EvidenceKind.Registry,
                        "Uninstall registry",
                        $"{sourceName} · {subKeyName}",
                        true));
                    if (!string.IsNullOrWhiteSpace(version))
                    {
                        candidate.Evidence.Add(new ApplicationEvidence(
                            EvidenceKind.Registry,
                            "Registry version",
                            version,
                            true));
                    }
                    if (!string.IsNullOrWhiteSpace(installLocation))
                    {
                        var verified = Directory.Exists(installLocation) || File.Exists(installLocation);
                        candidate.Paths.Add(new PathCandidate(installLocation, "Registry install location", 50, verified));
                        candidate.Evidence.Add(new ApplicationEvidence(
                            EvidenceKind.Registry,
                            "Install location",
                            installLocation,
                            verified));
                    }
                    if (!string.IsNullOrWhiteSpace(displayIcon))
                    {
                        var verified = File.Exists(displayIcon);
                        candidate.Paths.Add(new PathCandidate(displayIcon, "Registry display icon", 100, verified));
                        candidate.Evidence.Add(new ApplicationEvidence(
                            EvidenceKind.Registry,
                            "Display icon path",
                            displayIcon,
                            verified));
                    }
                    if (!string.IsNullOrWhiteSpace(uninstallString))
                    {
                        candidate.Evidence.Add(new ApplicationEvidence(
                            EvidenceKind.Registry,
                            "Uninstall command",
                            uninstallString));
                    }
                    if (!string.IsNullOrWhiteSpace(installDateText))
                    {
                        candidate.Evidence.Add(new ApplicationEvidence(
                            EvidenceKind.Registry,
                            "Install date",
                            installedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? installDateText,
                            installedOn.HasValue));
                    }
                    if (isWindowsInstaller)
                    {
                        candidate.Evidence.Add(new ApplicationEvidence(
                            EvidenceKind.Registry,
                            "Installer technology",
                            "Windows Installer (MSI)",
                            true));
                        if (TryPackGuid(subKeyName, out var packedProductCode) &&
                            installerUpgradeFamilies.TryGetValue(packedProductCode, out var upgradeFamily))
                        {
                            candidate.Evidence.Add(new ApplicationEvidence(
                                EvidenceKind.Registry,
                                InstallerUpgradeFamilyEvidenceLabel,
                                upgradeFamily,
                                true));
                        }
                    }
                    if (removalPlan is not null)
                    {
                        candidate.Evidence.Add(new ApplicationEvidence(
                            EvidenceKind.Registry,
                            "Removal method",
                            removalPlan.Kind.ToString(),
                            true));
                    }
                    results.Add(candidate);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    issues.Add(new InventoryIssue(sourceName, $"Could not read uninstall entry {subKeyName}: {exception.Message}", exception.GetType().Name));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            issues.Add(new InventoryIssue(sourceName, exception.Message, exception.GetType().Name));
        }
    }

    private static IReadOnlyDictionary<string, string> ReadInstallerUpgradeFamilies(ICollection<InventoryIssue> issues)
    {
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var upgradeCodes = baseKey.OpenSubKey(InstallerUpgradeCodesPath);
                if (upgradeCodes is null)
                {
                    continue;
                }
                foreach (var packedUpgradeCode in upgradeCodes.GetSubKeyNames())
                {
                    using var familyKey = upgradeCodes.OpenSubKey(packedUpgradeCode);
                    if (familyKey is null)
                    {
                        continue;
                    }
                    var family = TryUnpackGuid(packedUpgradeCode, out var upgradeCode)
                        ? upgradeCode
                        : packedUpgradeCode;
                    foreach (var packedProductCode in familyKey.GetValueNames())
                    {
                        results.TryAdd(packedProductCode, family);
                    }
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                issues.Add(new InventoryIssue($"Windows Installer upgrade families {view}", exception.Message, exception.GetType().Name));
            }
        }
        return results;
    }

    private static bool TryPackGuid(string value, out string packed)
    {
        packed = string.Empty;
        if (!Guid.TryParse(value, out var guid))
        {
            return false;
        }
        packed = TransformInstallerGuid(guid.ToString("N").ToUpperInvariant());
        return true;
    }

    private static bool TryUnpackGuid(string value, out string unpacked)
    {
        unpacked = string.Empty;
        if (value.Length != 32 || value.Any(static character => !Uri.IsHexDigit(character)))
        {
            return false;
        }
        var hex = TransformInstallerGuid(value);
        if (!Guid.TryParseExact(hex, "N", out var guid))
        {
            return false;
        }
        unpacked = guid.ToString("B").ToUpperInvariant();
        return true;
    }

    private static string TransformInstallerGuid(string value)
    {
        static string Reverse(string text) => string.Concat(text.Reverse());
        var tail = string.Concat(Enumerable.Range(0, 8).Select(index =>
        {
            var offset = 16 + (index * 2);
            return $"{value[offset + 1]}{value[offset]}";
        }));
        return Reverse(value[..8]) + Reverse(value[8..12]) + Reverse(value[12..16]) + tail;
    }

    private static string? GetString(RegistryKey key, string name) => key.GetValue(name)?.ToString()?.Trim() is { Length: > 0 } value ? value : null;

    private static int? GetInt32(RegistryKey key, string name)
    {
        var value = key.GetValue(name);
        return value switch
        {
            int integer => integer,
            string text when int.TryParse(text, out var integer) => integer,
            _ => null
        };
    }

    private static string? CleanPath(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : Environment.ExpandEnvironmentVariables(value.Trim().Trim('"').TrimEnd('\\'));

    private static DateTimeOffset? ParseInstallDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !DateTime.TryParseExact(value.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return null;
        }
        return new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date));
    }

    private static RemovalPlan? CreateRemovalPlan(
        string subKeyName,
        string? uninstallString,
        bool isWindowsInstaller,
        InstallScope scope,
        bool noRemove)
    {
        if (noRemove || string.IsNullOrWhiteSpace(uninstallString))
        {
            return null;
        }

        if (isWindowsInstaller)
        {
            var productCodeMatch = ProductCodeRegex().Match(subKeyName);
            if (!productCodeMatch.Success)
            {
                productCodeMatch = ProductCodeRegex().Match(uninstallString);
            }
            if (productCodeMatch.Success)
            {
                return new RemovalPlan(
                    RemovalKind.WindowsInstaller,
                    Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
                    $"/x {productCodeMatch.Value}",
                    null,
                    scope == InstallScope.Machine);
            }
        }

        var parsed = NativePathParser.SplitExecutableAndArguments(uninstallString);
        if (string.IsNullOrWhiteSpace(parsed.Executable) || !File.Exists(parsed.Executable))
        {
            return null;
        }
        var kind = parsed.Executable.Contains("0install", StringComparison.OrdinalIgnoreCase) &&
                   parsed.Arguments.Contains("remove", StringComparison.OrdinalIgnoreCase)
            ? RemovalKind.ZeroInstall
            : RemovalKind.RegisteredUninstaller;
        return new RemovalPlan(
            kind,
            parsed.Executable,
            parsed.Arguments,
            null,
            scope == InstallScope.Machine);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ProductCodeRegex();
}
