using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

public sealed partial class ManufacturerDriverService
{
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _browserPages = new(StringComparer.Ordinal);
    internal Func<Uri, CancellationToken, Task<string>>? BrowserReader { get; init; }

    private async Task<string> ReadManufacturerSourceAsync(Uri source, Func<string, bool> validate, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(RequestTimeout);
        try
        {
            var body = await GetStringAsync(source, deadline.Token);
            if (validate(body)) return body;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { }
        deadline.Token.ThrowIfCancellationRequested();
        return await ReadManufacturerBrowserAsync(source, validate, deadline.Token);
    }

    private async Task<string> ReadManufacturerBrowserAsync(Uri source, Func<string, bool> validate, CancellationToken token)
    {
        var page = _browserPages.GetOrAdd(source.AbsoluteUri, _ => new Lazy<Task<string>>(() =>
            BrowserReader is { } read ? read(source, token) : ManufacturerSourceQuery.ReadAsync(source,
                manufacturerBrowserExecutable ?? Path.Combine(AppContext.BaseDirectory, "Nax-Updater.exe"), RequestTimeout, token)));
        var html = await page.Value.WaitAsync(token);
        if (!validate(html)) throw new InvalidDataException("The manufacturer response did not contain the requested catalog.");
        return html;
    }

    private static string JsonPageBody(string body)
    {
        if (body.TrimStart().StartsWith('{')) return body;
        var pre = Regex.Match(body, @"<pre\b[^>]*>(?<json>[\s\S]*?)</pre>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return pre.Success ? WebUtility.HtmlDecode(pre.Groups["json"].Value) : "";
    }

    internal static IReadOnlyList<MsiDriverRelease> MsiDriverReleases(string body)
    {
        using var json = JsonDocument.Parse(JsonPageBody(body));
        var root = json.RootElement;
        if (root.GetProperty("status").GetProperty("code").GetInt32() != 200) throw new InvalidDataException("MSI's driver catalog did not succeed.");
        var result = root.GetProperty("result");
        if (result.GetProperty("title").GetString() != "MAG Z490 TOMAHAWK" || result.GetProperty("category").GetString() != "mb")
            throw new InvalidDataException("MSI's catalog did not identify the exact motherboard.");
        var releases = new List<MsiDriverRelease>();
        foreach (var category in result.GetProperty("downloads").EnumerateObject())
        {
            if (category.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var entry in category.Value.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                string Value(string key) => entry.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                var version = Value("download_version");
                var hash = Regex.Match(Value("download_sha256"), @"\b[0-9a-fA-F]{64}\b").Value;
                if (!Value("os").Split(',').Select(s => s.Trim()).Contains("Win11 64", StringComparer.OrdinalIgnoreCase) ||
                    !NumericVersionRegex().IsMatch(version) || hash.Length != 64 ||
                    !Uri.TryCreate(Value("download_url"), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                    uri.Host != "download.msi.com" || !uri.AbsolutePath.StartsWith("/dvr_exe/mb/", StringComparison.Ordinal)) continue;
                releases.Add(new(Value("download_title"), version, uri, hash));
            }
        }
        if (releases.Count == 0) throw new InvalidDataException("MSI's catalog contained no verified Windows 11 driver records.");
        return releases;
    }

    private static bool IsMsiCatalog(string body)
    {
        try { return MsiDriverReleases(body).Count > 0; }
        catch (Exception) { return false; }
    }

    private static bool IsExactMsiBoard()
    {
        using var board = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        return board?.GetValue("BaseBoardProduct")?.ToString()?.Equals("MAG Z490 TOMAHAWK (MS-7C80)", StringComparison.OrdinalIgnoreCase) == true &&
               board.GetValue("BaseBoardManufacturer")?.ToString()?.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) == true;
    }

    internal async Task<ManufacturerDriverResult> CheckMsiBoardDriverAsync(InstalledHardwareDriver driver, CancellationToken token, bool? exactBoard = null)
    {
        if (!(exactBoard ?? IsExactMsiBoard()))
            return OfficialSourceOnly(driver, "MSI motherboard support", MsiBoardSupport,
                "The exact motherboard identity was not confirmed. No model-specific driver comparison is made.");
        try
        {
            var body = await ReadManufacturerSourceAsync(ManufacturerSourceQuery.MsiDrivers, IsMsiCatalog, token);
            var releases = MsiDriverReleases(body);
            var isAudio = driver.HardwareId?.Contains("VEN_10EC&DEV_0B00&SUBSYS_1462EC80", StringComparison.OrdinalIgnoreCase) == true;
            if (isAudio)
            {
                var audio = releases.Where(r => r.Title == "Realtek HD Universal Driver")
                    .OrderByDescending(r => r.Version, Comparer<string>.Create(VersionOrder.Compare)).FirstOrDefault()
                    ?? throw new InvalidDataException("MSI's exact audio package was missing from the motherboard catalog.");
                var inbox = IsMicrosoftProvider(driver.Provider);
                var available = inbox || VersionOrder.Compare(audio.Version, driver.InstalledVersion) > 0;
                var offer = available ? MsiAudioOffer(driver, audio) : null;
                return new(driver, inbox || VersionOrder.Compare(audio.Version, driver.InstalledVersion) > 0
                        ? ManufacturerDriverStatus.Available : ManufacturerDriverStatus.Current,
                    audio.Version, "MSI MAG Z490 TOMAHAWK audio", MsiBoardSupport,
                    inbox
                        ? $"MSI's live catalog offers Realtek audio package {audio.Version} for this exact motherboard. The Microsoft inbox audio driver is installed; its version scheme is different."
                        : $"MSI's live catalog lists Realtek audio driver {audio.Version}; installed is {driver.InstalledVersion}.",
                    offer, ManufacturerCheckEvidence.LiveCatalog);
            }
            return OfficialSourceOnly(driver, "MSI MAG Z490 TOMAHAWK", MsiBoardSupport,
                "The exact motherboard's live Windows 11 driver catalog was checked. It does not list a separate MSI Software Component Interface package; no current-version claim is made.")
                with { CheckEvidence = ManufacturerCheckEvidence.LiveCatalog };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { return Error(driver, "MSI MAG Z490 TOMAHAWK", MsiBoardSupport, exception.Message); }
    }

    internal static IReadOnlyList<Uri> DellDownloads(string html)
    {
        if (!html.Contains("AW3423DW", StringComparison.OrdinalIgnoreCase)) return [];
        return Regex.Matches(html, @"<a\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Cast<Match>().Where(m => Regex.IsMatch(m.Value, "\\bid=[\"']download-[^\"']+-PRODUCT-[A-Z0-9]+[\"']", RegexOptions.IgnoreCase))
            .Select(m => Regex.Match(m.Value, "\\bhref=[\"'](?<url>https://dl\\.dell\\.com/[^\"'<>]+)[\"']", RegexOptions.IgnoreCase))
            .Where(m => m.Success).Select(m => new Uri(WebUtility.HtmlDecode(m.Groups["url"].Value))).Distinct().ToArray();
    }

    internal async Task<ManufacturerDriverResult> CheckDellAw3423DwAsync(InstalledHardwareDriver driver, CancellationToken token)
    {
        try
        {
            var page = await ReadManufacturerSourceAsync(ManufacturerSourceQuery.DellDrivers, html => DellDownloads(html).Count > 0, token);
            var downloads = DellDownloads(page);
            if (driver.DeviceClass.Equals("Monitor", StringComparison.OrdinalIgnoreCase))
            {
                var monitor = downloads.Where(u => Regex.IsMatch(u.Segments[^1], @"AW3423DW-MONITOR_.*_DRVR_", RegexOptions.IgnoreCase)).ToArray();
                if (monitor.Length == 1 && monitor[0].Segments[^1] == "ALIENWARE_AW3423DW-MONITOR_A00-00_DRVR_YW4YR.exe")
                    return new(driver, VersionOrder.Compare("1.1.0.0", driver.InstalledVersion) > 0 ? ManufacturerDriverStatus.Available : ManufacturerDriverStatus.Current,
                        "1.1.0.0", "Dell AW3423DW (M46J9)", DellAw3423DwDriver,
                        "Dell's live AW3423DW catalog lists the verified M46J9 monitor INF release 1.1.0.0. Firmware and monitor-management utilities are separate packages.", null,
                        ManufacturerCheckEvidence.LiveCatalog);
            }
            var firmware = downloads.Select(u => Regex.Match(u.Segments[^1], @"AW3423DW_(?<version>M0B[0-9]+)\.zip$", RegexOptions.IgnoreCase))
                .Where(m => m.Success).Select(m => m.Groups["version"].Value).FirstOrDefault();
            return OfficialSourceOnly(driver, "Dell AW3423DW", DellAw3423DwSupport,
                firmware ?? "—", "Dell's live model-specific catalog was checked. Monitor firmware and HID interface driver versions are different; no false comparison or automatic firmware installation is made.")
                with { CheckEvidence = ManufacturerCheckEvidence.LiveCatalog };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { return Error(driver, "Dell AW3423DW", DellAw3423DwSupport, exception.Message); }
    }

    internal sealed record MsiDriverRelease(string Title, string Version, Uri Download, string Sha256);

    internal static IReadOnlyList<(string Product, string Version)> RazerFirmwareVersions(string html, IReadOnlyList<string> products)
    {
        var releases = new List<(string, string)>();
        foreach (var product in RazerFirmwareReleases.Where(r => products.Contains(r.Product, StringComparer.OrdinalIgnoreCase)))
        {
            var expected = Regex.Replace(product.CatalogName, @"\s+", " ").Replace("8 kHz", "8kHz", StringComparison.OrdinalIgnoreCase);
            foreach (Match anchor in Regex.Matches(html, @"<a\b[^>]*>(?<text>[\s\S]*?)</a>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                if (!Regex.IsMatch(anchor.Value, @"(?:https?:)?//mysupport\.razer\.com/app/answers/detail/a_id/\d+", RegexOptions.IgnoreCase)) continue;
                var text = Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(anchor.Groups["text"].Value, "<[^>]+>", " ")), @"\s+", " ").Trim()
                    .Replace("8 kHz", "8kHz", StringComparison.OrdinalIgnoreCase);
                if (!text.StartsWith(expected + " ", StringComparison.OrdinalIgnoreCase)) continue;
                var version = Regex.Match(text[expected.Length..], @"^(?:\s+Firmware|\s+Updater)*\s+v(?<version>\d+(?:\.\d+){1,3}(?:_[a-z]\d+)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (version.Success) { releases.Add((product.Product, "v" + version.Groups["version"].Value)); break; }
            }
        }
        return releases;
    }
}
