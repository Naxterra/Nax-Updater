namespace NaxUpdater.Core.Models;

public enum UpdateStatus
{
    Current,
    Available,
    ManagedExternally,
    Error
}

public enum UpdateExecutionKind
{
    DownloadedExe,
    DownloadedMsi,
    NativeCommand
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
    string? Sha512 = null);

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
    public bool IsInstallable => Status == UpdateStatus.Available && ExecutionPlan is not null;
}

public sealed record UpdateCheckSnapshot(
    DateTimeOffset CheckedAt,
    IReadOnlyList<UpdateCheckResult> Results,
    int UnsupportedApplicationCount);
