using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

// The Nextcloud desktop client updates through Nextcloud's own update server,
// on the channel chosen in its settings (nextcloud.cfg updateChannel). The server
// reports versions in the numbering the MSI registers: 35.0.0-rc3 installs as
// 34.0.93, so GitHub's tag cannot be compared with the installed version. The
// installer itself is the server-referenced GitHub release asset, verified by
// GitHub's SHA-256 digest and the Nextcloud GmbH Authenticode signature.
public sealed partial class NextcloudUpdateProvider(HttpClient httpClient, Func<string?>? channelReader = null) : IUpdateProvider
{
    internal const string ProviderId = "nextcloud-client-updater";
    private const string Repository = "nextcloud-releases/desktop";
    private const string ExpectedSigner = "Nextcloud GmbH";
    private readonly Func<string?> _channelReader = channelReader ?? ReadConfiguredChannel;

    public string Id => ProviderId;
    public UpdateProviderDescriptor Descriptor { get; } = new(
        UpdateProviderAuthority.InstalledUpdateProtocol,
        100,
        "Nextcloud client update server on the channel configured in the client",
        [ManagementMode.Unmanaged, ManagementMode.Registry, ManagementMode.WindowsInstaller, ManagementMode.DirectVendor]);

    public bool CanHandle(InstalledApplication application) =>
        application.DisplayName.Equals("Nextcloud", StringComparison.OrdinalIgnoreCase) &&
        application.Publisher?.Contains("Nextcloud", StringComparison.OrdinalIgnoreCase) == true;

    public async Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        var channel = _channelReader() ?? "stable";
        if (string.IsNullOrWhiteSpace(application.NormalizedVersion))
            return Error(application, channel, "The installed Nextcloud version could not be read.");
        if (!string.Equals(InstalledApplicationMetadata.Architecture(application), "x64", StringComparison.OrdinalIgnoreCase))
            return Error(application, channel, "Nextcloud publishes its Windows MSI for x64 only; the installed architecture is not x64.");

