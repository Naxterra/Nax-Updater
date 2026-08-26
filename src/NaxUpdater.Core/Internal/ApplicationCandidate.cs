using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

namespace NaxUpdater.Core.Internal;

internal sealed class ApplicationCandidate
{
    public required string Identity { get; init; }
    public required string DisplayName { get; init; }
    public string? Publisher { get; set; }
    public string? RegistryVersion { get; set; }
    public string? ExecutableVersion { get; set; }
    public string? ProviderVersion { get; set; }
    public string? UninstallString { get; set; }
    public DateTimeOffset? InstalledOn { get; set; }
    public string? InstallDateSource { get; set; }
    public InstallScope Scope { get; set; }
    public ManagementMode ManagementMode { get; set; } = ManagementMode.Registry;
    public bool IsSystemComponent { get; set; }
    public bool IsMsixIntegrationPackage { get; set; }
    public string? MsixPackageFamilyName { get; set; }
    public MsixManifestInspection? MsixManifest { get; set; }
    public RemovalPlan? RemovalPlan { get; set; }
    public ApplicationPolicy? Policy { get; set; }
    public List<PathCandidate> Paths { get; } = [];
    public List<ApplicationEvidence> Evidence { get; } = [];
    public HashSet<string> BlockedProviders { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed record PathCandidate(
    string Path,
    string Source,
    int Priority,
    bool Verified);
