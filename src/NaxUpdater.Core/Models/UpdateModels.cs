namespace NaxUpdater.Core.Models;

public enum UpdateStatus
{
    Current,
    Available,
    NewerReleaseKnown,
    ManagedExternally,
    Unsupported,
    Error
}

public enum UpdateApplicability
{
    Unknown,
    NotRequired,
    Applicable,
    NotApplicable
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

public enum UpdateProviderAuthority
{
    Unverified = 0,
    FallbackCatalog = 100,
    PlatformStore = 200,
    ProducerRelease = 300,
    InstalledUpdateProtocol = 400,
    ExplicitApplicationPolicy = 500
}

public sealed record UpdateProviderDescriptor(
    UpdateProviderAuthority Authority,
    int Specificity,
    string SelectionReason,
    IReadOnlyList<ManagementMode>? SupportedManagementModes = null);

public enum UpdateProcessPolicy
{
    CloseBeforeApply
}

public enum UpdateTransactionStage
{
    Created,
    Revalidating,
    Preparing,
    Quiescing,
    Applying,
    Verifying,
    Succeeded,
    PendingReboot,
    NoLongerApplicable,
    CanceledBeforeChange,
    FailedBeforeChange,
    FailedNeedsAttention,
    Indeterminate
}

public sealed record UpdateTransactionProgress(
    UpdateTransactionStage Stage,
    double? Fraction = null);

public sealed record UpdateExecutionResult(int ExitCode, bool IsSuccess, string? Error);

public sealed record ApplicationCloseResult(
    bool CloseRequested,
    bool ForcedTerminationUsed,
    IReadOnlyList<string> RemainingProcessNames)
{
    public bool AllClosed => RemainingProcessNames.Count == 0;
}

public sealed record UpdateTransactionResult(
    UpdateTransactionStage Stage,
    UpdateCheckResult? FreshAssessment,
    UpdateExecutionResult? Execution,
    string? Error,
    IReadOnlyList<string>? RemainingProcessNames = null)
{
    public bool IsSuccess => Stage == UpdateTransactionStage.Succeeded;
    public bool RequiresRestart => Stage == UpdateTransactionStage.PendingReboot;
}

public sealed record UpdateOperationRecord(
    Guid OperationId,
    string ApplicationIdentity,
    string? CorrelationKey,
    string DisplayName,
    string ProviderId,
    string? InstalledVersion,
    string? TargetVersion,
    string ExecutionFingerprint,
    UpdateTransactionStage Stage,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    string? Error = null);

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
    string? ExpectedHardwareId = null,
    string? NativeWorkingDirectory = null,
    UpdateProcessPolicy ProcessPolicy = UpdateProcessPolicy.CloseBeforeApply,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? ExpiresAt = null,
    string? InstalledVersionPrecondition = null,
    Guid CheckGenerationId = default,
    IReadOnlyList<string>? RunningExecutablePaths = null);

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
    UpdateExecutionPlan? ExecutionPlan,
    UpdateProviderAuthority ProviderAuthority = UpdateProviderAuthority.Unverified,
    string? ProviderSelectionReason = null,
    IReadOnlyList<string>? CandidateProviderIds = null,
    UpdateApplicability Applicability = UpdateApplicability.Applicable,
    string? CorrelationKey = null)
{
    public bool IsInstallable => ExecutionPlan is not null &&
                                 Status == UpdateStatus.Available &&
                                 Applicability == UpdateApplicability.Applicable &&
                                 !string.IsNullOrWhiteSpace(AvailableVersion);
}

public sealed record UpdateCheckSnapshot(
    DateTimeOffset CheckedAt,
    IReadOnlyList<UpdateCheckResult> Results,
    int UnsupportedApplicationCount,
    Guid GenerationId = default);
