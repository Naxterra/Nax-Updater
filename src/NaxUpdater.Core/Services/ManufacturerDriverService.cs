using Microsoft.Win32;
using NaxUpdater.Core.Models;
using System.Text.RegularExpressions;

namespace NaxUpdater.Core.Services;

public sealed partial class ManufacturerDriverService(HttpClient httpClient)
{
    private const string DriverClassPath = @"SYSTEM\CurrentControlSet\Control\Class";
    private static readonly Uri NvidiaDriverHome = new("https://www.nvidia.com/Download/index.aspx");
    private static readonly Uri IntelDsaHome = new("https://www.intel.com/content/www/us/en/support/detect.html");
    private static readonly Uri MsiBoardSupport = new("https://www.msi.com/Motherboard/MAG-Z490-TOMAHAWK/support");
    private static readonly Uri RazerSupport = new("https://mysupport.razer.com/app/answers/detail/a_id/1835");
    private static readonly Uri DellDrivers = new("https://www.dell.com/support/home/drivers");

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
            issues);
    }

    internal async Task<ManufacturerDriverResult> CheckNvidiaAsync(
        InstalledHardwareDriver driver,
        CancellationToken cancellationToken)
    {
        var seriesId = NvidiaSeriesId(driver.DeviceName);
        if (seriesId is null)
        {
            return new ManufacturerDriverResult(
                driver,
                ManufacturerDriverStatus.ManufacturerManaged,
                null,
                "NVIDIA",
                NvidiaDriverHome,
                "The NVIDIA device was inventoried, but its product series is not yet mapped to NVIDIA's official driver search.",
                null);
        }

        try
        {
            var searchUri = new Uri($"https://www.nvidia.com/Download/processFind.aspx?dtcid=1&lang=en-us&lid=1&osid=57&psid={seriesId}&whql=1");
            using var searchRequest = new HttpRequestMessage(HttpMethod.Get, searchUri);
            searchRequest.Headers.UserAgent.ParseAdd("NaxUpdater/0.14");
            using var searchResponse = await httpClient.SendAsync(searchRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            searchResponse.EnsureSuccessStatusCode();
            var html = await searchResponse.Content.ReadAsStringAsync(cancellationToken);
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
                return new ManufacturerDriverResult(
                    driver with { InstalledVersion = installedVersion },
                    ManufacturerDriverStatus.Current,
                    availableVersion,
                    "NVIDIA",
                    detailsUri,
                    "NVIDIA's official WHQL Game Ready catalog reports the installed driver as current.",
                    null);
            }

            var fileName = $"{availableVersion}-desktop-win10-win11-64bit-international-dch-whql.exe";
            var downloadUri = new Uri($"https://us.download.nvidia.com/Windows/{availableVersion}/{fileName}");
            var hashUri = new Uri(downloadUri.AbsoluteUri + ".sha256");
            using var hashRequest = new HttpRequestMessage(HttpMethod.Get, hashUri);
            hashRequest.Headers.UserAgent.ParseAdd("NaxUpdater/0.14");
            using var hashResponse = await httpClient.SendAsync(hashRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            hashResponse.EnsureSuccessStatusCode();
            var hashText = await hashResponse.Content.ReadAsStringAsync(cancellationToken);
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
            return new ManufacturerDriverResult(
                driver,
                ManufacturerDriverStatus.Error,
                null,
                "NVIDIA",
                NvidiaDriverHome,
                exception.Message,
                null);
        }
    }

    private static IReadOnlyList<InstalledHardwareDriver> ReadInstalledDrivers(ICollection<string> issues)
    {
        var results = new Dictionary<string, InstalledHardwareDriver>(StringComparer.OrdinalIgnoreCase);
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
                        var deviceName = Read(instance, "DriverDesc");
                        var provider = Read(instance, "ProviderName");
                        var version = Read(instance, "DriverVersion");
                        if (string.IsNullOrWhiteSpace(deviceName) || string.IsNullOrWhiteSpace(provider) ||
                            string.IsNullOrWhiteSpace(version) || IsMicrosoftProvider(provider))
                        {
                            continue;
                        }
                        var hardwareId = Read(instance, "MatchingDeviceId");
                        var identity = $"driver:{className}:{hardwareId ?? deviceName}:{provider}";
                        results[identity] = new InstalledHardwareDriver(
                            identity,
                            deviceName,
                            classLabel,
                            Read(instance, "Mfg") ?? provider,
                            provider,
                            version,
                            Read(instance, "DriverDate"),
                            hardwareId,
                            Read(instance, "InfPath"));
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
            issues.Add(exception.Message);
        }
        return results.Values.ToArray();
    }

    private static ManufacturerDriverResult ManufacturerManaged(InstalledHardwareDriver driver)
    {
        var provider = driver.Provider;
        var uri = provider.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? IntelDsaHome :
                  provider.Contains("Razer", StringComparison.OrdinalIgnoreCase) ? RazerSupport :
                  provider.Contains("Realtek", StringComparison.OrdinalIgnoreCase) ||
                  provider.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) ? MsiBoardSupport :
                  provider.Contains("Dell", StringComparison.OrdinalIgnoreCase) ||
                  provider.Contains("Alienware", StringComparison.OrdinalIgnoreCase) ? DellDrivers : null;
        return new ManufacturerDriverResult(
            driver,
            ManufacturerDriverStatus.ManufacturerManaged,
            null,
            provider,
            uri,
            uri is null
                ? "No independently verifiable manufacturer catalog adapter is available for this driver yet."
                : "This driver is inventoried and linked to its official manufacturer update channel.",
            null);
    }

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

    private static string? NvidiaSeriesId(string deviceName) =>
        NvidiaRtx50Regex().IsMatch(deviceName) ? "120" : null;

    private static bool IsNvidiaDisplayDriver(InstalledHardwareDriver driver) =>
        driver.Provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) &&
        driver.DeviceName.Contains("GeForce", StringComparison.OrdinalIgnoreCase);

    private static bool IsMicrosoftProvider(string provider) =>
        provider.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ||
        provider.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase) ||
        provider.Equals("Windows", StringComparison.OrdinalIgnoreCase);

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
}
