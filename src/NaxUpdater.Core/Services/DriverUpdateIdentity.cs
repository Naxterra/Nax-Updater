using NaxUpdater.Core.Models;
using System.Security.Cryptography;
using System.Text;

namespace NaxUpdater.Core.Services;

public static class DriverUpdateIdentity
{
    public const string MsiAudioProviderId = "manufacturer-driver:msi-realtek-audio";
    internal const string MsiAudioHardwareId = @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0B00&SUBSYS_1462EC80";

    internal static bool IsMsiAudioDevice(InstalledHardwareDriver driver) => driver.IsPresent &&
        driver.DeviceClass.Equals("MEDIA", StringComparison.OrdinalIgnoreCase) &&
        driver.HardwareId?.Split(['\r', '\n', '\0'], StringSplitOptions.RemoveEmptyEntries)
            .Any(id => id.Equals(MsiAudioHardwareId, StringComparison.OrdinalIgnoreCase) ||
                       id.StartsWith(MsiAudioHardwareId + "&REV_", StringComparison.OrdinalIgnoreCase)) == true;

    public static bool HasReachedTarget(InstalledHardwareDriver driver, string? targetVersion, string providerId)
    {
        if (!driver.IsPresent || string.IsNullOrWhiteSpace(targetVersion)) return false;
        if (providerId == MsiAudioProviderId)
            return IsMsiAudioDevice(driver) && driver.Provider.Equals("Realtek Semiconductor Corp.", StringComparison.OrdinalIgnoreCase) &&
                   VersionOrder.Compare(driver.InstalledVersion, targetVersion) == 0;
        return VersionOrder.Compare(InstalledReleaseVersion(driver), targetVersion) >= 0;
    }

    public static UpdateCheckResult ObservedState(UpdateCheckResult previous, InstalledHardwareDriver driver, string message)
    {
        var reached = HasReachedTarget(driver, previous.AvailableVersion, previous.ProviderId);
        return previous with
        {
            ApplicationIdentity = driver.Identity,
            DisplayName = driver.DeviceName,
            InstalledVersion = InstalledReleaseVersion(driver),
            AvailableVersion = null,
            Status = reached ? UpdateStatus.Current : UpdateStatus.Error,
            ExecutionPlan = null,
            Applicability = reached ? UpdateApplicability.NotRequired : UpdateApplicability.Unknown,
            Message = message,
            CorrelationKey = previous.CorrelationKey ?? $"driver:{driver.Identity}"
        };
    }

    internal static string ForGroup(string packageKey, IReadOnlyList<InstalledHardwareDriver> members)
    {
        // Named suites already have fixed grouping identities. Default groups must
        // identify the physical devices, not the replaceable INF or driver version.
        if (!packageKey.Contains('|')) return "driver-package:" + packageKey;
        var devices = members.Where(static driver => driver.IsPresent && driver.Identity.StartsWith("pnp:", StringComparison.OrdinalIgnoreCase))
            .Select(static driver => driver.Identity.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return devices.Length == 0 ? "driver-package:" + packageKey :
            "driver-device:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", devices))));
    }

    public static string InstalledReleaseVersion(InstalledHardwareDriver driver) =>
        driver.DeviceClass.Equals("Display", StringComparison.OrdinalIgnoreCase) &&
        driver.Provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            ? ManufacturerDriverService.NormalizeNvidiaVersion(driver.InstalledVersion)
            : driver.InstalledVersion;

    public static InstalledHardwareDriver? Find(
        IEnumerable<InstalledHardwareDriver> drivers, string identity, string? correlation,
        string displayName, string providerId)
    {
        var present = drivers.Where(static driver => driver.IsPresent).ToArray();
        var exact = present.Where(driver => driver.Identity.Equals(identity, StringComparison.Ordinal) ||
            ("driver:" + driver.Identity).Equals(correlation, StringComparison.Ordinal)).ToArray();
        if (exact.Length == 1) return exact[0];
        if (exact.Length > 1 || !identity.StartsWith("driver-package:", StringComparison.Ordinal) || !identity.Contains('|'))
            return null;

        // Migration for journals written before stable device IDs existed. Only the
        // existing automatic NVIDIA/Intel providers and one unambiguous live device
        // can use this path; retained driver-store registrations are never proof.
        var legacyProvider = identity["driver-package:".Length..].Split('|')[0];
        var requiredClass = providerId switch
        {
            "manufacturer-driver:nvidia" => "Display",
            "manufacturer-driver:intel-i219" => "Net",
            _ => null
        };
        if (requiredClass is null) return null;
        var matches = present.Where(driver =>
            driver.DeviceCount == 1 &&
            driver.Provider.Equals(legacyProvider, StringComparison.OrdinalIgnoreCase) &&
            driver.DeviceClass.Equals(requiredClass, StringComparison.OrdinalIgnoreCase) &&
            driver.DeviceName.Equals(displayName, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
