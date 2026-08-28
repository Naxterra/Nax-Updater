using Microsoft.Win32;
using NaxUpdater.Core.Models;
using System.Globalization;
using System.Management;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NaxUpdater.Core.Services;

public sealed partial class ManufacturerDriverService(HttpClient httpClient)
{
    private const string DriverClassPath = @"SYSTEM\CurrentControlSet\Control\Class";
    private static readonly Uri NvidiaDriverHome = new("https://www.nvidia.com/Download/index.aspx");
    private static readonly Uri IntelDsaHome = new("https://www.intel.com/content/www/us/en/support/detect.html");
    private static readonly Uri IntelEthernetPack = new("https://www.intel.com/content/www/us/en/download/15084/intel-ethernet-adapter-complete-driver-pack.html");
    private static readonly Uri IntelEthernetReadme = new("https://downloadmirror.intel.com/923522/readme.txt");
    private static readonly Uri MsiBoardSupport = new("https://www.msi.com/Motherboard/MAG-Z490-TOMAHAWK/support");
    private static readonly Uri RazerSupport = new("https://mysupport.razer.com/app/answers/detail/a_id/1835");
    private static readonly Uri RealtekPcieCatalog = new("https://www.realtek.com/Download/List?cate_id=584");
    private static readonly Uri RealtekPcieApi = new("https://www.realtek.com/Download/ListAllDownloadItem?cate_id=584");
    private static readonly Uri TpLinkTbe400Uh = new("https://www.tp-link.com/de/support/download/archer-tbe400uh/v1/");
    private static readonly Uri DellAw3423DwDriver = new("https://www.dell.com/support/home/en-us/drivers/driversdetails?driverid=m46j9");
    private static readonly Uri DellAw3423DwSupport = new("https://www.dell.com/support/product-details/en-us/product/aw3423dw-monitor/drivers");
    private static readonly Uri WdExternalFirmwarePolicy = new("https://support-en.wd.com/app/answers/detailweb/a_id/50745");

