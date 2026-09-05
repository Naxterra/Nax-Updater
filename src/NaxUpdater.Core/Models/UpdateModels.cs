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

public enum UpdateAvailabilityReason { None, AwaitingStorePublication }
public sealed record UpdateCheckProgress(int Completed, int Total, string Phase, string? ApplicationName);

public enum UpdateExecutionKind
{
    DownloadedExe,
    DownloadedMsi,
    DownloadedZipMsi,
    DownloadedZipDriver,
    NativeCommand,
    StorePackage,
    WingetPackage,
    NativeStorePackage
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
    IReadOnlyList<string>? RunningExecutablePaths = null,
    WingetUpdateTarget? WingetTarget = null,
    PublishedStorePackage? NativeStoreTarget = null);

public sealed record PublishedStorePackage(
    string ProductId, string SkuId, string PackageFamilyName,
    string Version, string PackageFullName, string Architecture);

public sealed record WingetUpdateTarget(
    string PackageId,
    string SourceId,
    string Version,
    string InstalledCatalogVersion,
    IReadOnlyList<string> InstalledProductCodes,
    string Architecture,
    string InstallerType,
    string Locale,
    InstallScope Scope,
    string? InstallLocation);

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
    string? CorrelationKey = null,
    UpdateAvailabilityReason AvailabilityReason = UpdateAvailabilityReason.None,
    string? PublishedPackageVersion = null,
    string? AnnouncedVersion = null)
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
    Guid GenerationId = default)
{
    public int CheckedVersionCount => Results.Count(static result =>
        result.Status is UpdateStatus.Current or UpdateStatus.Available or UpdateStatus.NewerReleaseKnown);
    public int ManagedExternallyCount => Results.Count(static result => result.Status == UpdateStatus.ManagedExternally);
    public int FailedCheckCount => Results.Count(static result => result.Status == UpdateStatus.Error);
    public int InstallableUpdateCount => Results.Count(static result => result.IsInstallable);
    public int KnownReleaseCount => Results.Count(static result => result.Status == UpdateStatus.NewerReleaseKnown);
    public bool AllCurrent => Results.Count > 0 && Results.All(static result => result.Status == UpdateStatus.Current);
}
