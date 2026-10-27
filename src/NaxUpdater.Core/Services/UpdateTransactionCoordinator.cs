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
    Task<UpdateExecutionResult> ApplyWithProgressAsync(UpdateCheckResult update, PreparedUpdateExecution prepared,
        IProgress<double> progress, CancellationToken token) => ApplyAsync(update, prepared, token);
    // Called once the installed target was verified; the payload is no longer needed.
    Task ReleaseCompletedPayloadAsync(UpdateCheckResult update, PreparedUpdateExecution prepared) => Task.CompletedTask;
}

public sealed record PreparedUpdateExecution(
    VerifiedInstaller? Installer,
    string? ExecutablePath,
    string? WorkingDirectory,
    string? CleanupDirectory,
    string? ContentSha256,
    IReadOnlyList<PreparedContentLock>? ContentLocks = null,
    PreparedCatalogUpdate? CatalogUpdate = null,
    PreparedNativeStoreUpdate? NativeStoreUpdate = null,
    PreparedStoreQueueUpdate? StoreQueueUpdate = null,
    PreparedChocolateyUpdate? ChocolateyUpdate = null);

public sealed class PreparedChocolateyUpdate(
    ChocolateyUpdateTarget target,
    Func<IProgress<double>?, CancellationToken, Task<UpdateExecutionResult>> apply)
{
    public ChocolateyUpdateTarget Target { get; } = target;
    public Task<UpdateExecutionResult> ApplyAsync(CancellationToken token, IProgress<double>? progress = null) => apply(progress, token);
}

public sealed record PreparedContentLock(string Path, FileStream Stream) : IDisposable
{
    public void Dispose() => Stream.Dispose();
}

