using NaxUpdater.Core.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NaxUpdater.Core.Services;

public sealed class IvpnUpdateProvider : IUpdateProvider
{
    private static readonly Uri ManualFeedUri = new("https://repo.ivpn.net/windows/update_manual.json");
    private static readonly Uri ManualFeedSignatureUri = new("https://repo.ivpn.net/windows/update_manual.json.sign.sha256.base64");
    private const string ExpectedSigner = "IVPN Limited";
    private const string OfficialPublicKey = """
        -----BEGIN PUBLIC KEY-----
        MIICIjANBgkqhkiG9w0BAQEFAAOCAg8AMIICCgKCAgEA1m7vr8rY10V1ZDIxsP6g
        Bhq+QYRGNt+33NA0+/MUpxioi2t6sfua0ql6Pxs+Q5x10C/Sx8vNlcagOHwXOS6W
        YNnLsqEOHCxgd0M5thEdT5KXjJEbpzjjrTmk2HuD2cnqmI5b9wCYx5GzREMguCAU
        or+PCUEV/TWittG1DYAW3evPUy3VIMer+Oq6L0jLFSDpfGlXBBKmqZwX3nRuzSaI
        iS0qfs39FipVEyuX/ZKNHXx7mFG73RqhU1V6m3dFEdwrMGEqq9rHc/XUXZKMgiwO
        Wvr7qfCXFoYYcYdseQg1g/8MP6ur0WctMfK5PC36MJlSq/gy/W/gRiIrQMCYMHnB
        0yRrGXvm1n8483y0YVorz2WcGt4cal4bCEnOuYam+SOjD+XM81FIXJnlUFpehXbA
        ZNxgu/5woENBPavCkgK0z+d+CdPdF6WAO6mzytAakLyDffOBblVpGouyYr78LhF3
        DfEQSV06n6dAYFyIyxR/jET24MrWwM3KCXTQAyPV1v2eKaMJoh8JMf+4dEVde5om
        LopbFeMGb9xFxQmedNqtBb/DYBcgEh/Fa3s9r+V/8Fq6ULzjeyejC4VMnc8KCST9
        mX57qSlQ3sj9GG7wlW5TvGUnpJ6vuTj50S6ZXfYe7VuvBM9gxtOhJVPwA5Uy/RzX
        C6HXQqBJNLEOqq2b/+q9fHECAwEAAQ==
        -----END PUBLIC KEY-----
        """;

    private readonly HttpClient _httpClient;
    private readonly string _publicKeyPem;
    private readonly Func<string, CancellationToken, Task<string?>>? _cliReader;

    public IvpnUpdateProvider(HttpClient httpClient) : this(httpClient, OfficialPublicKey)
    {
    }

    internal IvpnUpdateProvider(HttpClient httpClient, string publicKeyPem,
        Func<string, CancellationToken, Task<string?>>? cliReader = null)
    {
        _httpClient = httpClient;
        _publicKeyPem = publicKeyPem;
        _cliReader = cliReader;
    }

    public string Id => "ivpn-signed-manual-feed";
    public UpdateProviderDescriptor Descriptor { get; } = new(
        UpdateProviderAuthority.InstalledUpdateProtocol,
        95,
        "IVPN's producer-signed manual update feed and official release hash",
        [ManagementMode.Unmanaged, ManagementMode.Registry, ManagementMode.WindowsInstaller, ManagementMode.DirectVendor]);

    public bool CanHandle(InstalledApplication application) =>
        application.DisplayName.Equals("IVPN Client", StringComparison.OrdinalIgnoreCase) &&
        application.Publisher?.Contains("IVPN", StringComparison.OrdinalIgnoreCase) == true;

