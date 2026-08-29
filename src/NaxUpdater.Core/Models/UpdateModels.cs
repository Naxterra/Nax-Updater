namespace NaxUpdater.Core.Models;

public enum UpdateStatus
{
    Current,
    Available,
    ManagedExternally,
    Unsupported,
    Error
}

public enum UpdateExecutionKind
{
    DownloadedExe,
    DownloadedMsi,
    DownloadedZipMsi,
    DownloadedZipDriver,
    NativeCommand,
    StorePackage
}

public sealed record UpdateExecutionPlan(
    UpdateExecutionKind Kind,
    Uri? DownloadUri,
    string? FileName,
    string? Sha256,
    string? ExpectedSigner,
    string? NativeExecutable,
    IReadOnlyList<string> Arguments,
    bool RequiresElevation,
    IReadOnlyList<string> AllowedDownloadHosts,
    IReadOnlyList<string> RunningProcessNames,
    string? Sha512 = null,
    bool RequireAuthenticode = true,
    bool AllowHashVerifiedRedirects = false,
    string? StoreProductId = null,
    string? StorePackageFamilyName = null,
    string? StorePublisher = null,
    IReadOnlyList<string>? ExpectedSigners = null,
    string? NestedInstallerRelativePath = null,
    string? ExpectedHardwareId = null);

public sealed record UpdateCheckResult(
    string ApplicationIdentity,
    string DisplayName,
    string? InstalledVersion,
    string? AvailableVersion,
    UpdateStatus Status,
    string ProviderId,
    string ProviderDisplayName,
    string Language,
    string LanguageSource,
    string Architecture,
    string Channel,
    string? ReleaseNotesUrl,
    string? Message,
    UpdateExecutionPlan? ExecutionPlan)
{
    public bool IsInstallable => ExecutionPlan is not null && Status == UpdateStatus.Available;
}

public sealed record UpdateCheckSnapshot(
    DateTimeOffset CheckedAt,
    IReadOnlyList<UpdateCheckResult> Results,
    int UnsupportedApplicationCount);
