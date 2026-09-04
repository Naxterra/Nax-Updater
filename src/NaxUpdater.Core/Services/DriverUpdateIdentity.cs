using NaxUpdater.Core.Models;
using System.Security.Cryptography;
using System.Text;

namespace NaxUpdater.Core.Services;

public static class DriverUpdateIdentity
{
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