    public async Task<ManufacturerDriverSnapshot> CheckAsync(CancellationToken cancellationToken = default)
    {
        var issues = new List<string>();
        var installed = await Task.Run(() => ReadInstalledDrivers(issues), cancellationToken);
        var results = new List<ManufacturerDriverResult>(installed.Count);
        foreach (var driver in installed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsNvidiaDisplayDriver(driver))
            {
                results.Add(await CheckNvidiaAsync(driver, cancellationToken));
            }
            else if (IsRealtek8125(driver))
            {
                results.Add(await CheckRealtekEthernetAsync(driver, cancellationToken));
            }
            else if (IsIntelI219(driver))
            {
                results.Add(await CheckIntelI219Async(driver, cancellationToken));
            }
            else if (IsTpLinkTbe400Uh(driver))
            {
                results.Add(await CheckTpLinkAsync(driver, cancellationToken));
            }
            else if (IsDellAw3423DwDriver(driver))
            {
                results.Add(CheckDellAw3423Dw(driver));
            }
            else if (IsWesternDigitalExternal(driver))
            {
                results.Add(NoWdDriverRequired(driver));
            }
            else
            {
                results.Add(ManufacturerManaged(driver));
            }
        }
        return new ManufacturerDriverSnapshot(
            DateTimeOffset.Now,
            results
                .OrderBy(static result => result.Status == ManufacturerDriverStatus.Available ? 0 : 1)
                .ThenBy(static result => result.Driver.DeviceName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            issues.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    internal async Task<ManufacturerDriverResult> CheckNvidiaAsync(
        InstalledHardwareDriver driver,
        CancellationToken cancellationToken)
    {
        var seriesId = NvidiaSeriesId(driver.DeviceName);
        if (seriesId is null)
        {
            return NoVerifiedCatalog(driver, "NVIDIA", NvidiaDriverHome,
                "The NVIDIA device is present, but its product series is not mapped to NVIDIA's official driver search.");
        }

        try
        {
            var searchUri = new Uri($"https://www.nvidia.com/Download/processFind.aspx?dtcid=1&lang=en-us&lid=1&osid=57&psid={seriesId}&whql=1");
            var html = await GetStringAsync(searchUri, cancellationToken);
            var match = NvidiaResultRegex().Match(html);
            if (!match.Success)
            {
                throw new InvalidDataException("NVIDIA's official search returned no WHQL Game Ready driver.");
            }

            var availableVersion = match.Groups["version"].Value;
            var resultId = match.Groups["id"].Value;
            var installedVersion = NormalizeNvidiaVersion(driver.InstalledVersion);
            var detailsUri = new Uri($"https://www.nvidia.com/en-us/drivers/details/{resultId}/");
            if (VersionOrder.Compare(availableVersion, installedVersion) <= 0)
            {
                return Current(driver with { InstalledVersion = installedVersion }, availableVersion, "NVIDIA", detailsUri,
                    "NVIDIA's official WHQL Game Ready catalog reports the installed driver as current.");
            }

            var fileName = $"{availableVersion}-desktop-win10-win11-64bit-international-dch-whql.exe";
            var downloadUri = new Uri($"https://us.download.nvidia.com/Windows/{availableVersion}/{fileName}");
            var hashText = await GetStringAsync(new Uri(downloadUri.AbsoluteUri + ".sha256"), cancellationToken);
            var hashMatch = Sha256Regex().Match(hashText);
            if (!hashMatch.Success || !hashText.Contains(fileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("NVIDIA's official SHA-256 sidecar did not identify the selected installer.");
            }

            var plan = new UpdateExecutionPlan(
                UpdateExecutionKind.DownloadedExe,
                downloadUri,
                fileName,
                hashMatch.Groups["hash"].Value,
                "NVIDIA Corporation",
                null,
                [],
                true,
                ["us.download.nvidia.com"],
                []);
            var update = new UpdateCheckResult(
                driver.Identity,
                driver.DeviceName,
                installedVersion,
                availableVersion,
                UpdateStatus.Available,
                "manufacturer-driver:nvidia",
                "NVIDIA official WHQL driver catalog",
                "neutral",
                "NVIDIA international multi-language installer",
                "x64",
                "game-ready",
                detailsUri.AbsoluteUri,
                "The official NVIDIA installer is protected by NVIDIA's published SHA-256 and Authenticode publisher.",
                plan);
            return new ManufacturerDriverResult(
                driver with { InstalledVersion = installedVersion },
                ManufacturerDriverStatus.Available,
                availableVersion,
                "NVIDIA",
                detailsUri,
                "A newer official NVIDIA WHQL Game Ready driver is available.",
                update);
        }
        catch (Exception exception)
        {
            return Error(driver, "NVIDIA", NvidiaDriverHome, exception.Message);
        }
    }

    internal async Task<ManufacturerDriverResult> CheckRealtekEthernetAsync(
        InstalledHardwareDriver driver,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await GetStringAsync(RealtekPcieApi, cancellationToken));
            var windows = document.RootElement.GetProperty("Data").GetProperty("DownloadItems").GetProperty("Windows");
            JsonElement? selected = null;
            foreach (var item in windows.EnumerateArray())
            {
                var description = item.GetProperty("Description").GetString() ?? string.Empty;
                if (description.Equals("Win10/Win11 Auto Installation Program (NDIS)", StringComparison.OrdinalIgnoreCase))
                {
                    selected = item;
                    break;
                }
            }
            if (!selected.HasValue)
            {
                throw new InvalidDataException("Realtek's official PCIe catalog returned no standard Windows NDIS package.");
            }

            var itemValue = selected.Value;
            var catalogVersion = itemValue.GetProperty("Version").GetString() ?? string.Empty;
            var catalogDate = NormalizeDate(itemValue.GetProperty("UpdateTime").GetString());
            var downloadId = itemValue.GetProperty("DownloadId").GetString();
            var exactUri = string.IsNullOrWhiteSpace(downloadId)
                ? RealtekPcieCatalog
                : new Uri($"https://www.realtek.com/Download/ToDownload?type=direct&downloadid={downloadId}");
            var isNewer = RealtekCatalogIsNewer(driver.InstalledVersion, driver.DriverDate, catalogVersion, catalogDate);
            var available = string.IsNullOrWhiteSpace(catalogDate)
                ? catalogVersion
                : $"{catalogVersion} · {catalogDate}";
            return isNewer
                ? new ManufacturerDriverResult(
                    driver,
                    ManufacturerDriverStatus.Available,
                    available,
                    "Realtek RTL8125",
                    exactUri,
                    "Realtek's official RTL8125-compatible PCIe catalog has a newer standard Windows NDIS package. Realtek requires its download page and license flow.",
                    null)
                : Current(driver, available, "Realtek RTL8125", exactUri,
                    "Realtek's official RTL8125-compatible PCIe catalog reports no newer standard Windows NDIS package.");
        }
        catch (Exception exception)
        {
            return Error(driver, "Realtek RTL8125", RealtekPcieCatalog, exception.Message);
        }
    }

    internal async Task<ManufacturerDriverResult> CheckTpLinkAsync(
        InstalledHardwareDriver driver,
        CancellationToken cancellationToken)
    {
        try
        {
            var html = WebUtility.HtmlDecode(await GetStringAsync(TpLinkTbe400Uh, cancellationToken));
            var versions = TpLinkVersionRegex().Matches(html)
                .Select(static match => match.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(static version => NormalizeTpLinkVersion(version), Comparer<string>.Create(VersionOrder.Compare))
                .ToArray();
            if (versions.Length == 0)
            {
                throw new InvalidDataException("TP-Link's exact Archer TBE400UH page returned no Windows driver version.");
            }
            var available = versions[0];
            var current = NormalizeTpLinkVersion(driver.InstalledVersion);
            var newer = VersionOrder.Compare(NormalizeTpLinkVersion(available), current) > 0;
            return newer
                ? new ManufacturerDriverResult(
                    driver,
                    ManufacturerDriverStatus.Available,
                    available,
                    "TP-Link Archer TBE400UH",
                    TpLinkTbe400Uh,
                    "The exact TP-Link hardware-ID catalog page reports a newer stable Windows driver.",
                    null)
                : Current(driver, available, "TP-Link Archer TBE400UH", TpLinkTbe400Uh,
                    "The exact TP-Link hardware-ID catalog page reports the installed driver family as current.");
        }
        catch (Exception exception)
        {
            return Error(driver, "TP-Link Archer TBE400UH", TpLinkTbe400Uh, exception.Message);
        }
    }

    internal async Task<ManufacturerDriverResult> CheckIntelI219Async(
        InstalledHardwareDriver driver,
        CancellationToken cancellationToken)
    {
        try
        {
            var readme = await GetStringAsync(IntelEthernetReadme, cancellationToken);
            var release = IntelEthernetReleaseRegex().Match(readme);
            if (!release.Success || !readme.Contains("Intel® Ethernet Connection I219", StringComparison.OrdinalIgnoreCase) &&
                                    !readme.Contains("Intel(R) Ethernet Connection I219", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Intel's official Ethernet release metadata did not confirm I219 support.");
            }
            var available = release.Groups["version"].Value;
            var releaseDate = NormalizeDate(release.Groups["date"].Value) ?? release.Groups["date"].Value;
            var newer = string.IsNullOrWhiteSpace(driver.DriverDate) ||
                        string.CompareOrdinal(releaseDate, driver.DriverDate) > 0;
            return newer
                ? new ManufacturerDriverResult(
                    driver,
                    ManufacturerDriverStatus.Available,
                    $"{available} · {releaseDate}",
                    "Intel Ethernet I219",
                    IntelEthernetPack,
                    "Intel's official Ethernet pack explicitly supports the I219 family and is newer than the installed driver package. Intel requires acceptance of its license before download.",
                    null)
                : Current(driver, available, "Intel Ethernet I219", IntelEthernetPack,
                    "Intel's official Ethernet pack explicitly supports the I219 family and is not newer than the installed package date.");
        }
        catch (Exception exception)
        {
            return Error(driver, "Intel Ethernet I219", IntelEthernetPack, exception.Message);
        }
    }

    private static ManufacturerDriverResult CheckDellAw3423Dw(InstalledHardwareDriver driver)
    {
        if (driver.DeviceClass.Equals("Monitor", StringComparison.OrdinalIgnoreCase))
        {
            return Current(driver, "A00-00", "Dell AW3423DW", DellAw3423DwDriver,
                "Dell's exact AW3423DW monitor-driver package is the installed A00 generation. Monitor firmware is a separate, deliberately excluded workflow.");
        }
        return new ManufacturerDriverResult(
            driver,
            ManufacturerDriverStatus.ManufacturerManaged,
            null,
            "Dell AW3423DW",
            DellAw3423DwSupport,
            "This Alienware monitor interface belongs to the exact AW3423DW support channel; it is not the monitor INF driver itself.",
            null);
    }

    private static ManufacturerDriverResult NoWdDriverRequired(InstalledHardwareDriver driver) => new(
        driver,
        ManufacturerDriverStatus.NoUpdateRequired,
        null,
        "Western Digital",
        WdExternalFirmwarePolicy,
        "WD Elements is present and uses Microsoft's supported USB-storage/disk driver. Western Digital states that portable and desktop external drives have no firmware update available.",
        null);

    private async Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("NaxUpdater/0.15");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static IReadOnlyList<InstalledHardwareDriver> ReadInstalledDrivers(ICollection<string> issues)
    {
        var candidates = new List<InstalledHardwareDriver>();
        var signedDrivers = ReadSignedDrivers(issues);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPClass, Manufacturer, PNPDeviceID, HardwareID, Present, ConfigManagerErrorCode FROM Win32_PnPEntity");
            foreach (ManagementObject entity in searcher.Get())
            {
                var present = entity["Present"] is bool reportedPresent
                    ? reportedPresent
                    : Convert.ToUInt32(entity["ConfigManagerErrorCode"] ?? 1, CultureInfo.InvariantCulture) == 0;
                if (!present)
                {
                    continue;
                }
                var deviceId = Text(entity["PNPDeviceID"]);
                var name = Text(entity["Name"]);
                if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }
                signedDrivers.TryGetValue(deviceId, out var signed);
                var provider = signed?.Provider ?? "Microsoft";
                var manufacturer = Text(entity["Manufacturer"]) ?? signed?.Manufacturer ?? provider;
                var hardwareId = Strings(entity["HardwareID"]).FirstOrDefault() ?? deviceId;
                var deviceClass = Text(entity["PNPClass"]) ?? signed?.DeviceClass ?? "Unknown";
                if (!ShouldInventory(name, deviceClass, manufacturer, provider, hardwareId))
                {
                    continue;
                }
                candidates.Add(new InstalledHardwareDriver(
                    $"pnp:{deviceId}",
                    name,
                    deviceClass,
                    manufacturer.Trim(),
                    provider.Trim(),
                    signed?.Version ?? "—",
                    signed?.Date,
                    hardwareId,
                    signed?.InfName,
                    true));
            }
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException)
        {
            issues.Add($"Present PnP devices: {exception.Message}");
        }

        candidates.AddRange(ReadInstalledDriverRegistrations(issues));
        return AggregateDriverPackages(candidates);
    }

    private static Dictionary<string, SignedDriverData> ReadSignedDrivers(ICollection<string> issues)
    {
        var results = new Dictionary<string, SignedDriverData>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, DeviceName, DeviceClass, Manufacturer, DriverProviderName, DriverVersion, DriverDate, InfName FROM Win32_PnPSignedDriver");
            foreach (ManagementObject item in searcher.Get())
            {
                var deviceId = Text(item["DeviceID"]);
                if (string.IsNullOrWhiteSpace(deviceId))
                {
                    continue;
                }
                results[deviceId] = new SignedDriverData(
                    Text(item["DeviceName"]),
                    Text(item["DeviceClass"]),
                    Text(item["Manufacturer"]),
                    Text(item["DriverProviderName"]),
                    Text(item["DriverVersion"]),
                    NormalizeDate(Text(item["DriverDate"])),
                    Text(item["InfName"]));
            }
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException)
        {
            issues.Add($"Signed PnP drivers: {exception.Message}");
        }
        return results;
    }

