using System.Text.Json.Serialization;

namespace NaxUpdater.Core.Models;

public sealed class UpdateProviderCatalog
{
    public List<GitHubUpdateRecipe> GitHub { get; init; } = [];
    public List<OfficialSiteRecipe> Official { get; init; } = [];
}

// An application's official download site, described once so a scan reads the
// publisher's own version source directly instead of searching catalogs.
// Templates take {version}, {version_} (dots as underscores) and every named
// group captured from the display name, version or hash patterns.
public sealed class OfficialSiteRecipe
{
    public string Id { get; init; } = string.Empty;
    public string DisplayNamePattern { get; init; } = string.Empty;
    public string? PublisherContains { get; init; }
    public string VersionUrl { get; init; } = string.Empty;
    // Matched against the whole document; every match is a candidate and the
    // highest version wins. Named groups: version (required), hash, url, others.
    public string VersionPattern { get; init; } = string.Empty;
    public bool StayOnInstalledMajor { get; init; }
    public string DownloadUrl { get; init; } = string.Empty;
    public string? HashUrl { get; init; }
    public string? HashPattern { get; init; }
    public string HashAlgorithm { get; init; } = "sha256";
    public string HashEncoding { get; init; } = "hex";
    // A vendor's staged rollout (electron-builder stagingPercentage) is honored.
    public string? RolloutPattern { get; init; }
    public string? ReleaseNotesUrl { get; init; }
    public string ExpectedSigner { get; init; } = string.Empty;
    public bool Unsigned { get; init; }
    public List<string> AllowedHosts { get; init; } = [];
    // Download servers that redirect to mirrors; the vendor-published hash is verified regardless.
    public bool AllowMirrorRedirects { get; init; }
    public string Architecture { get; init; } = "x64";
    public string Language { get; init; } = "neutral";
    [JsonConverter(typeof(JsonStringEnumConverter<UpdateExecutionKind>))]
    public UpdateExecutionKind InstallerKind { get; init; }
    public List<string> InstallerArguments { get; init; } = [];
    public List<string>? CurrentUserInstallerArguments { get; init; }
    public List<string> RunningProcessNames { get; init; } = [];
    public bool RequiresElevation { get; init; }
    // Installers that register per user but install into Program Files (Kodi).
    public bool AlwaysElevate { get; init; }
}

public sealed class GitHubUpdateRecipe
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? PublisherContains { get; init; }
    public string Repository { get; init; } = string.Empty;
    public string? ReleaseTagPrefix { get; init; }
    public string AssetNamePattern { get; init; } = string.Empty;
    public string Architecture { get; init; } = "neutral";
    public string Language { get; init; } = "neutral";
    public string ExpectedSigner { get; init; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter<UpdateExecutionKind>))]
    public UpdateExecutionKind InstallerKind { get; init; }
    public List<string> InstallerArguments { get; init; } = [];
    public List<string> RunningProcessNames { get; init; } = [];
    public bool RequiresElevation { get; init; }
    public Dictionary<string, string> AlternateArchitectureAssets { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string>? CurrentUserInstallerArguments { get; init; }
    public string? InstallDirectoryArgument { get; init; }
    // The publisher ships unsigned installers (the owner's own apps): GitHub's SHA-256
    // asset digest is then the only verification, and it stays mandatory.
    public bool Unsigned { get; init; }
    // An installed pre-release (2.0.0-alpha.23) follows that release channel.
    public bool FollowInstalledPrerelease { get; init; }
    // Release assets of a private repository are downloaded through GitHub's API
    // with the signed-in GitHub CLI account; the digest is verified as usual.
    public bool PrivateRepository { get; init; }
}
