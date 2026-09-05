using NaxUpdater.Core.Models;
using System.Text.Json;

namespace NaxUpdater.Core.Services;

public sealed class NodeJsUpdateProvider(HttpClient httpClient) : IUpdateProvider
{
    private static readonly Uri ReleaseIndexUri = new("https://nodejs.org/dist/index.json");
    private const string ExpectedSigner = "OpenJS Foundation";

    public string Id => "nodejs-official-dist";
    public UpdateProviderDescriptor Descriptor { get; } = new(
        UpdateProviderAuthority.ProducerRelease,
        95,
        "Official Node.js distribution index with producer-published MSI SHA-256",
        [ManagementMode.Unmanaged, ManagementMode.Registry, ManagementMode.WindowsInstaller, ManagementMode.DirectVendor]);

    public bool CanHandle(InstalledApplication application) =>
        application.DisplayName.Equals("Node.js", StringComparison.OrdinalIgnoreCase) &&
        (application.Publisher?.Contains("Node.js", StringComparison.OrdinalIgnoreCase) == true ||
         application.Publisher?.Contains("OpenJS", StringComparison.OrdinalIgnoreCase) == true);

    public async Task<UpdateCheckResult> CheckAsync(
        InstalledApplication application,
        CancellationToken cancellationToken)
    {
        if (!TryMajor(application.NormalizedVersion, out var installedMajor))
        {
            return Error(application, "The installed Node.js major version could not be established, so its release line cannot be preserved.");
        }
        var architecture = DetectArchitecture(application.PrimaryInstallPath);
        if (architecture is null)
        {
            return Error(application, "The installed Node.js executable architecture could not be verified; an installer architecture will not be guessed.");
        }
        var catalogFile = $"win-{architecture}-msi";

        NodeRelease? release;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseIndexUri);
            request.Headers.UserAgent.ParseAdd("NaxUpdater/0.16.7");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            release = ReadLatestCompatibleRelease(document.RootElement, installedMajor, catalogFile);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return Error(application, $"The official Node.js distribution index could not be read: {exception.Message}");
        }
        if (release is null)
        {
            return Error(application, $"The official Node.js distribution index has no Windows {architecture} MSI on the installed major {installedMajor} release line.");
        }

        var releaseNotes = $"https://nodejs.org/en/blog/release/v{Uri.EscapeDataString(release.Version)}";
        if (VersionOrder.Compare(release.Version, application.NormalizedVersion) <= 0)
        {
            return Result(
                application,
                release,
                UpdateStatus.Current,
                releaseNotes,
                "The official Node.js distribution index confirms the installed release line is current.",
                null,
                architecture);
        }

        var installerName = $"node-v{release.Version}-{architecture}.msi";
        if (application.Scope != InstallScope.Machine)
            return Result(application, release, UpdateStatus.NewerReleaseKnown, releaseNotes,
                "The official Node.js MSI installs machine-wide and does not preserve this installation's scope.",
                null, architecture, UpdateApplicability.NotApplicable);
        var installerUri = new Uri($"https://nodejs.org/dist/v{release.Version}/{installerName}");
        var hash = await ReadInstallerHashAsync(release.Version, installerName, cancellationToken);
        if (hash is null)
        {
            return Result(
                application,
                release,
                UpdateStatus.NewerReleaseKnown,
                releaseNotes,
                "A newer Node.js release exists on the installed major line, but its producer-published MSI SHA-256 could not be verified; automatic installation is blocked.",
                null,
                architecture,
                UpdateApplicability.NotApplicable);
        }

        var plan = new UpdateExecutionPlan(
            UpdateExecutionKind.DownloadedMsi,
            installerUri,
            installerName,
            hash,
            ExpectedSigner,
            null,
            ["/qn", "/norestart"],
            true,
            ["nodejs.org"],
            ["node"]);
        return Result(
            application,
            release,
            UpdateStatus.Available,
            releaseNotes,
            "The official Node.js MSI hash and OpenJS Foundation Authenticode signature are required before installation.",
            plan,
            architecture);
    }

    internal static NodeRelease? ReadLatestCompatibleRelease(JsonElement releases, int installedMajor, string requiredFile)
    {
        if (releases.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var candidates = new List<NodeRelease>();
        foreach (var item in releases.EnumerateArray())
        {
            var rawVersion = item.TryGetProperty("version", out var versionValue)
                ? versionValue.GetString()?.TrimStart('v', 'V')
                : null;
            if (!TryMajor(rawVersion, out var major) || major != installedMajor ||
                !item.TryGetProperty("files", out var files) ||
                files.ValueKind != JsonValueKind.Array ||
                !files.EnumerateArray().Any(file =>
                    file.GetString()?.Equals(requiredFile, StringComparison.OrdinalIgnoreCase) == true))
            {
                continue;
            }
            var lts = item.TryGetProperty("lts", out var ltsValue) && ltsValue.ValueKind == JsonValueKind.String
                ? ltsValue.GetString()
                : null;
            candidates.Add(new NodeRelease(rawVersion!, lts));
        }
        return candidates.OrderByDescending(static release => release.Version, Comparer<string>.Create(VersionOrder.Compare)).FirstOrDefault();
    }

    internal static string? ParseSha256(string contents, string installerName)
    {
        foreach (var line in contents.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit) &&
                parts[1].TrimStart('*').Equals(installerName, StringComparison.Ordinal))
            {
                return parts[0].ToUpperInvariant();
            }
        }
        return null;
    }

    private async Task<string?> ReadInstallerHashAsync(
        string version,
        string installerName,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://nodejs.org/dist/v{version}/SHASUMS256.txt");
        request.Headers.UserAgent.ParseAdd("NaxUpdater/0.16.7");
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            return ParseSha256(await response.Content.ReadAsStringAsync(cancellationToken), installerName);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static bool TryMajor(string? version, out int major)
    {
        major = 0;
        var first = version?.Trim().TrimStart('v', 'V').Split('.', 2)[0];
        return int.TryParse(first, out major) && major > 0;
    }

    internal static string? DetectArchitecture(string? executablePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath) ||
                !Path.GetExtension(executablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            using var stream = File.OpenRead(executablePath);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt16() != 0x5A4D)
            {
                return null;
            }
            stream.Position = 0x3c;
            var peOffset = reader.ReadInt32();
            if (peOffset < 0 || peOffset > stream.Length - 6)
            {
                return null;
            }
            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550)
            {
                return null;
            }
            return reader.ReadUInt16() switch
            {
                0x8664 => "x64",
                0xAA64 => "arm64",
                0x014c => "x86",
                _ => null
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private UpdateCheckResult Result(
        InstalledApplication application,
        NodeRelease release,
        UpdateStatus status,
        string releaseNotes,
        string message,
        UpdateExecutionPlan? plan,
        string architecture,
        UpdateApplicability applicability = UpdateApplicability.Applicable) => new(
        application.Identity,
        application.DisplayName,
        application.NormalizedVersion,
        release.Version,
        status,
        Id,
        "Official Node.js distribution",
        "neutral",
        "Producer-published multi-language MSI",
        architecture,
        string.IsNullOrWhiteSpace(release.LtsName) ? "current" : $"LTS {release.LtsName}",
        releaseNotes,
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
        "Official Node.js distribution",
        "neutral",
        "Producer distribution metadata",
        "x64",
        "installed major",
        "https://nodejs.org/en/download/",
        message,
        null,
        Applicability: UpdateApplicability.Unknown);

    internal sealed record NodeRelease(string Version, string? LtsName);
}
