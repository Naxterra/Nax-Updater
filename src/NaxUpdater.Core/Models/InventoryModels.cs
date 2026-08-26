namespace NaxUpdater.Core.Models;

public enum EvidenceKind
{
    Registry,
    Shortcut,
    Executable,
    MsixPackage,
    ZeroInstall,
    Policy,
    FileSystem
}

public enum ConfidenceLevel
{
    Low,
    Medium,
    High
}

public enum ManagementMode
{
    Unmanaged,
    Registry,
    WindowsInstaller,
    Msix,
    ZeroInstall,
    NativeSelfUpdater,
    DirectVendor
}

public enum InstallScope
{
    Unknown,
    CurrentUser,
    Machine
}

public sealed record ApplicationEvidence(
    EvidenceKind Kind,
    string Label,
    string Value,
    bool Verified = false);

public sealed record InstalledApplication(
    string Identity,
    string DisplayName,
    string? Publisher,
    string? InstalledVersion,
    string? NormalizedVersion,
    string? VersionSource,
    string? PrimaryInstallPath,
    string? PathSource,
    DateTimeOffset? InstalledOn,
    string? InstallDateSource,
    InstallScope Scope,
    ManagementMode ManagementMode,
    ConfidenceLevel Confidence,
    bool IsSystemComponent,
    IReadOnlyList<string> BlockedProviders,
    RemovalPlan? RemovalPlan,
    IReadOnlyList<ApplicationEvidence> Evidence);

public sealed record InventoryIssue(
    string Source,
    string Message,
    string? ExceptionType = null);

public sealed record InventorySnapshot(
    DateTimeOffset ScannedAt,
    IReadOnlyList<InstalledApplication> Applications,
    IReadOnlyList<ApplicationPolicy> UnmatchedPolicies,
    IReadOnlyList<InventoryIssue> Issues);
