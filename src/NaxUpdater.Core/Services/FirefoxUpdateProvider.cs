using NaxUpdater.Core.Models;
using System.Text.Json;

namespace NaxUpdater.Core.Services;

public sealed class FirefoxUpdateProvider(
    HttpClient httpClient,
    FirefoxMetadataDetector metadataDetector) : IUpdateProvider
{
    private const string VersionsUri = "https://product-details.mozilla.org/1.0/firefox_versions.json";
    private const string ArchiveRoot = "https://archive.mozilla.org/pub/firefox/releases";

    public string Id => "mozilla-firefox";

    public bool CanHandle(InstalledApplication application) =>
        application.DisplayName.StartsWith("Mozilla Firefox", StringComparison.OrdinalIgnoreCase) &&
        application.Publisher?.Contains("Mozilla", StringComparison.OrdinalIgnoreCase) == true;

    public async Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        var profile = metadataDetector.Detect(application);
        if (profile.EffectiveLanguage == "unknown" || profile.Architecture == "unknown")
        {
            return Error(application, profile, "Firefox language or architecture could not be verified; no installer was selected.");
        }

        var versionKey = profile.Channel.ToLowerInvariant() switch
        {
            "release" => "LATEST_FIREFOX_VERSION",
            "esr" => "FIREFOX_ESR",
            _ => null
        };
        if (versionKey is null)
        {
            return new UpdateCheckResult(
                application.Identity,
                application.DisplayName,
                application.NormalizedVersion,
                null,
                UpdateStatus.ManagedExternally,
                Id,
                "Mozilla native updater",
                profile.EffectiveLanguage,
                profile.LanguageSource,
                profile.Architecture,
                profile.Channel,
                null,
                $"The {profile.Channel} channel is left with Firefox's native updater.",
                null);
        }

        using var versionsDocument = JsonDocument.Parse(await httpClient.GetStringAsync(VersionsUri, cancellationToken));
        if (!versionsDocument.RootElement.TryGetProperty(versionKey, out var latestValue) ||
            string.IsNullOrWhiteSpace(latestValue.GetString()))
        {
            return Error(application, profile, $"Mozilla did not report a version for channel {profile.Channel}.");
        }
        var latestVersion = latestValue.GetString()!;
        var platformDirectory = profile.Architecture switch
        {
            "x64" => "win64",
            "x86" => "win32",
            "arm64" => "win64-aarch64",
            _ => string.Empty
        };
        if (platformDirectory.Length == 0)
        {
            return Error(application, profile, $"Firefox architecture {profile.Architecture} is unsupported.");
        }

        var fileName = $"Firefox Setup {latestVersion}.exe";
        var relativePath = $"{platformDirectory}/{profile.EffectiveLanguage}/{fileName}";
        var checksumText = await httpClient.GetStringAsync($"{ArchiveRoot}/{Uri.EscapeDataString(latestVersion)}/SHA256SUMS", cancellationToken);
        var sha256 = FindChecksum(checksumText, relativePath);
        if (sha256 is null)
        {
            return Error(
                application,
                profile,
                $"Mozilla does not publish a {profile.Architecture}/{profile.EffectiveLanguage} installer for {latestVersion}; English fallback is disabled.",
                latestVersion);
        }

        var downloadUri = new Uri(
            $"{ArchiveRoot}/{Uri.EscapeDataString(latestVersion)}/{platformDirectory}/{Uri.EscapeDataString(profile.EffectiveLanguage)}/{Uri.EscapeDataString(fileName)}");
        var status = VersionOrder.Compare(latestVersion, application.NormalizedVersion) > 0
            ? UpdateStatus.Available
            : UpdateStatus.Current;
        var messageParts = new[]
        {
            profile.Warning,
            $"Mozilla SHA-256 verified source: {relativePath}."
        }.Where(static value => !string.IsNullOrWhiteSpace(value));
        var plan = status == UpdateStatus.Available
            ? new UpdateExecutionPlan(
                UpdateExecutionKind.DownloadedExe,
                downloadUri,
                fileName,
                sha256,
                "Mozilla Corporation",
                null,
                ["/S", $"/InstallDirectoryPath={profile.InstallDirectory}"],
                application.Scope == InstallScope.Machine,
                ["archive.mozilla.org"],
                ["firefox"])
            : null;

        return new UpdateCheckResult(
            application.Identity,
            application.DisplayName,
            application.NormalizedVersion,
            latestVersion,
            status,
            Id,
            "Mozilla official release archive",
            profile.EffectiveLanguage,
            profile.LanguageSource,
            profile.Architecture,
            profile.Channel,
            $"https://www.mozilla.org/firefox/{latestVersion}/releasenotes/",
            string.Join(' ', messageParts),
            plan);
    }

    public static string? FindChecksum(string checksumText, string relativePath)
    {
        foreach (var rawLine in checksumText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.EndsWith(relativePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var separator = line.IndexOfAny([' ', '\t']);
            var hash = separator > 0 ? line[..separator] : string.Empty;
            if (hash.Length == 64 && hash.All(Uri.IsHexDigit))
            {
                return hash.ToLowerInvariant();
            }
        }
        return null;
    }

    private UpdateCheckResult Error(
        InstalledApplication application,
        FirefoxInstallProfile profile,
        string message,
        string? availableVersion = null) => new(
            application.Identity,
            application.DisplayName,
            application.NormalizedVersion,
            availableVersion,
            UpdateStatus.Error,
            Id,
            "Mozilla official release archive",
            profile.EffectiveLanguage,
            profile.LanguageSource,
            profile.Architecture,
            profile.Channel,
            null,
            message,
            null);
}