    private static IReadOnlyList<InstalledHardwareDriver> ReadInstalledDriverRegistrations(ICollection<string> issues)
    {
        var results = new List<InstalledHardwareDriver>();
        try
        {
            using var classes = Registry.LocalMachine.OpenSubKey(DriverClassPath);
            if (classes is null)
            {
                return [];
            }
            foreach (var className in classes.GetSubKeyNames())
            {
                using var classKey = classes.OpenSubKey(className);
                if (classKey is null)
                {
                    continue;
                }
                var classLabel = Read(classKey, "Class") ?? className;
                foreach (var instanceName in classKey.GetSubKeyNames().Where(static name => NumericKeyRegex().IsMatch(name)))
                {
                    try
                    {
                        using var instance = classKey.OpenSubKey(instanceName);
                        if (instance is null)
                        {
                            continue;
                        }
                        var name = Read(instance, "DriverDesc");
                        var provider = Read(instance, "ProviderName");
                        var version = Read(instance, "DriverVersion");
                        var hardwareId = Read(instance, "MatchingDeviceId");
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(provider) ||
                            string.IsNullOrWhiteSpace(version) || !ShouldInventory(name, classLabel, provider, provider, hardwareId))
                        {
                            continue;
                        }
                        results.Add(new InstalledHardwareDriver(
                            $"driver-registration:{className}:{instanceName}",
                            name,
                            classLabel,
                            provider,
                            provider,
                            version,
                            NormalizeDate(Read(instance, "DriverDate")),
                            hardwareId,
                            Read(instance, "InfPath"),
                            false));
                    }
                    catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                    {
                        issues.Add($"{className}\\{instanceName}: {exception.Message}");
                    }
                }
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            issues.Add($"Installed driver registrations: {exception.Message}");
        }
        return results;
    }

