using System.Text.Json.Serialization;

namespace NaxUpdater.Core.Models;

public sealed class ApplicationPolicyCatalog
{
    public List<ApplicationPolicy> Applications { get; init; } = [];
}

public sealed class ApplicationPolicy
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? PublisherContains { get; init; }
    public List<string> BlockedProviders { get; init; } = [];
    [JsonConverter(typeof(JsonStringEnumConverter<ManagementMode>))]
    public ManagementMode? ManagementMode { get; init; }
    public string? PreferredProvider { get; init; }
    public string? VersionNormalization { get; init; }
    public bool AppliesWhenAbsent { get; init; }
    public string? Reason { get; init; }
}
