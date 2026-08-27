using NaxUpdater.Core.Models;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace NaxUpdater.Core.Services;

public sealed partial class ElectronBuilderUpdateProvider(HttpClient httpClient) : IUpdateProvider
{
    private readonly ConcurrentDictionary<string, UpdateConfiguration> _configurations = new(StringComparer.Ordinal);

    public string Id => "installed-updater-metadata";

    public bool CanHandle(InstalledApplication application) => GetConfiguration(application) is not null;

    public async Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        var configuration = GetConfiguration(application);
        if (configuration is null)
        {
            return Error(application, "No supported installed updater metadata was found.");
        }

        using var response = await httpClient.GetAsync(configuration.MetadataUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var metadata = ParseLatestMetadata(Encoding.UTF8.GetString(bytes));
        if (metadata is null)
        {
            return Error(application, "The installed updater feed did not contain a usable version and installer hash.");
        }

        var architecture = DetectArchitecture(application.PrimaryInstallPath);
        var artifact = SelectArtifact(metadata.Artifacts, architecture);
        if (artifact is null)
        {
            return Error(application, $"The installed updater feed has no Windows installer for {architecture}.", metadata.Version);
        }

        var status = VersionOrder.Compare(metadata.Version, application.NormalizedVersion) > 0
            ? UpdateStatus.Available
            : UpdateStatus.Current;
        var downloadUri = ResolveDownloadUri(configuration, artifact.Url);
        var signer = configuration.PublisherNames.FirstOrDefault() ??
                     NativeAuthenticodeVerifier.GetTrustedSigner(application.PrimaryInstallPath ?? string.Empty);
        if (status == UpdateStatus.Available &&
            (downloadUri is null || artifact.Sha512 is null || string.IsNullOrWhiteSpace(signer)))
        {
            return Error(
                application,
                "The installed updater metadata lacks a safe HTTPS download, SHA-512, or publisher identity; installation is blocked.",
                metadata.Version,
                configuration.ReleasePage);
        }

        var plan = status == UpdateStatus.Available
            ? new UpdateExecutionPlan(
                Path.GetExtension(artifact.Url).Equals(".msi", StringComparison.OrdinalIgnoreCase)
                    ? UpdateExecutionKind.DownloadedMsi
                    : UpdateExecutionKind.DownloadedExe,
                downloadUri,
                Path.GetFileName(downloadUri!.LocalPath),
                null,
                signer,
                null,
                ["/S"],
                application.Scope == InstallScope.Machine,
                AllowedHosts(configuration.MetadataUri, downloadUri),
                RunningProcesses(application),
                artifact.Sha512)
                with { ExpectedSigners = configuration.PublisherNames.Count > 0 ? configuration.PublisherNames : null }
            : null;

        return new UpdateCheckResult(
            application.Identity,
            application.DisplayName,
            application.NormalizedVersion,
            metadata.Version,
            status,
            Id,
            "Installed application update metadata",
            "application-managed",
            "Preserved by the application's updater",
            architecture,
            configuration.Channel,
            configuration.ReleasePage,
            "Discovered from the updater metadata installed with the application; SHA-512 and the configured Windows publisher are required.",
            plan);
    }

    private UpdateConfiguration? GetConfiguration(InstalledApplication application)
    {
        if (_configurations.TryGetValue(application.Identity, out var cached))
        {
            return cached;
        }
        var discovered = DiscoverConfiguration(application);
        if (discovered is not null)
        {
            _configurations.TryAdd(application.Identity, discovered);
        }
        return discovered;
    }