public sealed class DefaultUpdateTransactionBackend(
    UpdateExecutionService executionService,
    UpdatePackageDownloader packageDownloader,
    Func<UpdateCheckResult, CancellationToken, Task<UpdateCheckResult?>> revalidate) : IUpdateTransactionBackend
{
    private string? _cacheRoot;

    public Task<UpdateCheckResult?> RevalidateAsync(UpdateCheckResult previous, CancellationToken cancellationToken) =>
        revalidate(previous, cancellationToken);

    public Task ReleaseCompletedPayloadAsync(UpdateCheckResult update, PreparedUpdateExecution prepared)
    {
        if (_cacheRoot is not null && prepared.Installer?.Path is { } path)
            UpdatePayloadCache.ReleaseCompleted(_cacheRoot, path, update.ProviderId);
        return Task.CompletedTask;
    }

    public async Task<PreparedUpdateExecution> PrepareAsync(
        UpdateCheckResult update,
        string cacheRoot,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        VerifiedInstaller? installer = null;
        _cacheRoot = cacheRoot;
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

    public Task<UpdateExecutionResult> ApplyWithProgressAsync(UpdateCheckResult update, PreparedUpdateExecution prepared,
        IProgress<double> progress, CancellationToken token) => executionService.ExecutePreparedAsync(update, prepared, token, progress);
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
        UpdateOperationRecord? operation;
        try
        {
            operation = approvedAssessment.ExecutionPlan is null
                ? null
                : _journal?.Begin(approvedAssessment, _timeProvider.GetUtcNow());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Without a journal entry an interrupted change could not be recovered.
            return new(
                UpdateTransactionStage.FailedBeforeChange,
                approvedAssessment,
                null,
                $"The update journal could not be written, so the update was not started: {exception.Message}");
        }
        string? journalFailure = null;
        void Report(UpdateTransactionStage stage, double? fraction = null)
        {
            progress?.Report(new(stage, fraction));
            if (fraction is null && operation is not null && _journal is not null)
            {
                try { operation = _journal.Record(operation, stage, _timeProvider.GetUtcNow()); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { journalFailure = exception.Message; }
            }
        }

        var result = await ApplyCoreAsync(
            approvedAssessment,
            cacheRoot,
            Report,
            cancellationToken);
        if (operation is not null && _journal is not null)
        {
            // The installer has already finished; a locked or full disk must not turn its
            // verified outcome into an exception.
            try { _journal.Record(operation, result.Stage, _timeProvider.GetUtcNow(), result.Error); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { journalFailure = exception.Message; }
        }
        return journalFailure is null
            ? result
            : result with { JournalWarning = $"The outcome could not be saved in the update journal: {journalFailure}" };
    }

    private async Task<UpdateTransactionResult> ApplyCoreAsync(
        UpdateCheckResult approvedAssessment,
        string cacheRoot,
        Action<UpdateTransactionStage, double?> report,
        CancellationToken cancellationToken)
    {
        var changeMayHaveStarted = false;
        var repairOnly = false;
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
            // A same-version repair (Chocolatey reinstalling a version installed outside it
            // so its record catches up) satisfies the version check before it runs. Its
            // success is proven only by a successful execution, never by the version.
            repairOnly = VersionOrder.Compare(freshAssessment.AvailableVersion, freshAssessment.InstalledVersion) <= 0;
            execution = await _backend.ApplyWithProgressAsync(freshAssessment, prepared,
                new Progress<double>(fraction => report(UpdateTransactionStage.Applying, fraction)), cancellationToken);
            var appliedPayload = prepared;
            prepared = null;

            report(UpdateTransactionStage.Verifying, null);
            var observed = execution.NotStarted
                ? await _backend.RevalidateAsync(freshAssessment, cancellationToken)
                : await ObserveInstalledTargetAsync(freshAssessment, cancellationToken);
            if (ReachedTarget(freshAssessment, observed) && (!repairOnly || execution.IsSuccess) ||
                execution.IsSuccess && ReconciledStoreQueue(freshAssessment, observed))
            {
                var completedStage = execution.ExitCode is 1641 or 3010
                    ? UpdateTransactionStage.PendingReboot
                    : UpdateTransactionStage.Succeeded;
                if (completedStage == UpdateTransactionStage.Succeeded)
                {
                    try { await _backend.ReleaseCompletedPayloadAsync(freshAssessment, appliedPayload); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
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
                // Only a refused start proves that nothing ran. An installer that ran and
                // exited with its own ERROR_CANCELLED (1223) may have changed the system.
                if (execution.NotStarted)
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
                    if (ReachedTarget(freshAssessment, observed) && !repairOnly)
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
            // The executor refuses to start (an application was reopened after closing, or
            // the verified payload is missing or changed) before anything runs.
            if (changeMayHaveStarted && freshAssessment is not null &&
                exception is not (ApplicationStillRunningException or UpdateExecutionRefusedException))
            {
                try
                {
                    report(UpdateTransactionStage.Verifying, null);
                    var observed = await _backend.RevalidateAsync(freshAssessment, CancellationToken.None);
                    if (ReachedTarget(freshAssessment, observed) && !repairOnly)
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
            IsInstallable: true,
            ExecutionPlan: not null
        } &&
        fresh.ApplicationIdentity.Equals(approved.ApplicationIdentity, StringComparison.Ordinal) &&
        string.Equals(fresh.CorrelationKey, approved.CorrelationKey, StringComparison.Ordinal) &&
        string.Equals(fresh.InstalledVersion, approved.InstalledVersion, StringComparison.OrdinalIgnoreCase) &&
        fresh.ProviderId.Equals(approved.ProviderId, StringComparison.Ordinal) &&
        fresh.ProviderAuthority == approved.ProviderAuthority &&
        string.Equals(fresh.AvailableVersion, approved.AvailableVersion, StringComparison.OrdinalIgnoreCase) &&
        approved.ExecutionPlan is not null &&
        SameExecutionIntent(approved.ExecutionPlan, fresh.ExecutionPlan);

    // The Store does not expose a trustworthy target version on a queue item.
    // This is a servicing operation, not a fabricated version-bound offer:
    // require Windows completion AND independently observed installed identity,
    // no active queue entry, and no downgrade relative to the approved baseline.
    internal static bool ReconciledStoreQueue(UpdateCheckResult expected, UpdateCheckResult? observed) =>
        expected.ExecutionPlan?.StoreQueueTarget is { } target && observed is not null &&
        observed.Status is not (UpdateStatus.StoreQueued or UpdateStatus.Error) &&
        !string.IsNullOrWhiteSpace(expected.CorrelationKey) && expected.CorrelationKey == observed.CorrelationKey &&
        !string.IsNullOrWhiteSpace(observed.InstalledVersion) &&
        VersionOrder.Compare(observed.InstalledVersion, target.InstalledVersion) >= 0;

    // The observed application is the same one when it keeps its inventory identity or its
    // correlation key. The key of a versioned display name ("7-Zip 24.09" becoming
    // "7-Zip 25.01") changes with the major version while the identity stays the same.
    internal static bool ReachedTarget(UpdateCheckResult expected, UpdateCheckResult? observed) =>
        observed is not null &&
        (!string.IsNullOrWhiteSpace(expected.ApplicationIdentity) &&
         expected.ApplicationIdentity.Equals(observed.ApplicationIdentity, StringComparison.Ordinal) ||
         !string.IsNullOrWhiteSpace(expected.CorrelationKey) &&
         expected.CorrelationKey.Equals(observed.CorrelationKey, StringComparison.Ordinal)) &&
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
            if (ReachedTarget(expected, observed) || ReconciledStoreQueue(expected, observed) || attempt + 1 >= _verificationAttempts)
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
        if (string.IsNullOrWhiteSpace(update.AvailableVersion) && plan.Kind != UpdateExecutionKind.NativeStoreQueue)
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
            UpdateExecutionKind.NativeStoreQueue when plan.StoreQueueTarget is not { } target ||
                string.IsNullOrWhiteSpace(target.ProductId) || !Version.TryParse(target.InstalledVersion, out _) ||
                target.InstalledVersion != update.InstalledVersion || target.ProductId != plan.StoreProductId ||
                target.PackageFamilyName != plan.StorePackageFamilyName ||
                !update.ApplicationIdentity.Equals("msix:" + target.PackageFamilyName, StringComparison.OrdinalIgnoreCase) ||
                plan.ProcessPolicy == UpdateProcessPolicy.PlatformManaged && plan.RunningProcessNames.Count != 0
                => "The Store queue action is not bound to the installed package identity and baseline version.",
            UpdateExecutionKind.DownloadedExe or
            UpdateExecutionKind.DownloadedMsi or
            UpdateExecutionKind.DownloadedZipMsi or
            UpdateExecutionKind.DownloadedZipDriver
                when plan.DownloadUri is null ||
                     plan.DownloadUri.Scheme != Uri.UriSchemeHttps ||
                     string.IsNullOrWhiteSpace(plan.FileName) ||
                     string.IsNullOrWhiteSpace(plan.Sha256) && string.IsNullOrWhiteSpace(plan.Sha512) &&
                     !(plan.SignatureOnly && plan.RequireAuthenticode && plan.Kind != UpdateExecutionKind.DownloadedZipDriver &&
                       (!string.IsNullOrWhiteSpace(plan.ExpectedSigner) || plan.ExpectedSigners is { Count: > 0 })) ||
                     plan.AllowedDownloadHosts.Count == 0
                => "The downloadable update plan is missing its HTTPS artifact identity or release hash.",
            // Mirror redirects are safe only because a published hash must match; without
            // a hash they would accept an installer from any HTTPS host.
            UpdateExecutionKind.DownloadedExe or
            UpdateExecutionKind.DownloadedMsi or
            UpdateExecutionKind.DownloadedZipMsi or
            UpdateExecutionKind.DownloadedZipDriver
                when plan.AllowHashVerifiedRedirects &&
                     string.IsNullOrWhiteSpace(plan.Sha256) && string.IsNullOrWhiteSpace(plan.Sha512)
                => "Mirror redirects are allowed only for an installer with a published hash.",
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
            // Store-packaged tools carry a package signature instead of per-file
            // Authenticode; they are bound to their exact package family instead.
            UpdateExecutionKind.NativeCommand when string.IsNullOrWhiteSpace(plan.ExpectedSigner) &&
                (plan.RequireAuthenticode || string.IsNullOrWhiteSpace(plan.StorePackageFamilyName))
                => "A native updater requires a trusted Authenticode publisher or an exact Store package identity.",
            UpdateExecutionKind.StorePackage
                when string.IsNullOrWhiteSpace(plan.StoreProductId) ||
                     string.IsNullOrWhiteSpace(plan.StorePackageFamilyName)
                => "The package update plan does not contain an exact Store product and package-family identity.",
            UpdateExecutionKind.WingetPackage when plan.WingetTarget is null ||
                plan.WingetTarget.SourceId != WingetPackageService.OfficialSourceId ||
                plan.WingetTarget.Version != update.AvailableVersion
                => "The WinGet update plan does not contain the approved official package and version.",
            UpdateExecutionKind.NativeStorePackage when plan.NativeStoreTarget is null ||
                plan.NativeStoreTarget.Version != update.AvailableVersion ||
                plan.NativeStoreTarget.PackageFamilyName != plan.StorePackageFamilyName ||
                plan.NativeStoreTarget.ProductId != plan.StoreProductId
                => "The native Store plan does not identify the approved package and version.",
            UpdateExecutionKind.ChocolateyPackage when plan.ChocolateyTarget is null ||
                (plan.ChocolateyTarget.PayloadVersion ?? plan.ChocolateyTarget.Version) != update.AvailableVersion ||
                plan.ChocolateyTarget.PayloadPackageId is null != plan.ChocolateyTarget.PayloadVersion is null
                => "The Chocolatey update plan does not contain the approved package and version.",
            _ => null
        };
    }
}