    private static IReadOnlyList<InstalledHardwareDriver> AggregateDriverPackages(IEnumerable<InstalledHardwareDriver> candidates)
    {
        var results = new List<InstalledHardwareDriver>();
        foreach (var group in candidates.GroupBy(DriverPackageKey, StringComparer.OrdinalIgnoreCase))
        {
            var items = group.ToArray();
            var present = items.Where(static item => item.IsPresent).ToArray();
            var representative = (present.Length > 0 ? present : items)
                .OrderByDescending(static item => Specificity(item.DeviceName))
                .First();
            var distinctNames = items.Select(static item => item.DeviceName)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var name = group.Key.Equals("western-digital:elements", StringComparison.OrdinalIgnoreCase)
                ? "WD Elements 25A3"
                : distinctNames.Length > 1
                ? $"{representative.Provider} — {representative.InfName ?? representative.DeviceClass} ({distinctNames.Length} devices)"
                : representative.DeviceName;
            var hardwareId = items.Select(static item => item.HardwareId)
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
            results.Add(representative with
            {
                Identity = $"driver-package:{group.Key}",
                DeviceName = name,
                HardwareId = hardwareId,
                IsPresent = present.Length > 0,
                DeviceCount = distinctNames.Length
            });
        }
        return results;
    }

    private static string DriverPackageKey(InstalledHardwareDriver driver)
    {
        if (IsWesternDigitalExternal(driver)) return "western-digital:elements";
        if (IsTpLinkTbe400Uh(driver)) return "tp-link:archer-tbe400uh";
        if (driver.Provider.Contains("Razer", StringComparison.OrdinalIgnoreCase))
        {
            var product = RazerProductRegex().Match(driver.HardwareId ?? string.Empty);
            if (product.Success)
            {
                return $"razer:{product.Groups["product"].Value}";
            }
        }
        var inf = driver.InfName ?? driver.HardwareId ?? driver.DeviceName;
        return $"{driver.Provider}|{inf}|{driver.InstalledVersion}";
    }

