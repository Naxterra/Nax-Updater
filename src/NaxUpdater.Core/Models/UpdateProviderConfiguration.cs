using System.Text.Json.Serialization;

namespace NaxUpdater.Core.Models;

public sealed class UpdateProviderCatalog
{
    public List<GitHubUpdateRecipe> GitHub { get; init; } = [];
}

public sealed class GitHubUpdateRecipe
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? PublisherContains { get; init; }
    public string Repository { get; init; } = string.Empty;
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
}
