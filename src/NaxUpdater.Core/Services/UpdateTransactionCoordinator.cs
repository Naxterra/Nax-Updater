using NaxUpdater.Core.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NaxUpdater.Core.Services;

public interface IUpdateTransactionBackend
{
    Task<UpdateCheckResult?> RevalidateAsync(UpdateCheckResult previous, CancellationToken cancellationToken);
    Task<PreparedUpdateExecution> PrepareAsync(
        UpdateCheckResult update,
        string cacheRoot,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
    Task<ApplicationCloseResult> QuiesceAsync(UpdateCheckResult update, CancellationToken cancellationToken);
    Task DiscardPreparedAsync(PreparedUpdateExecution prepared);
    Task<UpdateExecutionResult> ApplyAsync(
        UpdateCheckResult update,
        PreparedUpdateExecution prepared,
        CancellationToken cancellationToken);
}

public sealed record PreparedUpdateExecution(
    VerifiedInstaller? Installer,
    string? ExecutablePath,
    string? WorkingDirectory,
    string? CleanupDirectory,
    string? ContentSha256,
    IReadOnlyList<PreparedContentLock>? ContentLocks = null,
    PreparedCatalogUpdate? CatalogUpdate = null);

public sealed record PreparedContentLock(string Path, FileStream Stream) : IDisposable
{
    public void Dispose() => Stream.Dispose();
}

public sealed class DefaultUpdateTransactionBackend(
    UpdateExecutionService executionService,
    UpdatePackageDownloader packageDownloader,
    Func<UpdateCheckResult, CancellationToken, Task<UpdateCheckResult?>> revalidate) : IUpdateTransactionBackend
{
    public Task<UpdateCheckResult?> RevalidateAsync(UpdateCheckResult previous, CancellationToken cancellationToken) =>
        revalidate(previous, cancellationToken);

    public async Task<PreparedUpdateExecution> PrepareAsync(
        UpdateCheckResult update,
        string cacheRoot,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        VerifiedInstaller? installer = null;
        try
        {
            if (update.ExecutionPlan?.Kind is (
                    UpdateExecutionKind.DownloadedExe or
                    UpdateExecutionKind.DownloadedMsi or
                    UpdateExecutionKind.DownloadedZipMsi or
                    UpdateExecutionKind.DownloadedZipDriver))
            {
                installer = await packageDownloader.DownloadAndVerifyAsync(update, cacheRoot, progress, cancellationToken);
                installer = await packageDownloader.ReverifyAsync(update, installer, cancellationToken);
            }
            return await executionService.PrepareAsync(update, installer, cancellationToken);
        }
        catch
        {
            installer?.Dispose();
            throw;
        }
    }

    public Task<ApplicationCloseResult> QuiesceAsync(UpdateCheckResult update, CancellationToken cancellationToken) =>
        executionService.CloseForUpdateAsync(
            update,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            cancellationToken);

    public Task DiscardPreparedAsync(PreparedUpdateExecution prepared)
    {
        executionService.DiscardPrepared(prepared);
        return Task.CompletedTask;
    }

    public Task<UpdateExecutionResult> ApplyAsync(
        UpdateCheckResult update,
        PreparedUpdateExecution prepared,
        CancellationToken cancellationToken) =>
        executionService.ExecutePreparedAsync(update, prepared, cancellationToken);
}

public sealed class UpdateTransactionCoordinator
{
    private readonly IUpdateTransactionBackend _backend;
    private readonly IUpdateOperationJournal? _journal;
    private readonly IUpdateTransactionLeaseProvider? _leaseProvider;
    private readonly int _verificationAttempts;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _timeProvider;