    private static bool ShouldInventory(string name, string deviceClass, string manufacturer, string provider, string? hardwareId)
    {
        if (!IsMicrosoftProvider(provider))
        {
            return true;
        }
        var identity = $"{name}|{manufacturer}|{hardwareId}";
        return identity.Contains("VID_1058", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("USBSTOR\\DISK&VEN_WD", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("WD Elements", StringComparison.OrdinalIgnoreCase) ||
               (identity.Contains("VEN_10EC", StringComparison.OrdinalIgnoreCase) &&
                deviceClass.Equals("MEDIA", StringComparison.OrdinalIgnoreCase));
    }

    private static ManufacturerDriverResult ManufacturerManaged(InstalledHardwareDriver driver)
    {
        if (driver.Provider.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            return new ManufacturerDriverResult(driver, ManufacturerDriverStatus.ManufacturerManaged, null,
                "Intel Driver & Support Assistant", IntelDsaHome,
                "Intel requires its hardware-aware Driver & Support Assistant for an exact applicability decision; no update is claimed without that scan.", null);
        }
        if (driver.Provider.Contains("Razer", StringComparison.OrdinalIgnoreCase))
        {
            return new ManufacturerDriverResult(driver, ManufacturerDriverStatus.ManufacturerManaged, null,
                "Razer Synapse", RazerSupport,
                "Razer Synapse owns this signed driver package. Repeated device interfaces are collapsed into one installed INF package.", null);
        }
        if (driver.Provider.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase))
        {
            return new ManufacturerDriverResult(driver, ManufacturerDriverStatus.ManufacturerManaged, null,
                "MSI MAG Z490 TOMAHAWK", MsiBoardSupport,
                "This MSI-specific component is routed to the exact motherboard support channel.", null);
        }
        if (driver.HardwareId?.Contains("VEN_10EC", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new ManufacturerDriverResult(driver, ManufacturerDriverStatus.ManufacturerManaged, null,
                "MSI MAG Z490 TOMAHAWK audio", MsiBoardSupport,
                "Realtek's official guidance requires motherboard-OEM audio packages because generic audio drivers can omit customizations.", null);
        }
        return NoVerifiedCatalog(driver, driver.Provider, null,
            "The driver package is inventoried, but no exact manufacturer catalog identity is available. No action is offered.");
    }

    private static ManufacturerDriverResult Current(InstalledHardwareDriver driver, string? available, string source, Uri uri, string message) =>
        new(driver, ManufacturerDriverStatus.Current, available, source, uri, message, null);

    private static ManufacturerDriverResult NoVerifiedCatalog(InstalledHardwareDriver driver, string source, Uri? uri, string message) =>
        new(driver, ManufacturerDriverStatus.NoVerifiedCatalog, null, source, uri, message, null);

    private static ManufacturerDriverResult Error(InstalledHardwareDriver driver, string source, Uri uri, string message) =>
        new(driver, ManufacturerDriverStatus.Error, null, source, uri, message, null);

    internal static string NormalizeNvidiaVersion(string version)
    {
        var parts = version.Split('.');
        if (parts.Length == 4 && int.TryParse(parts[2], out var build) && int.TryParse(parts[3], out var revision))
        {
            var compact = $"{Math.Abs(build) % 10}{Math.Abs(revision):0000}";
            return $"{compact[..3]}.{compact[3..]}";
        }
        return version;
    }

    internal static string NormalizeTpLinkVersion(string version)
    {
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 4 ? string.Join('.', parts.Skip(1)) : version;
    }

    internal static bool RealtekCatalogIsNewer(string installedVersion, string? installedDate, string catalogVersion, string? catalogDate)
    {
        var installedCore = string.Join('.', installedVersion.Split('.').Take(3));
        if (VersionOrder.Compare(catalogVersion, installedCore) > 0)
        {
            return true;
        }
        return !string.IsNullOrWhiteSpace(catalogDate) &&
               !string.IsNullOrWhiteSpace(installedDate) &&
               string.CompareOrdinal(catalogDate, installedDate) > 0;
    }

    private static string? NvidiaSeriesId(string deviceName) => NvidiaRtx50Regex().IsMatch(deviceName) ? "120" : null;

    private static bool IsNvidiaDisplayDriver(InstalledHardwareDriver driver) =>
        driver.Provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) &&
        driver.DeviceName.Contains("GeForce", StringComparison.OrdinalIgnoreCase);

    private static bool IsRealtek8125(InstalledHardwareDriver driver) =>
        driver.HardwareId?.Contains("VEN_10EC&DEV_8125", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsIntelI219(InstalledHardwareDriver driver) =>
        driver.HardwareId?.Contains("VEN_8086&DEV_15BC", StringComparison.OrdinalIgnoreCase) == true ||
        driver.DeviceName.Contains("I219-V", StringComparison.OrdinalIgnoreCase);

    private static bool IsTpLinkTbe400Uh(InstalledHardwareDriver driver) =>
        driver.HardwareId?.Contains("VID_3625&PID_010A", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsDellAw3423DwDriver(InstalledHardwareDriver driver) =>
        driver.DeviceName.Contains("AW3423DW", StringComparison.OrdinalIgnoreCase) ||
        driver.HardwareId?.Contains("DELA1E4", StringComparison.OrdinalIgnoreCase) == true ||
        driver.HardwareId?.Contains("VID_187C&PID_100B", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsWesternDigitalExternal(InstalledHardwareDriver driver) =>
        driver.DeviceName.Contains("WD Elements", StringComparison.OrdinalIgnoreCase) ||
        driver.HardwareId?.Contains("VID_1058&PID_25A3", StringComparison.OrdinalIgnoreCase) == true ||
        driver.HardwareId?.Contains("USBSTOR\\DISKWD", StringComparison.OrdinalIgnoreCase) == true ||
        driver.HardwareId?.Contains("USBSTOR\\DISK&VEN_WD&PROD_ELEMENTS_25A3", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsMicrosoftProvider(string provider) =>
        provider.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ||
        provider.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase) ||
        provider.Equals("Windows", StringComparison.OrdinalIgnoreCase);

    private static int Specificity(string value) =>
        value.Count(char.IsLetterOrDigit) +
        (value.Contains("WD Elements", StringComparison.OrdinalIgnoreCase) ? 100 : 0) +
        (value.Contains("Razer ", StringComparison.OrdinalIgnoreCase) ? 40 : 0) -
        (value.Contains("compliant", StringComparison.OrdinalIgnoreCase) ? 20 : 0) -
        (value.Contains("USB Input Device", StringComparison.OrdinalIgnoreCase) ? 20 : 0);

    private static string? NormalizeDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            if (value.Length >= 14 && value.Take(14).All(char.IsDigit))
            {
                return ManagementDateTimeConverter.ToDateTime(value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            // Continue with ordinary date parsing.
        }
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed) ||
               DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out parsed)
            ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : value.Trim();
    }

    private static string? Text(object? value) => value?.ToString()?.Trim() is { Length: > 0 } text ? text : null;

    private static IReadOnlyList<string> Strings(object? value) => value switch
    {
        string[] values => values.Where(static item => !string.IsNullOrWhiteSpace(item)).ToArray(),
        string text when !string.IsNullOrWhiteSpace(text) => [text],
        _ => []
    };

    private static string? Read(RegistryKey key, string name) =>
        key.GetValue(name)?.ToString()?.Trim() is { Length: > 0 } value ? value : null;

    [GeneratedRegex(@"^\d{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericKeyRegex();

    [GeneratedRegex(@"GeForce\s+RTX\s+50\d{2}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NvidiaRtx50Regex();

    [GeneratedRegex(@"driverResults\.aspx/(?<id>\d+)/en-us[^>]*>.*?</a>.*?</td>\s*<td[^>]*>(?<version>\d+\.\d+)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex NvidiaResultRegex();

    [GeneratedRegex(@"(?im)^\s*(?<hash>[0-9a-f]{64})\s+")]
    private static partial Regex Sha256Regex();

    [GeneratedRegex(@"(?:50|51)02\.\d+\.\d+\.\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TpLinkVersionRegex();

    [GeneratedRegex(@"Release\s+(?<version>\d+(?:\.\d+)+).*?(?<date>20\d{2}-\d{2}-\d{2}|July\s+20\d{2})", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex IntelEthernetReleaseRegex();

    [GeneratedRegex(@"VID_1532&PID_(?<product>[0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RazerProductRegex();

    private sealed record SignedDriverData(
        string? DeviceName,
        string? DeviceClass,
        string? Manufacturer,
        string? Provider,
        string? Version,
        string? Date,
        string? InfName);
}