    private static UpdateConfiguration? DiscoverConfiguration(InstalledApplication application)
    {
        var executablePath = application.PrimaryInstallPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return null;
        }
        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }
        var path = Path.Combine(directory, "resources", "app-update.yml");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var values = ParseConfiguration(File.ReadAllText(path));
            var provider = Value(values, "provider")?.ToLowerInvariant();
            var channel = Value(values, "channel") ?? "latest";
            Uri? metadataUri = null;
            string? releasePage = null;
            Uri? downloadBaseUri = null;

            if (provider == "generic" && Uri.TryCreate(EnsureTrailingSlash(Value(values, "url")), UriKind.Absolute, out var genericBase) &&
                genericBase.Scheme == Uri.UriSchemeHttps)
            {
                downloadBaseUri = genericBase;
                metadataUri = new Uri(genericBase, $"{channel}.yml");
                releasePage = genericBase.AbsoluteUri;
            }
            else if (provider == "github" && IsSafeRepositoryPart(Value(values, "owner")) && IsSafeRepositoryPart(Value(values, "repo")))
            {
                var owner = Value(values, "owner")!;
                var repository = Value(values, "repo")!;
                downloadBaseUri = new Uri($"https://github.com/{owner}/{repository}/releases/latest/download/");
                metadataUri = new Uri(downloadBaseUri, $"{channel}.yml");
                releasePage = $"https://github.com/{owner}/{repository}/releases/latest";
            }

            return metadataUri is null || downloadBaseUri is null
                ? null
                : new UpdateConfiguration(
                    metadataUri,
                    downloadBaseUri,
                    releasePage,
                    channel,
                    values.Where(static pair => pair.Key.Equals("publisherName", StringComparison.OrdinalIgnoreCase))
                        .Select(static pair => pair.Value)
                        .Where(static value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or UriFormatException)
        {
            return null;
        }
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ParseConfiguration(string yaml)
    {
        var values = new List<KeyValuePair<string, string>>();
        string? listKey = null;
        foreach (var rawLine in yaml.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            if (line.StartsWith("- ", StringComparison.Ordinal) && listKey is not null)
            {
                values.Add(new KeyValuePair<string, string>(listKey, Unquote(line[2..])));
                continue;
            }
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }
            var key = line[..separator].Trim();
            var value = Unquote(line[(separator + 1)..]);
            listKey = value.Length == 0 ? key : null;
            if (value.Length > 0)
            {
                values.Add(new KeyValuePair<string, string>(key, value));
            }
        }
        return values;
    }

    private static LatestMetadata? ParseLatestMetadata(string yaml)
    {
        var version = Unquote(VersionRegex().Match(yaml).Groups["value"].Value);
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var artifacts = new List<UpdateArtifact>();
        string? url = null;
        foreach (var rawLine in yaml.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("- url:", StringComparison.OrdinalIgnoreCase))
            {
                url = Unquote(line[(line.IndexOf(':') + 1)..]);
                continue;
            }
            if (url is not null && line.StartsWith("sha512:", StringComparison.OrdinalIgnoreCase))
            {
                var hash = DecodeSha512(Unquote(line[(line.IndexOf(':') + 1)..]));
                if (hash is not null)
                {
                    artifacts.Add(new UpdateArtifact(url, hash));
                }
                url = null;
            }
        }

        if (artifacts.Count == 0)
        {
            var path = RootValueRegex("path").Match(yaml).Groups["value"].Value;
            var hash = DecodeSha512(RootValueRegex("sha512").Match(yaml).Groups["value"].Value);
            if (!string.IsNullOrWhiteSpace(path) && hash is not null)
            {
                artifacts.Add(new UpdateArtifact(Unquote(path), hash));
            }
        }
        return artifacts.Count == 0 ? null : new LatestMetadata(version, artifacts);
    }

    private static UpdateArtifact? SelectArtifact(IReadOnlyList<UpdateArtifact> artifacts, string architecture) =>
        artifacts
            .Where(static artifact =>
                Path.GetExtension(artifact.Url).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(artifact.Url).Equals(".msi", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(artifact => ArtifactScore(artifact.Url, architecture))
            .FirstOrDefault();

    private static int ArtifactScore(string url, string architecture)
    {
        var hasX64 = url.Contains("x64", StringComparison.OrdinalIgnoreCase);
        var hasArm64 = url.Contains("arm64", StringComparison.OrdinalIgnoreCase);
        return architecture switch
        {
            "x64" when hasX64 => 100,
            "x64" when hasArm64 => -100,
            "arm64" when hasArm64 => 100,
            "arm64" when hasX64 => -100,
            "x86" when !hasX64 && !hasArm64 => 100,
            _ when !hasX64 && !hasArm64 => 10,
            _ => 0
        };
    }

    private static string DetectArchitecture(string? executablePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                return "unknown";
            }
            using var stream = File.OpenRead(executablePath);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt16() != 0x5A4D)
            {
                return "unknown";
            }
            stream.Position = 0x3c;
            var peOffset = reader.ReadInt32();
            stream.Position = peOffset + 4;
            return reader.ReadUInt16() switch
            {
                0x8664 => "x64",
                0xAA64 => "arm64",
                0x014c => "x86",
                _ => "unknown"
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return "unknown";
        }
    }

    private static Uri? ResolveDownloadUri(UpdateConfiguration configuration, string artifactUrl)
    {
        if (Uri.TryCreate(artifactUrl, UriKind.Absolute, out var absolute))
        {
            return absolute.Scheme == Uri.UriSchemeHttps ? absolute : null;
        }
        return Uri.TryCreate(configuration.DownloadBaseUri, artifactUrl, out var relative) && relative.Scheme == Uri.UriSchemeHttps
            ? relative
            : null;
    }

    private static IReadOnlyList<string> AllowedHosts(Uri metadataUri, Uri downloadUri) =>
        new[]
        {
            metadataUri.Host,
            downloadUri.Host,
            "objects.githubusercontent.com",
            "release-assets.githubusercontent.com",
            "github-releases.githubusercontent.com"
        }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static IReadOnlyList<string> RunningProcesses(InstalledApplication application)
    {
        var name = Path.GetFileNameWithoutExtension(application.PrimaryInstallPath);
        return string.IsNullOrWhiteSpace(name) ? [] : [name];
    }

    private UpdateCheckResult Error(
        InstalledApplication application,
        string message,
        string? availableVersion = null,
        string? releasePage = null) => new(
            application.Identity,
            application.DisplayName,
            application.NormalizedVersion,
            availableVersion,
            UpdateStatus.Error,
            Id,
            "Installed application update metadata",
            "application-managed",
            "Preserved by the application's updater",
            "unknown",
            "unknown",
            releasePage,
            message,
            null);

    private static string? Value(IReadOnlyList<KeyValuePair<string, string>> values, string key) =>
        values.FirstOrDefault(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool IsSafeRepositoryPart(string? value) =>
        !string.IsNullOrWhiteSpace(value) && RepositoryPartRegex().IsMatch(value);

    private static string? EnsureTrailingSlash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.EndsWith('/') ? value : value + "/";

    private static string Unquote(string value) => value.Trim().Trim('"', '\'');

    private static string? DecodeSha512(string value)
    {
        try
        {
            var bytes = Convert.FromBase64String(Unquote(value));
            return bytes.Length == 64 ? Convert.ToHexString(bytes) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Regex RootValueRegex(string key) => new($"(?m)^{Regex.Escape(key)}:\\s*(?<value>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [GeneratedRegex(@"(?m)^version:\s*(?<value>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPartRegex();

    private sealed record UpdateConfiguration(
        Uri MetadataUri,
        Uri DownloadBaseUri,
        string? ReleasePage,
        string Channel,
        IReadOnlyList<string> PublisherNames);

    private sealed record LatestMetadata(string Version, IReadOnlyList<UpdateArtifact> Artifacts);
    private sealed record UpdateArtifact(string Url, string Sha512);
}