    public UpdateTransactionCoordinator(
        IUpdateTransactionBackend backend,
        IUpdateOperationJournal? journal = null,
        IUpdateTransactionLeaseProvider? leaseProvider = null,
        int verificationAttempts = 3,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? timeProvider = null)
    {
        _backend = backend;
        _journal = journal;
        _leaseProvider = leaseProvider;
        _verificationAttempts = Math.Max(1, verificationAttempts);
        _delay = delay ?? Task.Delay;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<UpdateTransactionResult> ApplyAsync(
        UpdateCheckResult approvedAssessment,
        string cacheRoot,
        IProgress<UpdateTransactionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var lease = _leaseProvider?.TryAcquire();
        if (_leaseProvider is not null && lease is null)
        {
            return new(
                UpdateTransactionStage.FailedBeforeChange,
                approvedAssessment,
                null,
                "Another NaxUpdater process is already applying or recovering an update.");
        }
        UpdateOperationRecord? operation = approvedAssessment.ExecutionPlan is null
            ? null
            : _journal?.Begin(approvedAssessment, _timeProvider.GetUtcNow());
        void Report(UpdateTransactionStage stage, double? fraction = null)
        {
            progress?.Report(new(stage, fraction));
            if (fraction is null && operation is not null && _journal is not null)
            {
                operation = _journal.Record(operation, stage, _timeProvider.GetUtcNow());
            }
        }

        var result = await ApplyCoreAsync(
            approvedAssessment,
            cacheRoot,
            Report,
            cancellationToken);
        if (operation is not null && _journal is not null)
        {
            _journal.Record(operation, result.Stage, _timeProvider.GetUtcNow(), result.Error);
        }
        return result;
    }

    private async Task<UpdateTransactionResult> ApplyCoreAsync(
        UpdateCheckResult approvedAssessment,
        string cacheRoot,
        Action<UpdateTransactionStage, double?> report,
        CancellationToken cancellationToken)
    {
        var changeMayHaveStarted = false;
        UpdateCheckResult? freshAssessment = null;
        UpdateExecutionResult? execution = null;
        PreparedUpdateExecution? prepared = null;
        try
        {
            var approvedPlanError = UpdatePlanValidator.Validate(
                approvedAssessment,
                _timeProvider.GetUtcNow(),
                requireFreshness: false);
            if (approvedPlanError is not null)
            {
                return new(
                    UpdateTransactionStage.FailedBeforeChange,
                    approvedAssessment,
                    null,
                    approvedPlanError);
            }
            report(UpdateTransactionStage.Revalidating, null);
            freshAssessment = await _backend.RevalidateAsync(approvedAssessment, cancellationToken);
            if (!SameApprovedOffer(approvedAssessment, freshAssessment))
            {
                return new(
                    UpdateTransactionStage.NoLongerApplicable,
                    freshAssessment,
                    null,
                    "The update offer changed or is no longer applicable. The application was not modified.");
            }

            var validationError = UpdatePlanValidator.Validate(freshAssessment!, _timeProvider.GetUtcNow());
            if (validationError is not null)
            {
                return new(UpdateTransactionStage.FailedBeforeChange, freshAssessment, null, validationError);
            }

            report(UpdateTransactionStage.Preparing, null);
            var preparationProgress = new Progress<double>(fraction =>
                report(UpdateTransactionStage.Preparing, fraction));
            prepared = await _backend.PrepareAsync(
                freshAssessment!,
                cacheRoot,
                preparationProgress,
                cancellationToken);

            report(UpdateTransactionStage.Revalidating, null);
            var afterPreparation = await _backend.RevalidateAsync(freshAssessment!, cancellationToken);
            if (!SameApprovedOffer(freshAssessment!, afterPreparation))
            {
                await _backend.DiscardPreparedAsync(prepared);
                prepared = null;
                return new(
                    UpdateTransactionStage.NoLongerApplicable,
                    afterPreparation,
                    null,
                    "The update offer or installed state changed during preparation. The prepared payload was discarded and the application was not modified.");
            }
            freshAssessment = afterPreparation!;

            if (freshAssessment!.ExecutionPlan!.ProcessPolicy == UpdateProcessPolicy.CloseBeforeApply)
            {
                report(UpdateTransactionStage.Quiescing, null);
                var close = await _backend.QuiesceAsync(freshAssessment, cancellationToken);
                if (!close.AllClosed)
                {
                    await _backend.DiscardPreparedAsync(prepared);
                    prepared = null;
                    return new(
                        UpdateTransactionStage.FailedBeforeChange,
                        freshAssessment,
                        null,
                        "Required application processes could not be closed.",
                        close.RemainingProcessNames);
                }
            }

            report(UpdateTransactionStage.Applying, null);
            changeMayHaveStarted = true;
            execution = await _backend.ApplyAsync(freshAssessment, prepared, cancellationToken);
            prepared = null;

            report(UpdateTransactionStage.Verifying, null);
            var observed = execution.ExitCode == 1223
                ? await _backend.RevalidateAsync(freshAssessment, cancellationToken)
                : await ObserveInstalledTargetAsync(freshAssessment, cancellationToken);
            if (ReachedTarget(freshAssessment, observed))
            {
                var completedStage = execution.ExitCode is 1641 or 3010
                    ? UpdateTransactionStage.PendingReboot
                    : UpdateTransactionStage.Succeeded;
                report(completedStage, 1);
                return new(completedStage, observed, execution, null);
            }
            if (execution.IsSuccess && execution.ExitCode is 1641 or 3010)
            {
                report(UpdateTransactionStage.PendingReboot, null);
                return new(
                    UpdateTransactionStage.PendingReboot,
                    observed,
                    execution,
                    "The updater requested a restart; the target version will be verified after Windows restarts.");
            }
            if (!execution.IsSuccess)
            {
                if (execution.ExitCode == 1223)
                {
                    return new(
                        UpdateTransactionStage.CanceledBeforeChange,
                        observed,
                        execution,
                        "The Windows elevation prompt was canceled. The prepared payload was not executed.");
                }
                return new(
                    UpdateTransactionStage.FailedNeedsAttention,
                    observed,
                    execution,
                    execution.Error ?? $"The updater exited with code {execution.ExitCode}.");
            }
            return new(
                UpdateTransactionStage.Indeterminate,
                observed,
                execution,
                "The updater exited successfully, but the installed version did not reach the promised target.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (prepared is not null)
            {
                await _backend.DiscardPreparedAsync(prepared);
                prepared = null;
            }
            if (changeMayHaveStarted && freshAssessment is not null)
            {
                try
                {
                    report(UpdateTransactionStage.Verifying, null);
                    var observed = await _backend.RevalidateAsync(freshAssessment, CancellationToken.None);
                    if (ReachedTarget(freshAssessment, observed))
                    {
                        return new(UpdateTransactionStage.Succeeded, observed, execution, null);
                    }
                    return new(
                        UpdateTransactionStage.Indeterminate,
                        observed,
                        execution,
                        "Waiting was canceled after application began; installed state was checked but the target could not be confirmed.");
                }
                catch
                {
                    return new(
                        UpdateTransactionStage.Indeterminate,
                        freshAssessment,
                        execution,
                        "Waiting was canceled after application began and installed state could not be re-read.");
                }
            }
            return new(
                UpdateTransactionStage.CanceledBeforeChange,
                freshAssessment,
                execution,
                "The update was canceled before the application could be changed.");
        }
        catch (Exception exception)
        {
            if (prepared is not null)
            {
                await _backend.DiscardPreparedAsync(prepared);
                prepared = null;
            }
            if (changeMayHaveStarted && freshAssessment is not null)
            {
                try
                {
                    report(UpdateTransactionStage.Verifying, null);
                    var observed = await _backend.RevalidateAsync(freshAssessment, CancellationToken.None);
                    if (ReachedTarget(freshAssessment, observed))
                    {
                        return new(UpdateTransactionStage.Succeeded, observed, execution, null);
                    }
                    return new(UpdateTransactionStage.FailedNeedsAttention, observed, execution, exception.Message);
                }
                catch
                {
                    return new(UpdateTransactionStage.Indeterminate, freshAssessment, execution, exception.Message);
                }
            }
            return new(UpdateTransactionStage.FailedBeforeChange, freshAssessment, execution, exception.Message);
        }
    }

    internal static bool SameApprovedOffer(UpdateCheckResult approved, UpdateCheckResult? fresh) =>
        fresh is
        {
            Status: UpdateStatus.Available,
            IsInstallable: true,
            ExecutionPlan: not null,
            AvailableVersion: not null
        } &&
        fresh.ApplicationIdentity.Equals(approved.ApplicationIdentity, StringComparison.Ordinal) &&
        string.Equals(fresh.CorrelationKey, approved.CorrelationKey, StringComparison.Ordinal) &&
        string.Equals(fresh.InstalledVersion, approved.InstalledVersion, StringComparison.OrdinalIgnoreCase) &&
        fresh.ProviderId.Equals(approved.ProviderId, StringComparison.Ordinal) &&
        fresh.ProviderAuthority == approved.ProviderAuthority &&
        fresh.AvailableVersion.Equals(approved.AvailableVersion, StringComparison.OrdinalIgnoreCase) &&
        approved.ExecutionPlan is not null &&
        SameExecutionIntent(approved.ExecutionPlan, fresh.ExecutionPlan);

    internal static bool ReachedTarget(UpdateCheckResult expected, UpdateCheckResult? observed) =>
        observed is not null &&
        !string.IsNullOrWhiteSpace(expected.CorrelationKey) &&
        expected.CorrelationKey.Equals(observed.CorrelationKey, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(expected.AvailableVersion) &&
        !string.IsNullOrWhiteSpace(observed.InstalledVersion) &&
        (expected.ExecutionPlan?.Kind == UpdateExecutionKind.StorePackage
            ? VersionOrder.Compare(observed.InstalledVersion, expected.AvailableVersion) == 0
            : VersionOrder.Compare(observed.InstalledVersion, expected.AvailableVersion) >= 0);

    private static bool SameExecutionIntent(UpdateExecutionPlan approved, UpdateExecutionPlan fresh) =>
        UpdateExecutionIntent.Fingerprint(approved).Equals(
            UpdateExecutionIntent.Fingerprint(fresh),
            StringComparison.Ordinal);

    private async Task<UpdateCheckResult?> ObserveInstalledTargetAsync(
        UpdateCheckResult expected,
        CancellationToken cancellationToken)
    {
        UpdateCheckResult? observed = null;
        for (var attempt = 0; attempt < _verificationAttempts; attempt++)
        {
            observed = await _backend.RevalidateAsync(expected, cancellationToken);
            if (ReachedTarget(expected, observed) || attempt + 1 >= _verificationAttempts)
            {
                return observed;
            }
            await _delay(TimeSpan.FromSeconds(1 << attempt), cancellationToken);
        }
        return observed;
    }
}

public static class UpdateExecutionIntent
{
    public static string Fingerprint(UpdateExecutionPlan plan)
    {
        var stable = plan with
        {
            CreatedAt = null,
            ExpiresAt = null,
            InstalledVersionPrecondition = null,
            CheckGenerationId = Guid.Empty
        };
        var json = JsonSerializer.Serialize(stable);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}

public static class UpdatePlanValidator
{
    public static string? Validate(
        UpdateCheckResult update,
        DateTimeOffset now,
        bool requireFreshness = true)
    {
        var plan = update.ExecutionPlan;
        if (!update.IsInstallable || plan is null)
        {
            return "The assessment does not contain an applicable execution plan.";
        }
        if (plan.CheckGenerationId == Guid.Empty || plan.CreatedAt is null || plan.ExpiresAt is null)
        {
            return "The execution plan is not bound to a check generation and cannot be applied.";
        }
        if (requireFreshness && plan.ExpiresAt <= now)
        {
            return "The execution plan expired and must be checked again.";
        }
        if (!string.Equals(
                plan.InstalledVersionPrecondition,
                update.InstalledVersion,
                StringComparison.OrdinalIgnoreCase))
        {
            return "The installed-version precondition no longer matches the checked application.";
        }
        if (string.IsNullOrWhiteSpace(update.AvailableVersion))
        {
            return "The execution plan has no target version.";
        }
        if (string.IsNullOrWhiteSpace(update.CorrelationKey))
        {
            return "The execution plan is not bound to a stable installed-application correlation identity.";
        }
        if (plan.ProcessPolicy == UpdateProcessPolicy.CloseBeforeApply &&
            plan.RunningProcessNames.Count > 0 &&
            plan.RunningExecutablePaths is not { Count: > 0 })
        {
            return "The process-close plan is not bound to a verified executable path.";
        }
        return plan.Kind switch
        {
            UpdateExecutionKind.DownloadedExe or
            UpdateExecutionKind.DownloadedMsi or
            UpdateExecutionKind.DownloadedZipMsi or
            UpdateExecutionKind.DownloadedZipDriver
                when plan.DownloadUri is null ||
                     plan.DownloadUri.Scheme != Uri.UriSchemeHttps ||
                     string.IsNullOrWhiteSpace(plan.FileName) ||
                     string.IsNullOrWhiteSpace(plan.Sha256) && string.IsNullOrWhiteSpace(plan.Sha512) ||
                     plan.AllowedDownloadHosts.Count == 0
                => "The downloadable update plan is missing its HTTPS artifact identity or release hash.",
            UpdateExecutionKind.DownloadedExe or
            UpdateExecutionKind.DownloadedMsi or
            UpdateExecutionKind.DownloadedZipMsi
                when plan.RequireAuthenticode &&
                     string.IsNullOrWhiteSpace(plan.ExpectedSigner) &&
                     plan.ExpectedSigners is not { Count: > 0 }
                => "The downloadable installer requires Authenticode but has no approved publisher identity.",
            UpdateExecutionKind.DownloadedZipMsi when string.IsNullOrWhiteSpace(plan.NestedInstallerRelativePath)
                => "The verified archive does not identify its exact nested MSI.",
            UpdateExecutionKind.DownloadedZipDriver
                when string.IsNullOrWhiteSpace(plan.NestedInstallerRelativePath) ||
                     string.IsNullOrWhiteSpace(plan.ExpectedHardwareId) ||
                     plan.ExpectedSigners is not { Count: > 0 }
                => "The driver archive lacks an exact INF, hardware identity, or catalog signer policy.",
            UpdateExecutionKind.NativeCommand when string.IsNullOrWhiteSpace(plan.NativeExecutable)
                => "The native update plan does not identify its installed updater executable.",
            UpdateExecutionKind.NativeCommand when string.IsNullOrWhiteSpace(plan.ExpectedSigner)
                => "A native updater requires a trusted Authenticode publisher.",
            UpdateExecutionKind.StorePackage
                when string.IsNullOrWhiteSpace(plan.StoreProductId) ||
                     string.IsNullOrWhiteSpace(plan.StorePackageFamilyName)
                => "The package update plan does not contain an exact Store product and package-family identity.",
            UpdateExecutionKind.WingetPackage when plan.WingetTarget is null ||
                plan.WingetTarget.SourceId != WingetPackageService.OfficialSourceId ||
                plan.WingetTarget.Version != update.AvailableVersion
                => "The WinGet update plan does not contain the approved official package and version.",
            _ => null
        };
    }
}
