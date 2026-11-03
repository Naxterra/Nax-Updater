using System.Text;
using System.Xml.Linq;

namespace NaxUpdater.Core.Services;

// One driver package that Windows' driver store holds for a device, as pnputil lists it.
public sealed record DriverStorePackage(string InfName, string? OriginalName, string? Provider, string? Version, string? Date, string Status)
{
    public bool IsInstalled => Status.Contains("Installed", StringComparison.OrdinalIgnoreCase);
}

// What a device runs and what it could go back to. Nax-Updater only shows this evidence and the
// way back; it never rolls a driver back on its own, because a wrong storage driver can stop
// Windows from starting.
public sealed record DriverDeviceRecovery(
    string InstanceId,
    string? Description,
    string? ClassName,
    string? Status,
    int? ProblemCode,
    string? Problem,
    string? DriverName,
    IReadOnlyList<DriverStorePackage> MatchingDrivers)
{
    // CM_PROB_NEED_RESTART: the new driver is staged and Windows loads it at the next restart.
    public bool AwaitingRestart => ProblemCode == 14 || string.Equals(Problem, "CM_PROB_NEED_RESTART", StringComparison.OrdinalIgnoreCase);

    public DriverStorePackage? Installed => MatchingDrivers.FirstOrDefault(static driver => driver.IsInstalled) ??
        MatchingDrivers.FirstOrDefault(driver => string.Equals(driver.InfName, DriverName, StringComparison.OrdinalIgnoreCase));

    // Older packages of the same provider that are still in the driver store, newest first:
    // the versions the device can go back to without a download.
    public IReadOnlyList<DriverStorePackage> EarlierPackages()
    {
        var installed = Installed;
        if (installed?.Version is null) return [];
        return MatchingDrivers
            .Where(driver => !driver.IsInstalled && driver.Version is not null &&
                             string.Equals(driver.Provider, installed.Provider, StringComparison.OrdinalIgnoreCase) &&
                             VersionOrder.Compare(driver.Version, installed.Version) < 0)
            // A package that matches several hardware IDs of the device is listed once per ID.
            .DistinctBy(static driver => driver.InfName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static driver => driver.Version!, Comparer<string>.Create(VersionOrder.Compare))
            .ToArray();
    }
}

public static class DriverRecovery
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(20);

    // Storage controllers and disks: a wrong driver here can keep Windows from starting.
    public static bool IsBootCritical(string deviceClass) =>
        deviceClass.Equals("SCSIAdapter", StringComparison.OrdinalIgnoreCase) ||
        deviceClass.Equals("HDC", StringComparison.OrdinalIgnoreCase) ||
        deviceClass.Equals("DiskDrive", StringComparison.OrdinalIgnoreCase);

    // pnputil's XML output (Windows 11) is the same in every display language and needs no
    // administrator rights. Older Windows builds without "/format xml" report an error instead.
    public static async Task<(DriverDeviceRecovery? Details, string? Error)> ReadAsync(string instanceId, CancellationToken cancellationToken)
    {
        var pnputil = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
        var result = await new ProcessQueryRunner().RunAsync(pnputil,
            ["/enum-devices", "/instanceid", instanceId, "/drivers", "/format", "xml"], QueryTimeout, cancellationToken, Encoding.UTF8);
        if (result.ExitCode != 0)
            return (null, $"pnputil could not list the drivers of {instanceId} (exit code {result.ExitCode}).");
        try
        {
            var details = Parse(result.StandardOutput, instanceId);
            return details is null ? (null, $"pnputil did not list the device {instanceId}.") : (details, null);
        }
        catch (System.Xml.XmlException exception)
        {
            return (null, $"pnputil's list of drivers for {instanceId} could not be read: {exception.Message}");
        }
    }

    internal static DriverDeviceRecovery? Parse(string xml, string instanceId)
    {
        var device = XDocument.Parse(xml).Root?.Elements("Device")
            .FirstOrDefault(element => string.Equals((string?)element.Attribute("InstanceId"), instanceId, StringComparison.OrdinalIgnoreCase));
        if (device is null) return null;
        var problem = device.Element("ProblemCode");
        var drivers = device.Element("MatchingDrivers")?.Elements("DriverName").Select(static driver =>
        {
            // "05/07/2025 12.19.2.64": the date, then the version.
            var versionText = ((string?)driver.Element("DriverVersion"))?.Trim();
            var space = versionText?.LastIndexOf(' ') ?? -1;
            return new DriverStorePackage(
                (string?)driver.Attribute("DriverName") ?? "?",
                (string?)driver.Element("OriginalName"),
                (string?)driver.Element("ProviderName"),
                space < 0 ? versionText : versionText![(space + 1)..],
                space < 0 ? null : versionText![..space],
                (string?)driver.Element("Status") ?? string.Empty);
        }).ToArray() ?? [];
        return new DriverDeviceRecovery(
            (string?)device.Attribute("InstanceId") ?? instanceId,
            (string?)device.Element("DeviceDescription"),
            (string?)device.Element("ClassName"),
            (string?)device.Element("Status"),
            int.TryParse((string?)problem, out var code) ? code : null,
            (string?)problem?.Attribute("Problem"),
            (string?)device.Element("DriverName"),
            drivers);
    }
}