    public async Task<UpdateCheckResult> CheckAsync(
        InstalledApplication application,
        CancellationToken cancellationToken)
    {
        byte[] feed;
        byte[] signatureText;
        try
        {
            feed = await GetBytesAsync(ManualFeedUri, cancellationToken);
            signatureText = await GetBytesAsync(ManualFeedSignatureUri, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return Error(application, $"IVPN's signed manual update feed could not be read: {exception.Message}");
        }

        if (!VerifySignature(feed, signatureText, _publicKeyPem))
        {
            return Error(application, "IVPN's manual update metadata failed verification against the public key pinned by IVPN's desktop client.");
        }

        IvpnRelease? release;
        try
        {
            release = ParseRelease(feed);
        }
        catch (JsonException exception)
        {
            return Error(application, $"IVPN's signed update metadata was malformed: {exception.Message}");
        }
        if (release is null || !IsExpectedDownload(release.Version, release.DownloadUri, release.SignatureUri))
        {
            return Error(application, "IVPN's signed update metadata did not contain the exact Windows x64 version, download, and detached-signature URLs.");
        }

        var releasePage = $"https://github.com/ivpn/desktop-app/releases/tag/v{Uri.EscapeDataString(release.Version)}";
        if (VersionOrder.Compare(release.Version, application.NormalizedVersion) <= 0)
        {
            return Result(
                application,
                release.Version,
                UpdateStatus.Current,
                releasePage,
                "IVPN's producer-signed manual update feed confirms the installed version is current.",
                null);
        }

        var uiExecutable = ResolveUiExecutable(application);
        var architecture = NodeJsUpdateProvider.DetectArchitecture(uiExecutable);
        if (architecture is not ("x64" or "arm64") || application.Scope != InstallScope.Machine)
            return Result(application, release.Version, UpdateStatus.NewerReleaseKnown, releasePage,
                "IVPN has a newer release, but the installed architecture or scope has no matching supported installer.",
                null, UpdateApplicability.NotApplicable);
        if (architecture == "arm64")
        {
            var uri = new Uri($"https://repo.ivpn.net/windows/bin/IVPN-Client-v{release.Version}-arm64.exe");
            release = release with { DownloadUri = uri, SignatureUri = new Uri(uri.AbsoluteUri + ".sign.sha256.base64") };
        }
        var hash = await ReadOfficialReleaseHashAsync(release, cancellationToken);
        if (hash is null)
        {
            return Result(
                application,
                release.Version,
                UpdateStatus.Error,
                releasePage,
                "IVPN's signed manual feed reports a newer version, but the official release hash could not be retrieved. Retry the source check.",
                null,
                UpdateApplicability.Unknown);
        }

        if (uiExecutable is null)
        {
            return Result(
                application,
                release.Version,
                UpdateStatus.NewerReleaseKnown,
                releasePage,
                "IVPN's signed manual feed reports a newer version, but the installed IVPN UI executable could not be bound for safe process shutdown; automatic installation is blocked.",
                null,
                UpdateApplicability.NotApplicable);
        }

        var plan = new UpdateExecutionPlan(
            UpdateExecutionKind.DownloadedExe,
            release.DownloadUri,
            Path.GetFileName(release.DownloadUri.LocalPath),
            hash,
            ExpectedSigner,
            null,
            ["/S"],
            true,
            ["repo.ivpn.net"],
            ["IVPN Client"],
            RunningExecutablePaths: [uiExecutable]);
        return Result(
            application,
            release.Version,
            UpdateStatus.Available,
            releasePage,
            "IVPN's signed manual feed, official release SHA-256, and Authenticode publisher are all required before installation.",
            plan) with
        { Architecture = architecture };
    }

    private async Task<byte[]> GetBytesAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("NaxUpdater/0.16.10");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private async Task<string?> ReadOfficialReleaseHashAsync(IvpnRelease release, CancellationToken cancellationToken)
    {
        try
        {
            var json = await GitHubApiClient.ReadAsync(_httpClient,
                $"repos/ivpn/desktop-app/releases/tags/v{Uri.EscapeDataString(release.Version)}",
                cancellationToken, _cliReader);
            if (json is null) return null;
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean() ||
                !root.GetProperty("tag_name").GetString()!.Equals($"v{release.Version}", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            var body = root.GetProperty("body").GetString() ?? string.Empty;
            var pattern = $@"\]\({Regex.Escape(release.DownloadUri.AbsoluteUri)}\)\s+SHA256:\s*(?<hash>[0-9a-f]{{64}})";
            var match = Regex.Match(body, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success ? match.Groups["hash"].Value.ToUpperInvariant() : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    internal static bool VerifySignature(byte[] data, byte[] base64Signature, string publicKeyPem)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            var signature = Convert.FromBase64String(Encoding.ASCII.GetString(base64Signature).Trim());
            return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    internal static IvpnRelease? ParseRelease(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var generic = document.RootElement.GetProperty("generic");
        var version = generic.GetProperty("version").GetString();
        var download = generic.GetProperty("downloadLink").GetString();
        var signature = generic.GetProperty("signature").GetString();
        return !string.IsNullOrWhiteSpace(version) &&
               Uri.TryCreate(download, UriKind.Absolute, out var downloadUri) &&
               Uri.TryCreate(signature, UriKind.Absolute, out var signatureUri)
            ? new IvpnRelease(version.Trim(), downloadUri, signatureUri)
            : null;
    }

    internal static bool IsExpectedDownload(string version, Uri downloadUri, Uri signatureUri)
    {
        var expectedPath = $"/windows/bin/IVPN-Client-v{version}.exe";
        return downloadUri.Scheme == Uri.UriSchemeHttps &&
               downloadUri.Host.Equals("repo.ivpn.net", StringComparison.OrdinalIgnoreCase) &&
               downloadUri.AbsolutePath.Equals(expectedPath, StringComparison.Ordinal) &&
               string.IsNullOrEmpty(downloadUri.Query) &&
               signatureUri.Scheme == Uri.UriSchemeHttps &&
               signatureUri.Host.Equals("repo.ivpn.net", StringComparison.OrdinalIgnoreCase) &&
               signatureUri.AbsolutePath.Equals(expectedPath + ".sign.sha256.base64", StringComparison.Ordinal) &&
               string.IsNullOrEmpty(signatureUri.Query);
    }

    internal static string? ResolveUiExecutable(InstalledApplication application)
    {
        try
        {
            if (application.PrimaryInstallPath is { } path && File.Exists(path) &&
                Path.GetFileName(path).Equals("IVPN Client.exe", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(path);
            var root = string.IsNullOrWhiteSpace(application.PrimaryInstallPath)
                ? null
                : Directory.Exists(application.PrimaryInstallPath)
                    ? application.PrimaryInstallPath
                    : Path.GetDirectoryName(application.PrimaryInstallPath);
            var candidate = string.IsNullOrWhiteSpace(root)
                ? null
                : Path.Combine(root, "ui", "IVPN Client.exe");
            return candidate is not null && File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private UpdateCheckResult Result(
        InstalledApplication application,
        string version,
        UpdateStatus status,
        string releasePage,
        string message,
        UpdateExecutionPlan? plan,
        UpdateApplicability applicability = UpdateApplicability.Applicable) => new(
        application.Identity,
        application.DisplayName,
        application.NormalizedVersion,
        version,
        status,
        Id,
        "IVPN signed manual update feed",
        "neutral",
        "Producer-signed multi-language Windows installer",
        "x64",
        "stable-manual",
        releasePage,
        message,
        plan,
        Applicability: applicability);

    private UpdateCheckResult Error(InstalledApplication application, string message) => new(
        application.Identity,
        application.DisplayName,
        application.NormalizedVersion,
        null,
        UpdateStatus.Error,
        Id,
        "IVPN signed manual update feed",
        "neutral",
        "Producer-signed update metadata",
        "x64",
        "stable-manual",
        "https://github.com/ivpn/desktop-app/releases",
        message,
        null,
        Applicability: UpdateApplicability.Unknown);

    internal sealed record IvpnRelease(string Version, Uri DownloadUri, Uri SignatureUri);
}
