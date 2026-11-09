using System.Net;
using System.Text.RegularExpressions;
using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

public sealed partial class ManufacturerDriverService
{
    // These are Intel's own alternate-language pages for the same download record.
    // Do not turn a blocked English page into a cached or third-party answer.
    private async Task<IntelRelease> ReadIntelReleaseAsync(Uri page, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(RequestTimeout);
        var id = Regex.Match(page.AbsolutePath, @"/download/(?<id>\d+)/").Groups["id"].Value;
        var regional = new Uri("https://www.intel.cn" + page.AbsolutePath.Replace("/www/us/en/", "/www/cn/zh/", StringComparison.Ordinal));
        Exception? last = null;
        foreach (var source in new[] { page, regional })
        {
            try
            {
                var html = await GetStringAsync(source, deadline.Token);
                var version = IntelReleaseVersion(html, id);
                if (version is null) throw new InvalidDataException("Intel's response did not identify the requested download and its release version.");
                return new(version, html, source);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { throw new TimeoutException($"{page.Host} did not respond within {RequestTimeout.TotalSeconds:0} seconds."); }
            catch (Exception exception) { last = exception; }
        }
        // A locally blocked regional site must not require weakening TLS or DNS
        // policy. Read the original, allowlisted main-site page in the normal browser
        // runtime, then require the same exact download-record metadata.
        try
        {
            var html = await ReadManufacturerBrowserAsync(page, body => IntelReleaseVersion(body, id) is not null, deadline.Token);
            return new(IntelReleaseVersion(html, id)!, html, page);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { last = exception; }
        throw new InvalidDataException("Intel's current release could not be verified from its official download pages.", last);
    }

    internal static string? IntelReleaseVersion(string html, string downloadId)
    {
        var tags = Regex.Matches(html, @"<meta\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        string? version = null;
        var identity = false;
        foreach (Match tag in tags)
        {
            var attributes = Regex.Matches(tag.Value, "(?<key>[a-zA-Z:]+)\\s*=\\s*[\"'](?<value>[^\"']*)[\"']")
                .Cast<Match>().ToDictionary(m => m.Groups["key"].Value, m => WebUtility.HtmlDecode(m.Groups["value"].Value), StringComparer.OrdinalIgnoreCase);
            if (!attributes.TryGetValue("content", out var content)) continue;
            if (attributes.TryGetValue("name", out var name) && name.Equals("DownloadVersion", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(content, @"^\d+(?:\.\d+){1,4}$")) version = content;
            if (attributes.TryGetValue("property", out var property) && property.Equals("og:url", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(content, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
                uri.Host is "www.intel.com" or "www.intel.cn" &&
                Regex.IsMatch(uri.AbsolutePath, @"/download/" + Regex.Escape(downloadId) + @"/")) identity = true;
        }
        return identity ? version : null;
    }

    internal async Task<ManufacturerDriverResult> CheckIntelManagementEngineAsync(InstalledHardwareDriver driver, CancellationToken token)
    {
        try
        {
            var release = await ReadIntelReleaseAsync(IntelManagementEngine, token);
            return OfficialSourceOnly(driver, "Intel Management Engine components", release.Source, release.Version,
                $"Intel's live Management Engine catalog lists package {release.Version}. Individual component INF versions cannot be compared with this package version; motherboard-specific applicability remains with the OEM.")
                with { CheckEvidence = ManufacturerCheckEvidence.LiveCatalog };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { return Error(driver, "Intel Management Engine components", IntelManagementEngine, exception.Message); }
    }

    internal async Task<ManufacturerDriverResult> CheckIntelRstAsync(InstalledHardwareDriver driver, CancellationToken token)
    {
        try
        {
            var release = await ReadIntelReleaseAsync(IntelRapidStorageTenEleven, token);
            var reference = CheckIntelRst(driver);
            if (release.Version != "18.7.6.1010.3")
                return OfficialSourceOnly(driver, "Intel Rapid Storage Technology", release.Source, release.Version,
                    $"Intel's live RST catalog lists package {release.Version}. Its exact storage-controller INF applicability has not been verified, so no installation or current-version claim is made.")
                    with { CheckEvidence = ManufacturerCheckEvidence.LiveCatalog };
            if (reference.ExecutableUpdate?.ExecutionPlan is { Sha256: { } hash } && !release.Html.Contains(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Intel's current RST package hash did not match the verified installer.");
            var comparable = NumericVersionRegex().IsMatch(driver.InstalledVersion) && driver.HardwareId is not null &&
                Regex.IsMatch(driver.HardwareId, @"VEN_8086&DEV_06D[0-9A-F]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return reference with
            {
                CheckEvidence = ManufacturerCheckEvidence.LiveCatalog,
                SourceUri = release.Source,
                Status = comparable && reference.Status == ManufacturerDriverStatus.OfficialSourceOnly ? ManufacturerDriverStatus.Current : reference.Status,
                Message = comparable
                    ? $"Intel's live RST catalog lists package {release.Version} with driver 18.7.6.1010 for this controller; installed is {driver.InstalledVersion}."
                    : reference.Message
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { return Error(driver, "Intel Rapid Storage Technology", IntelRapidStorageTenEleven, exception.Message); }
    }

    private sealed record IntelRelease(string Version, string Html, Uri Source);
}
