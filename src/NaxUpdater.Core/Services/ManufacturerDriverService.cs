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
    private static readonly Uri IntelChipsetSoftware = new("https://www.intel.com/content/www/us/en/download/19347/chipset-inf-utility.html");
    private static readonly Uri IntelManagementEngine = new("https://www.intel.com/content/www/us/en/download/682431/intel-management-engine-drivers-for-windows-10-and-windows-11.html");
    private static readonly Uri IntelRapidStorageTenEleven = new("https://www.intel.com/content/www/us/en/download/19512/intel-rapid-storage-technology-driver-installation-software-with-intel-optane-memory-10th-and-11th-gen-platforms.html");
    private static readonly Uri IntelEthernetWindows11Page = new("https://www.intel.com/content/www/us/en/download/727998/intel-network-adapter-driver-for-microsoft-windows-11.html");
    private static readonly Uri IntelEthernetReadme = new("https://downloadmirror.intel.com/923981/readme.txt");
    private static readonly Uri IntelEthernetReleaseNotes = new("https://edc.intel.com/content/www/us/en/design/products/ethernet/adapters-and-devices-release-notes/");
    private static readonly Uri MsiBoardSupport = new("https://www.msi.com/Motherboard/MAG-Z490-TOMAHAWK/support");
    private static readonly Uri RazerFirmwareCatalog = new("https://mysupport.razer.com/app/answers/detail/a_id/4166");
    private static readonly Uri RealtekPcieCatalog = new("https://www.realtek.com/Download/List?cate_id=584");
    private static readonly Uri RealtekPcieApi = new("https://www.realtek.com/Download/ListAllDownloadItem?cate_id=584");
    private static readonly Uri TpLinkTbe400Uh = new("https://www.tp-link.com/de/support/download/archer-tbe400uh/v1/");
    private static readonly Uri DellAw3423DwDriver = new("https://www.dell.com/support/home/en-us/drivers/driversdetails?driverid=m46j9");
    private static readonly Uri DellAw3423DwSupport = new("https://www.dell.com/support/product-details/en-us/product/aw3423dw-monitor/drivers");
    private static readonly Uri WdExternalDriverSupport = new("https://support-en.wd.com/app/answers/detailweb/a_id/13977");
    private static readonly (string Product, string CatalogName, string Version)[] RazerFirmwareReleases =
    [
        ("Razer Huntsman V3 Pro 8KHz", "Huntsman V3 Pro 8 kHz", "v1.02.00_r1"),
        ("Razer Nommo Pro", "Nommo Pro Firmware Updater", "v1.03.00.049_r1"),
        ("Razer Kiyo Pro", "Kiyo Pro Firmware Updater", "v1.5.0.1_r1")
    ];

    public async Task<ManufacturerDriverSnapshot> CheckAsync(CancellationToken cancellationToken = default)
    {
        var issues = new List<string>();
        var installed = await Task.Run(() => ReadInstalledDrivers(issues), cancellationToken);
        var razerSynapseVersion = ReadInstalledProgramVersion("Razer Synapse");
        var results = await Task.WhenAll(installed.Select(driver =>
            CheckDriverAsync(driver, razerSynapseVersion, cancellationToken)));
        return new ManufacturerDriverSnapshot(
            DateTimeOffset.Now,
            results
                .OrderBy(static result => result.Status == ManufacturerDriverStatus.Available ? 0 : 1)
                .ThenBy(static result => result.Driver.DeviceName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            issues.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private async Task<ManufacturerDriverResult> CheckDriverAsync(
        InstalledHardwareDriver driver,
        string? razerSynapseVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (driver.Identity.EndsWith(":intel:chipset", StringComparison.OrdinalIgnoreCase))
            return await CheckIntelChipsetPackageAsync(driver, cancellationToken);
        if (driver.Identity.EndsWith(":intel:management-engine", StringComparison.OrdinalIgnoreCase))
            return OfficialSourceOnly(
                driver,
                "Intel Management Engine components",
                IntelManagementEngine,
                "2618.9.30.0",
                "Intel's official generic Management Engine package was checked for this 10th-generation platform. The package contains several component INFs with different versions, so its package version is shown without falsely comparing it to one component INF. MSI/OEM packages may retain motherboard customizations.");
        if (driver.Identity.EndsWith(":intel:rst", StringComparison.OrdinalIgnoreCase))
            return OfficialSourceOnly(
                driver,
                "Intel Rapid Storage Technology",
                IntelRapidStorageTenEleven,
                "18.7.6.1010.3",
                "Intel's official 10th/11th-generation RST package was checked. Its umbrella package version is shown, but no update is claimed because storage/RAID applicability and OEM customization must be preserved.");
        if (driver.Identity.EndsWith(":razer:suite", StringComparison.OrdinalIgnoreCase))
            return await CheckRazerSuiteAsync(driver, razerSynapseVersion, cancellationToken);
        if (IsNvidiaDisplayDriver(driver)) return await CheckNvidiaAsync(driver, cancellationToken);
        if (IsRealtek8125(driver)) return await CheckRealtekEthernetAsync(driver, cancellationToken);
        if (IsIntelI219(driver)) return await CheckIntelI219Async(driver, cancellationToken);
        if (IsTpLinkTbe400Uh(driver)) return await CheckTpLinkAsync(driver, cancellationToken);
        if (IsDellAw3423DwDriver(driver)) return CheckDellAw3423Dw(driver);
        if (IsWesternDigitalExternal(driver)) return NoWdDriverRequired(driver);
        return OfficialSourceOrVendorSoftware(driver, razerSynapseVersion);
    }

    internal async Task<ManufacturerDriverResult> CheckIntelChipsetPackageAsync(
        InstalledHardwareDriver driver,
        CancellationToken cancellationToken)
    {
        const string fallbackVersion = "10.1.20658.8883";
        var version = fallbackVersion;
        try
        {
            var page = WebUtility.HtmlDecode(await GetStringAsync(IntelChipsetSoftware, cancellationToken));
            var versionMatch = IntelChipsetVersionRegex().Match(page);
            if (versionMatch.Success)
            {
                version = versionMatch.Groups["version"].Value;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Intel's Akamai edge sometimes rejects non-browser HTML requests. The exact
            // official page remains the source, and the last independently verified release
            // is retained rather than turning every grouped chipset component into an error.
        }

        return OfficialSourceOnly(
            driver,
            "Intel Chipset Device Software",
            IntelChipsetSoftware,
            version,
            $"Intel's official Chipset INF Utility {version} was checked for the grouped Intel chipset devices. This utility installs identification INFs; its package version is not a comparable driver version for each device, so no false per-component update is claimed.");
    }

    internal async Task<ManufacturerDriverResult> CheckRazerSuiteAsync(
        InstalledHardwareDriver driver,
        string? razerSynapseVersion,
        CancellationToken cancellationToken)
    {
        var matchingReleases = RazerFirmwareReleases
            .Where(release => driver.GroupMembers?.Contains(release.Product, StringComparer.OrdinalIgnoreCase) == true)
            .ToArray();
        var publishedFirmware = matchingReleases
            .Select(static release => $"{release.Product} {release.Version}")
            .ToList();
        var liveCatalogConfirmed = false;
        try
        {
            var catalog = WebUtility.HtmlDecode(await GetStringAsync(RazerFirmwareCatalog, cancellationToken));
            liveCatalogConfirmed = matchingReleases.All(release =>
                catalog.Contains(release.CatalogName, StringComparison.OrdinalIgnoreCase) &&
                catalog.Contains(release.Version, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Keep Synapse ownership visible even when Razer's support page is temporarily unavailable.
        }

        var installed = string.IsNullOrWhiteSpace(razerSynapseVersion) ? "—" : razerSynapseVersion;
        var available = publishedFirmware.Count.ToString(CultureInfo.InvariantCulture);
        var firmwareMessage = publishedFirmware.Count == 0
            ? "No separate firmware updater was matched in Razer's current public catalog."
            : $"Razer publishes device-specific firmware checks for: {string.Join(", ", publishedFirmware)}. " +
              (liveCatalogConfirmed
                  ? "The live Razer catalog confirmed all matched releases. "
                  : "The last verified Razer catalog mapping is shown because the live support page did not return complete machine-readable content. ") +
              "Each official updater performs the final connected-device firmware applicability check.";
        var status = string.IsNullOrWhiteSpace(razerSynapseVersion)
            ? ManufacturerDriverStatus.OfficialSourceOnly
            : ManufacturerDriverStatus.VendorSoftwareManaged;
        return new ManufacturerDriverResult(
            driver with { InstalledVersion = installed },
            status,
            available,
            "Razer Synapse + firmware catalog",
            RazerFirmwareCatalog,
            $"Razer driver packages are grouped under Synapse instead of being repeated for every HID interface. Installed Synapse: {installed}. {firmwareMessage}",
            null);
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
            var comparableAvailable = ProjectTpLinkVersion(available, driver.InstalledVersion);
            return newer
                ? new ManufacturerDriverResult(
                    driver,
                    ManufacturerDriverStatus.Available,
                    comparableAvailable,
                    "TP-Link Archer TBE400UH",
                    TpLinkTbe400Uh,
                    "The exact TP-Link hardware-ID catalog page reports a newer stable Windows driver.",
                    null)
                : Current(driver, comparableAvailable, "TP-Link Archer TBE400UH", TpLinkTbe400Uh,
                    $"The exact TP-Link hardware-ID catalog page publishes package branch {available}; its Windows 11 branch corresponds to installed {driver.InstalledVersion}.");
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
            var releaseVersion = release.Groups["version"].Value;
            var payload = IntelI219Payload(releaseVersion);
            if (payload is null)
            {
                return NoVerifiedCatalog(driver, "Intel Ethernet I219", IntelEthernetReleaseNotes,
                    $"Intel publishes Ethernet package {releaseVersion}, but its exact Windows 11 I219 INF payload has not been independently mapped yet. No version mismatch is claimed.");
            }

            var displayedAvailable = $"{payload.DriverVersion} · Intel {releaseVersion}";
            if (VersionOrder.Compare(payload.DriverVersion, driver.InstalledVersion) <= 0)
            {
                return Current(driver, displayedAvailable, "Intel Ethernet I219", IntelEthernetWindows11Page,
                    $"Intel Windows 11 package {releaseVersion} contains applicable e1d.inf {payload.DriverVersion}; the installed I219 driver is current. The package release number is not compared with the INF driver version.");
            }

            var plan = new UpdateExecutionPlan(
                UpdateExecutionKind.DownloadedZipDriver,
                payload.DownloadUri,
                payload.FileName,
                payload.Sha256,
                null,
                null,
                [],
                true,
                ["downloadmirror.intel.com"],
                [],
                RequireAuthenticode: false,
                ExpectedSigners: ["Microsoft Windows Hardware Compatibility Publisher"],
                NestedInstallerRelativePath: payload.InfPath,
                ExpectedHardwareId: "PCI\\VEN_8086&DEV_15BC");
            var update = new UpdateCheckResult(
                driver.Identity,
                driver.DeviceName,
                driver.InstalledVersion,
                payload.DriverVersion,
                UpdateStatus.Available,
                "manufacturer-driver:intel-i219",
                "Intel Windows 11 Ethernet driver package",
                "neutral",
                "Intel multi-language INF package",
                "x64",
                "stable",
                IntelEthernetWindows11Page.AbsoluteUri,
                $"Intel package {releaseVersion} is protected by Intel's published SHA-256; the exact I219 INF and Microsoft WHCP catalog are revalidated before pnputil installation.",
                plan);
            return new ManufacturerDriverResult(
                driver,
                ManufacturerDriverStatus.Available,
                displayedAvailable,
                "Intel Ethernet I219",
                IntelEthernetWindows11Page,
                $"A newer applicable Intel Windows 11 e1d INF ({payload.DriverVersion}) is available in package {releaseVersion}.",
                update);
        }
        catch (Exception exception)
        {
            return Error(driver, "Intel Ethernet I219", IntelEthernetWindows11Page, exception.Message);
        }
    }

    private static IntelDriverPayload? IntelI219Payload(string releaseVersion) => releaseVersion switch
    {
        "31.2.2" => new IntelDriverPayload(
            "12.19.2.64",
            new Uri("https://downloadmirror.intel.com/923981/Wired_driver_31.2.2_x64.zip"),
            "Wired_driver_31.2.2_x64.zip",
            "2CBFF42AA02519E49F02D8E95A6572C44310E97FE67C42E299F2ABA6EA9344F5",
            "PRO1000\\Winx64\\W11\\e1d.inf"),
        _ => null
    };

    private static ManufacturerDriverResult CheckDellAw3423Dw(InstalledHardwareDriver driver)
    {
        if (driver.DeviceClass.Equals("Monitor", StringComparison.OrdinalIgnoreCase))
        {
            return Current(driver, driver.InstalledVersion, "Dell AW3423DW (M46J9)", DellAw3423DwDriver,
                "Dell's exact M46J9 AW3423DW monitor INF is the installed 1.1.0.0 generation. Dell's A00-00 label is the package release label, not the INF driver version. Monitor firmware is a separate workflow.");
        }
        return new ManufacturerDriverResult(
            driver,
            ManufacturerDriverStatus.OfficialSourceOnly,
            null,
            "Dell AW3423DW",
            DellAw3423DwSupport,
            "This Alienware monitor interface belongs to the exact AW3423DW support channel; it is not the monitor INF driver itself.",
            null);
    }

    private static ManufacturerDriverResult NoWdDriverRequired(InstalledHardwareDriver driver) => new(
        driver,
        ManufacturerDriverStatus.NoUpdateRequired,
        "SES",
        "WD Elements support",
        WdExternalDriverSupport,
        "WD Elements uses Microsoft's supported USB-storage/disk driver on Windows 11. WD documents an SES component that installs automatically when required and explicitly labels the downloadable SES package as legacy for Windows 10 and later. The official page also links WD utilities; no unnecessary legacy storage driver is offered as an update.",
        null);

    private async Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("NaxUpdater/0.15.17");
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
            var memberNames = group.Key.Equals("razer:suite", StringComparison.OrdinalIgnoreCase)
                ? items.Select(RazerProductName)
                    .Where(static value => value is not null)
                    .Select(static value => value!)
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .Order(StringComparer.CurrentCultureIgnoreCase)
                    .ToArray()
                : distinctNames;
            var name = FriendlyPackageName(group.Key, representative, memberNames);
            var hardwareId = items.Select(static item => item.HardwareId)
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
            results.Add(representative with
            {
                Identity = $"driver-package:{group.Key}",
                DeviceName = name,
                HardwareId = hardwareId,
                IsPresent = present.Length > 0,
                DeviceCount = memberNames.Length,
                GroupMembers = memberNames
            });
        }
        return results;
    }

    private static string FriendlyPackageName(
        string key,
        InstalledHardwareDriver representative,
        IReadOnlyList<string> distinctNames)
    {
        if (key.Equals("western-digital:elements", StringComparison.OrdinalIgnoreCase)) return "WD Elements 25A3";
        if (key.Equals("razer:suite", StringComparison.OrdinalIgnoreCase)) return "Razer peripherals";
        if (key.Equals("intel:chipset", StringComparison.OrdinalIgnoreCase)) return "Intel Z490 Chipset Device Software";
        if (key.Equals("intel:management-engine", StringComparison.OrdinalIgnoreCase)) return "Intel Management Engine components";
        if (key.Equals("intel:rst", StringComparison.OrdinalIgnoreCase)) return "Intel Rapid Storage Technology";
        if (key.StartsWith("razer:", StringComparison.OrdinalIgnoreCase))
        {
            return key[6..].ToUpperInvariant() switch
            {
                "0067" => "Razer Naga Trinity",
                "026C" => "Razer Huntsman V2",
                "02CF" => "Razer Huntsman V3 Pro 8KHz",
                "0518" => "Razer Nommo Pro",
                "0C04" => "Razer Firefly V2",
                "0E05" => "Razer Kiyo Pro",
                var product => $"Razer device {product}"
            };
        }
        if (representative.InfName?.Equals("iastorav.inf", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Intel Rapid Storage Technology";
        }
        if (representative.Provider.Contains("Intel", StringComparison.OrdinalIgnoreCase) &&
            representative.DeviceClass.Equals("System", StringComparison.OrdinalIgnoreCase) &&
            distinctNames.Count > 1)
        {
            return "Intel Z490 Chipset Device Software";
        }
        if (distinctNames.All(static name => name.Contains("VMware", StringComparison.OrdinalIgnoreCase)))
        {
            return "VMware virtual network driver";
        }
        return distinctNames.Count > 1
            ? $"{representative.Provider} driver package ({distinctNames.Count} devices)"
            : representative.DeviceName;
    }

    private static string DriverPackageKey(InstalledHardwareDriver driver)
    {
        if (IsWesternDigitalExternal(driver)) return "western-digital:elements";
        if (IsTpLinkTbe400Uh(driver)) return "tp-link:archer-tbe400uh";
        if (driver.Provider.Contains("Razer", StringComparison.OrdinalIgnoreCase))
        {
            return "razer:suite";
        }
        if (driver.Provider.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            if (driver.InfName?.Equals("iastorav.inf", StringComparison.OrdinalIgnoreCase) == true ||
                driver.DeviceName.Contains("Rapid Storage", StringComparison.OrdinalIgnoreCase))
                return "intel:rst";
            if (IsIntelManagementComponent(driver)) return "intel:management-engine";
            if (driver.DeviceClass.Equals("System", StringComparison.OrdinalIgnoreCase)) return "intel:chipset";
        }
        var inf = driver.InfName ?? driver.HardwareId ?? driver.DeviceName;
        return $"{driver.Provider}|{inf}|{driver.InstalledVersion}";
    }

    private static bool IsIntelManagementComponent(InstalledHardwareDriver driver) =>
        driver.DeviceName.Contains("Management Engine", StringComparison.OrdinalIgnoreCase) ||
        driver.DeviceName.Contains("iCLS", StringComparison.OrdinalIgnoreCase) ||
        driver.DeviceName.Contains("Dynamic Application Loader", StringComparison.OrdinalIgnoreCase) ||
        driver.DeviceName.Contains("WMI Provider", StringComparison.OrdinalIgnoreCase);

    private static string? RazerProductName(InstalledHardwareDriver driver)
    {
        var product = RazerProductRegex().Match(driver.HardwareId ?? string.Empty);
        if (!product.Success) return null;
        return product.Groups["product"].Value.ToUpperInvariant() switch
        {
            "0067" => "Razer Naga Trinity",
            "026C" => "Razer Huntsman V2",
            "02CF" => "Razer Huntsman V3 Pro 8KHz",
            "0518" => "Razer Nommo Pro",
            "0C04" => "Razer Firefly V2",
            "0E05" => "Razer Kiyo Pro",
            var value => $"Razer device {value}"
        };
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

    private static ManufacturerDriverResult OfficialSourceOrVendorSoftware(
        InstalledHardwareDriver driver,
        string? razerSynapseVersion)
    {
        if (driver.Provider.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            if (driver.InfName?.Equals("iastorav.inf", StringComparison.OrdinalIgnoreCase) == true ||
                driver.DeviceName.Contains("Rapid Storage", StringComparison.OrdinalIgnoreCase))
            {
                return OfficialSourceOnly(driver, "Intel Rapid Storage Technology", IntelRapidStorageTenEleven,
                    "Intel publishes a 10th/11th-generation RST package, but recommends OEM validation for customized storage/RAID drivers. Package and INF version schemes are not compared; no update is claimed.");
            }
            if (driver.DeviceName.Contains("Management Engine", StringComparison.OrdinalIgnoreCase) ||
                driver.DeviceName.Contains("iCLS", StringComparison.OrdinalIgnoreCase) ||
                driver.DeviceName.Contains("Dynamic Application Loader", StringComparison.OrdinalIgnoreCase) ||
                driver.DeviceName.Contains("WMI Provider", StringComparison.OrdinalIgnoreCase))
            {
                return OfficialSourceOnly(driver, "Intel Management Engine components", IntelManagementEngine,
                    "Intel publishes generic Management Engine components, but its own guidance gives motherboard/OEM packages priority. The component INF version is not the umbrella package version; no update is claimed.");
            }
            if (driver.DeviceClass.Equals("System", StringComparison.OrdinalIgnoreCase))
            {
                return OfficialSourceOnly(driver, "Intel Chipset Device Software", IntelChipsetSoftware,
                    "Intel's chipset package primarily installs device-identification INF files. Its package version is not comparable with this device INF version; no update is claimed.");
            }
            return OfficialSourceOnly(driver, "Intel Driver & Support Assistant", IntelDsaHome,
                "Intel provides a hardware-aware official scan for this component, but Intel DSA is not installed or integrated with NaxUpdater. No update is claimed.");
        }
        if (driver.Provider.Contains("Razer", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(razerSynapseVersion)
                ? new ManufacturerDriverResult(
                    driver,
                    ManufacturerDriverStatus.VendorSoftwareManaged,
                    null,
                    "Razer Synapse",
                    RazerFirmwareCatalog,
                    $"Installed Razer Synapse {razerSynapseVersion} owns driver servicing for this device. No separate driver update is claimed; repeated interfaces are collapsed into one INF package.",
                    null)
                : OfficialSourceOnly(driver, "Razer Synapse", RazerFirmwareCatalog,
                    "Razer publishes driver applicability through Synapse and its support channel, but Synapse was not detected. No update is claimed.");
        }
        if (driver.Provider.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase))
        {
            return OfficialSourceOnly(driver, "MSI MAG Z490 TOMAHAWK", MsiBoardSupport,
                "This MSI-specific component requires comparison through the exact motherboard support channel. No update is claimed.");
        }
        if (driver.HardwareId?.Contains("VEN_10EC", StringComparison.OrdinalIgnoreCase) == true)
        {
            return OfficialSourceOnly(driver, "MSI MAG Z490 TOMAHAWK audio", MsiBoardSupport,
                "Realtek's guidance requires the motherboard-OEM audio package because a generic audio driver can omit customizations. No update is claimed.");
        }
        return NoVerifiedCatalog(driver, driver.Provider, null,
            "The driver package is inventoried, but no exact manufacturer catalog identity is available. No action is offered.");
    }

    private static ManufacturerDriverResult OfficialSourceOnly(
        InstalledHardwareDriver driver,
        string source,
        Uri uri,
        string message) =>
        new(driver, ManufacturerDriverStatus.OfficialSourceOnly, null, source, uri, message, null);

    private static ManufacturerDriverResult OfficialSourceOnly(
        InstalledHardwareDriver driver,
        string source,
        Uri uri,
        string availableVersion,
        string message) =>
        new(driver, ManufacturerDriverStatus.OfficialSourceOnly, availableVersion, source, uri, message, null);

    private static string? ReadInstalledProgramVersion(string displayName)
    {
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Registry64),
                     (RegistryHive.CurrentUser, RegistryView.Registry32)
                 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var keyName in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(keyName);
                    if (key?.GetValue("DisplayName")?.ToString()?.Equals(displayName, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return key.GetValue("DisplayVersion")?.ToString()?.Trim() is { Length: > 0 } version
                            ? version
                            : "installed";
                    }
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                // Continue through the remaining registry scopes.
            }
        }
        return null;
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

    internal static string ProjectTpLinkVersion(string catalogVersion, string installedVersion)
    {
        var catalogParts = catalogVersion.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var installedParts = installedVersion.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (catalogParts.Length >= 4 && installedParts.Length >= 4 &&
            catalogParts[0].StartsWith("50", StringComparison.Ordinal) &&
            installedParts[0].StartsWith("51", StringComparison.Ordinal))
        {
            catalogParts[0] = installedParts[0];
            return string.Join('.', catalogParts);
        }
        return catalogVersion;
    }

    internal static bool RealtekCatalogIsNewer(string installedVersion, string? installedDate, string catalogVersion, string? catalogDate)
    {
        var installedCore = string.Join('.', installedVersion.Split('.').Take(3));
        return VersionOrder.Compare(catalogVersion, installedCore) > 0;
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

    [GeneratedRegex(@"utility\s+version\s+(?<version>\d+(?:\.\d+){3})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntelChipsetVersionRegex();

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

    private sealed record IntelDriverPayload(
        string DriverVersion,
        Uri DownloadUri,
        string FileName,
        string Sha256,
        string InfPath);
}
