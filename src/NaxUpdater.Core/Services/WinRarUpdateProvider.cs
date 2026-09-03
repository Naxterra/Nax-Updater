using NaxUpdater.Core.Models;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace NaxUpdater.Core.Services;

public sealed partial class WinRarUpdateProvider(HttpClient httpClient) : IUpdateProvider
{
    private static readonly Uri DownloadPage = new("https://www.rarlab.com/download.htm");

    public string Id => "rarlab-winrar";

    public bool CanHandle(InstalledApplication application) =>
        application.DisplayName.StartsWith("WinRAR", StringComparison.OrdinalIgnoreCase) &&
        (application.Publisher?.Contains("win.rar", StringComparison.OrdinalIgnoreCase) == true ||
         application.Publisher?.Contains("Alexander Roshal", StringComparison.OrdinalIgnoreCase) == true);

    public async Task<UpdateCheckResult> CheckAsync(
        InstalledApplication application,
        CancellationToken cancellationToken)
    {
        using var pageResponse = await httpClient.GetAsync(DownloadPage, cancellationToken);
        pageResponse.EnsureSuccessStatusCode();
        var finalPage = pageResponse.RequestMessage?.RequestUri ?? DownloadPage;
        EnsureRarLabUri(finalPage);
        var html = await pageResponse.Content.ReadAsStringAsync(cancellationToken);
        var language = DetectInstalledLanguage(application.PrimaryInstallPath);
        var release = SelectRelease(html, language) ?? SelectRelease(html, "en");
        if (release is null)
        {
            return Result(application, null, null, language, UpdateStatus.Error,
                "RARLAB's official download page did not expose a matching x64 release.");
        }

        var status = VersionOrder.Compare(release.Version, application.NormalizedVersion) > 0
            ? UpdateStatus.Available
            : UpdateStatus.Current;
        if (status == UpdateStatus.Current)
        {
            return Result(application, release.Version, null, language, status,
                "RARLAB's official download page reports the installed WinRAR release as current.");
        }

        using var installerResponse = await httpClient.GetAsync(release.DownloadUri, cancellationToken);
        installerResponse.EnsureSuccessStatusCode();
        EnsureRarLabUri(installerResponse.RequestMessage?.RequestUri ?? release.DownloadUri);
        var installerBytes = await installerResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        var sha256 = Convert.ToHexString(SHA256.HashData(installerBytes));
        var plan = new UpdateExecutionPlan(
            UpdateExecutionKind.DownloadedExe,
            release.DownloadUri,
            Path.GetFileName(release.DownloadUri.LocalPath),
            sha256,
            "win.rar GmbH",
            null,
            ["/S"],
            true,
            ["www.rarlab.com", "rarlab.com"],
            ["WinRAR"]);
        return Result(application, release.Version, plan, language, status,
            "The original RARLAB installer was hashed directly from the producer's HTTPS endpoint and must pass win.rar GmbH Authenticode verification before installation.");
    }

    internal static WinRarRelease? SelectRelease(string html, string language)
    {
        var desiredLabel = language.Equals("de", StringComparison.OrdinalIgnoreCase) ? "German" : "English";
        foreach (Match match in DownloadLinkRegex().Matches(html))
        {
            var label = WebUtility.HtmlDecode(StripTagsRegex().Replace(match.Groups["label"].Value, string.Empty)).Trim();
            if (!label.Equals(desiredLabel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value).Trim();
            if (!Uri.TryCreate(DownloadPage, href, out var downloadUri))
            {
                continue;
            }
            EnsureRarLabUri(downloadUri);
            var digits = VersionDigitsRegex().Match(downloadUri.LocalPath).Groups["digits"].Value;
            if (digits.Length < 3)
            {
                continue;
            }
            var version = $"{digits[..^2]}.{digits[^2..]}";
            return Version.TryParse(version, out _) ? new WinRarRelease(version, downloadUri) : null;
        }
        return null;
    }

    private static string DetectInstalledLanguage(string? primaryPath)
    {
        try
        {
            var directory = string.IsNullOrWhiteSpace(primaryPath)
                ? null
                : Directory.Exists(primaryPath) ? primaryPath : Path.GetDirectoryName(primaryPath);
            var languageFile = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, "winrar.lng");
            if (languageFile is not null && File.Exists(languageFile))
            {
                var header = string.Join('\n', File.ReadLines(languageFile).Take(12));
                if (header.Contains("Deutsche Übersetzung", StringComparison.OrdinalIgnoreCase))
                {
                    return "de";
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The UI culture remains the safe language fallback.
        }
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase)
            ? "de"
            : "en";
    }

    private static void EnsureRarLabUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("www.rarlab.com", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals("rarlab.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"RARLAB redirected to an untrusted endpoint '{uri.Host}'.");
        }
    }

    private UpdateCheckResult Result(
        InstalledApplication application,
        string? availableVersion,
        UpdateExecutionPlan? plan,
        string language,
        UpdateStatus status,
        string message) => new(
            application.Identity,
            application.DisplayName,
            application.NormalizedVersion,
            availableVersion,
            status,
            Id,
            "RARLAB official download",
            language,
            "Installed WinRAR language file",
            "x64",
            "stable",
            DownloadPage.ToString(),
            message,
            plan);

    [GeneratedRegex("(?is)<a[^>]+href=[\"'](?<href>[^\"']*winrar-x64-(?<digits>[0-9]+)[^\"']*\\.exe)[\"'][^>]*>(?<label>.*?)</a>", RegexOptions.CultureInvariant)]
    private static partial Regex DownloadLinkRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex StripTagsRegex();

    [GeneratedRegex("winrar-x64-(?<digits>[0-9]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionDigitsRegex();
}

internal sealed record WinRarRelease(string Version, Uri DownloadUri);