        XElement? offer;
        try
        {
            var os = Environment.OSVersion.Version;
            var query = $"https://updates.nextcloud.org/client/?version={Uri.EscapeDataString(application.InstalledVersion ?? application.NormalizedVersion)}" +
                        $"&platform=win32&oem=Nextcloud&buildArch=x86_64&currentArch=x86_64&msi=true&channel={Uri.EscapeDataString(channel)}" +
                        $"&osRelease=windows&osVersion={os.Major}.{os.Minor}.{os.Build}&kernelVersion={os.Major}.{os.Minor}.{os.Build}";
            using var request = new HttpRequestMessage(HttpMethod.Get, query);
            request.Headers.UserAgent.ParseAdd("NaxUpdater/0.17.14");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var reader = XmlReader.Create(await response.Content.ReadAsStreamAsync(cancellationToken),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, Async = true });
            offer = (await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken)).Root;
        }
        catch (Exception exception) when (exception is HttpRequestException or XmlException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return Error(application, channel, $"Nextcloud's update server could not be read: {exception.Message}");
        }
        if (offer?.Name.LocalName != "owncloudclient")
            return Error(application, channel, "Nextcloud's update server returned an unexpected response.");

        var version = offer.Element("version")?.Value.Trim();
        var label = offer.Element("versionstring")?.Value.Trim();
        var download = offer.Element("downloadurl")?.Value.Trim();
        if (string.IsNullOrEmpty(version))
            return Result(application, channel, null, UpdateStatus.Current, null,
                $"Nextcloud's update server offers no newer client on the {channel} channel.", null);
        if (VersionOrder.Compare(version, application.NormalizedVersion) <= 0)
            return Result(application, channel, null, UpdateStatus.Current, null,
                $"Nextcloud's update server confirms the installed client is current on the {channel} channel.", null);

        var shown = string.IsNullOrEmpty(label) ? version : $"{label} ({version})";
        var asset = Uri.TryCreate(download, UriKind.Absolute, out var downloadUri) ? AssetPathRegex().Match(downloadUri.AbsolutePath) : Match.Empty;
        if (downloadUri?.Scheme != Uri.UriSchemeHttps || !downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !asset.Success)
            return Result(application, channel, version, UpdateStatus.NewerReleaseKnown, null,
                $"Nextcloud's update server offers {shown}, but not as a verifiable x64 MSI release asset.", null,
                UpdateApplicability.NotApplicable);
        var tag = Uri.UnescapeDataString(asset.Groups["tag"].Value);
        var assetName = Uri.UnescapeDataString(asset.Groups["asset"].Value);
        var releasePage = $"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}";

        string? sha256 = null;
        var json = await GitHubApiClient.ReadAsync(httpClient, $"repos/{Repository}/releases/tags/{Uri.EscapeDataString(tag)}", cancellationToken);
        if (json is not null)
        {
            try
            {
                using var release = JsonDocument.Parse(json);
                if (release.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                    foreach (var item in assets.EnumerateArray())
                        if (item.TryGetProperty("name", out var name) && name.GetString() == assetName &&
                            item.TryGetProperty("digest", out var digest) && digest.GetString() is { } value &&
                            value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && value.Length == 71)
                            sha256 = value[7..];
            }
            catch (JsonException) { }
        }
        if (sha256 is null)
            return Result(application, channel, version, UpdateStatus.NewerReleaseKnown, releasePage,
                $"Nextcloud's update server offers {shown}, but GitHub publishes no SHA-256 digest for {assetName} yet.", null,
                UpdateApplicability.NotApplicable, UpdateAvailabilityReason.AwaitingReleaseVerification);
        if (application.Scope != InstallScope.Machine)
            return Result(application, channel, version, UpdateStatus.NewerReleaseKnown, releasePage,
                "The Nextcloud MSI installs machine-wide and does not preserve this installation's scope.", null,
                UpdateApplicability.NotApplicable);

        var plan = new UpdateExecutionPlan(UpdateExecutionKind.DownloadedMsi, downloadUri, assetName, sha256, ExpectedSigner, null,
            ["/qn", "/norestart"], true,
            ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com", "github-releases.githubusercontent.com"],
            ["nextcloud"]);
        return Result(application, channel, version, UpdateStatus.Available, releasePage,
            $"Nextcloud's update server offers {shown} on the {channel} channel; the GitHub asset SHA-256 and the Nextcloud GmbH signature are verified before installation.",
            plan);
    }

    // nextcloud.cfg is an INI file in the user's roaming profile; [General]
    // updateChannel is absent until the user picks a non-default channel.
    internal static string? ReadConfiguredChannel()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nextcloud", "nextcloud.cfg");
            return File.Exists(path) ? ParseChannel(File.ReadLines(path)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static string? ParseChannel(IEnumerable<string> lines)
    {
        var general = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { general = line.Equals("[General]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!general || !line.StartsWith("updateChannel=", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line["updateChannel=".Length..].Trim().ToLowerInvariant();
            return ChannelRegex().IsMatch(value) ? value : null;
        }
        return null;
    }

    private UpdateCheckResult Result(InstalledApplication application, string channel, string? available, UpdateStatus status,
        string? releasePage, string message, UpdateExecutionPlan? plan,
        UpdateApplicability applicability = UpdateApplicability.Applicable,
        UpdateAvailabilityReason reason = UpdateAvailabilityReason.None) => new(
        application.Identity, application.DisplayName, application.NormalizedVersion, available, status, Id,
        "Nextcloud client update server", "neutral", "Vendor multi-language installer", "x64", channel,
        releasePage, message, plan, Applicability: applicability, AvailabilityReason: reason);

    private UpdateCheckResult Error(InstalledApplication application, string channel, string message) =>
        Result(application, channel, null, UpdateStatus.Error, null, message, null);

    [GeneratedRegex(@"^/nextcloud-releases/desktop/releases/download/(?<tag>[^/]+)/(?<asset>Nextcloud-[^/]+-x64\.msi)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AssetPathRegex();

    [GeneratedRegex("^[a-z]{1,16}$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelRegex();
}
