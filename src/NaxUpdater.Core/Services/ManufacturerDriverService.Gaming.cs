using System.Text.Json;
using System.Text.RegularExpressions;
using NaxUpdater.Core.Models;
using Windows.Management.Deployment;

namespace NaxUpdater.Core.Services;

public sealed partial class ManufacturerDriverService
{
    private static readonly Uri GamingServicesSupport = new("https://apps.microsoft.com/detail/9MWPM2CQNLHN");
    private static readonly Uri XboxServiceSupport = new("https://support.xbox.com/help/hardware-network/accessories/controller-troubleshoot");

    internal static string? InfDriverVersion(string text, string hardwareId)
    {
        if (!text.Contains(hardwareId, StringComparison.OrdinalIgnoreCase)) return null;
        var version = Regex.Match(text, @"^\s*DriverVer\s*=\s*[^,\r\n]+,\s*(?<version>\d+(?:\.\d+){3})\s*(?:;.*)?$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return version.Success ? version.Groups["version"].Value : null;
    }

    internal async Task<ManufacturerDriverResult> CheckXvddAsync(InstalledHardwareDriver driver, CancellationToken token)
    {
        try
        {
            const string family = "Microsoft.GamingServices_8wekyb3d8bbwe";
            var package = new PackageManager().FindPackagesForUser("")
                .Where(p => p.Id.FamilyName == family && !p.IsResourcePackage)
                .OrderByDescending(p => p.Id.Version.Major).FirstOrDefault()
                ?? throw new InvalidDataException("The registered Microsoft Gaming Services owner package was not found.");
            var inf = Path.Combine(package.InstalledLocation.Path, "drivers", "xvdd.inf");
            if (!File.Exists(inf) || new FileInfo(inf).Length > 1024 * 1024) throw new InvalidDataException("The owner package's exact Xvdd INF was not found.");
            var bundled = InfDriverVersion(await File.ReadAllTextAsync(inf, token), "XvddRootDevice")
                ?? throw new InvalidDataException("The Gaming Services INF did not identify the exact Xvdd hardware.");
            var catalog = await GetStringAsync(new Uri("https://displaycatalog.mp.microsoft.com/v7.0/products/9MWPM2CQNLHN?fieldsTemplate=InstallAgent&market=DE&languages=en-US"), token);
            using var json = JsonDocument.Parse(catalog);
            var published = MicrosoftStoreProductMetadataClient.ParsePublishedPackage(json.RootElement, "9MWPM2CQNLHN", family,
                package.Id.Architecture.ToString(), null)
                ?? throw new InvalidDataException("Microsoft's catalog did not identify the Gaming Services package family and architecture.");
            return new(driver, ManufacturerDriverStatus.VendorSoftwareManaged, published.Version, "Microsoft Gaming Services", GamingServicesSupport,
                $"Microsoft's live catalog lists Gaming Services package {published.Version}. The installed owner package contains Xvdd INF {bundled}; the loaded driver is {driver.InstalledVersion}. Store eligibility and servicing are controlled by Microsoft; the package version is not a driver version.",
                null, ManufacturerCheckEvidence.LiveCatalog);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { return Error(driver, "Microsoft Gaming Services", GamingServicesSupport, exception.Message); }
    }

    internal static ManufacturerDriverResult CheckXboxSyntheticDriver(InstalledHardwareDriver driver)
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "XboxService", "x64");
            var inf = Path.Combine(root, "SynthGpDriver", "synthgpdriver.inf");
            var executable = Path.Combine(root, "XboxService.exe");
            if (!File.Exists(inf) || !File.Exists(executable) || new FileInfo(inf).Length > 1024 * 1024)
                throw new InvalidDataException("The Xbox Service synthetic-controller owner payload was not found.");
            var bundled = InfDriverVersion(File.ReadAllText(inf), "SYNTHETICGamepad")
                ?? throw new InvalidDataException("Xbox Service's bundled INF did not identify the synthetic controller.");
            var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(executable).ProductVersion ?? "—";
            return new(driver, ManufacturerDriverStatus.VendorSoftwareManaged, version, "Xbox Service", XboxServiceSupport,
                $"The installed Xbox Service {version} owns the bundled synthetic-controller INF {bundled}; the loaded driver is {driver.InstalledVersion}. No public independent driver catalog is available, so no latest-version claim is made.",
                null, ManufacturerCheckEvidence.SupportGuidance);
        }
        catch (Exception exception) { return Error(driver, "Xbox Service", XboxServiceSupport, exception.Message); }
    }
}
