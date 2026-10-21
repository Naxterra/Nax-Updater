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
    public UpdateProviderDescriptor Descriptor { get; } = new(
        UpdateProviderAuthority.ProducerRelease,
        100,
        "Exact RARLAB product and publisher match against the producer download page",
        [ManagementMode.Unmanaged, ManagementMode.Registry, ManagementMode.WindowsInstaller, ManagementMode.DirectVendor]);

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
        // A beta installation follows the beta line as well as final releases;
        // a final installation is offered final releases only.
        var installedBeta = InstalledBeta(application.DisplayName);
        var release = SelectRelease(html, language, installedBeta is not null) ?? SelectRelease(html, "en", installedBeta is not null);
        if (release is null)
        {
            return Result(application, null, null, language, UpdateStatus.Error,
                "RARLAB's official download page did not expose a matching x64 release.");
        }

        var installedRank = Rank(application.NormalizedVersion, installedBeta);
        var status = installedRank is not null && release.Rank.CompareTo(installedRank.Value) > 0
            ? UpdateStatus.Available
            : UpdateStatus.Current;
        if (status == UpdateStatus.Current)
        {
            return Result(application, application.NormalizedVersion, null, language, status,
                $"RARLAB's official download page lists {release.Label} as its newest {(installedBeta is null ? "release" : "release or beta")}; the installed WinRAR is current.");
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

    // RARLAB labels links "English (64-bit x64)" (earlier just "English") and lists
    // betas ("winrar-x64-730b1.exe", 7.30 beta 1) before releases
    // ("winrar-x64-723.exe"). WinRAR registers a beta n as x.yy.n and a release as
    // x.yy.0, so a release ranks above every beta of the same version.
    internal static WinRarRelease? SelectRelease(string html, string language, bool includeBeta)
    {
        var desiredLabel = language.Equals("de", StringComparison.OrdinalIgnoreCase) ? "German" : "English";
        WinRarRelease? best = null;
        foreach (Match match in DownloadLinkRegex().Matches(html))
        {
            var label = WebUtility.HtmlDecode(StripTagsRegex().Replace(match.Groups["label"].Value, string.Empty)).Trim();
            var name = ArchitectureLabelRegex().Replace(label, string.Empty).Trim();
            if (!name.Equals(desiredLabel, StringComparison.OrdinalIgnoreCase) ||
                label.Contains("ARM", StringComparison.OrdinalIgnoreCase) || label.Contains("32-bit", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value).Trim();
            if (!Uri.TryCreate(DownloadPage, href, out var downloadUri))
            {
                continue;
            }
            EnsureRarLabUri(downloadUri);
            var file = VersionDigitsRegex().Match(downloadUri.LocalPath);
            var digits = file.Groups["digits"].Value;
            if (!file.Success || digits.Length < 3)
            {
                continue;
            }
            var beta = file.Groups["beta"].Success ? int.Parse(file.Groups["beta"].Value, CultureInfo.InvariantCulture) : (int?)null;
            if (beta is not null && !includeBeta)
            {
                continue;
            }
            var version = $"{digits[..^2]}.{digits[^2..]}";
            if (Rank(version, beta) is not { } rank)
            {
                continue;
            }
            var release = new WinRarRelease($"{version}.{beta ?? 0}", downloadUri, rank,
                beta is null ? version : $"{version} beta {beta}");
            if (best is null || release.Rank.CompareTo(best.Rank) > 0)
            {
                best = release;
            }
        }
        return best;
    }

    // "WinRAR 7.30 Beta 1 (64-Bit)" -> 1; null for a release.
    internal static int? InstalledBeta(string displayName) =>
        InstalledBetaRegex().Match(displayName) is { Success: true } match ? int.Parse(match.Groups["beta"].Value, CultureInfo.InvariantCulture) : null;

    // Orders x.yy betas below the x.yy release. An installed beta n registers as x.yy.n.
    internal static (Version Version, int Stage)? Rank(string? version, int? beta)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var parts = version.Split('.');
        if (parts.Length < 2 || !Version.TryParse($"{parts[0]}.{parts[1]}", out var baseVersion)) return null;
        return (baseVersion, beta ?? int.MaxValue);
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

    [GeneratedRegex("winrar-x64-(?<digits>[0-9]+)(?:b(?<beta>[0-9]+))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionDigitsRegex();

    [GeneratedRegex(@"\s*\([^)]*\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ArchitectureLabelRegex();

    [GeneratedRegex(@"\bbeta\s*(?<beta>[0-9]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstalledBetaRegex();
}

// Version is the form WinRAR registers (7.23.0, 7.30.1 for beta 1); Label is the
// page's own wording for messages.
internal sealed record WinRarRelease(string Version, Uri DownloadUri, (Version Version, int Stage) Rank, string Label);
