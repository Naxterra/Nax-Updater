using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

// Findings of the 2026-10-18 review: same-version Chocolatey repairs, wrapper-route
// plan validation, the desktop-launch fallback and prerelease product names.
internal static class ReviewFindingsRegression
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        var app = new InstalledApplication("registry:fixture-choco", "Fixture App", "Fixture", "2.0.0", "2.0.0", "fixture",
            null, "fixture", null, null, InstallScope.Machine, ManagementMode.Registry, ConfidenceLevel.High, false, [], null, []);
        async Task<UpdateCheckResult> Bound(InstalledApplication application, string available, ChocolateyUpdateTarget target) =>
            (await new UpdateCheckService([new Offer(available, target)]).CheckAsync(new(DateTimeOffset.UtcNow, [application], [], []))).Results.Single();

        // Finding 3: Chocolatey's record says 1.0.0 while 2.0.0 is installed; the repair
        // reinstalls 2.0.0. The version is "reached" before anything runs, so only a
        // successful execution may count.
        var repair = await Bound(app, "2.0.0", new("fixture", "2.0.0"));
        assert(repair.IsInstallable, "The same-version Chocolatey repair fixture was not installable.");
        foreach (var (execution, expected) in new[]
        {
            (new UpdateExecutionResult(1223, false, "The Windows elevation prompt was canceled."), UpdateTransactionStage.CanceledBeforeChange),
            (new UpdateExecutionResult(1603, false, "Chocolatey upgrade failed (exit code 1603)."), UpdateTransactionStage.FailedNeedsAttention),
            (new UpdateExecutionResult(0, false, "Chocolatey reported success, but fixture is still 1.0.0 instead of 2.0.0"), UpdateTransactionStage.FailedNeedsAttention),
            (new UpdateExecutionResult(0, true, null), UpdateTransactionStage.Succeeded)
        })
        {
            var outcome = await new UpdateTransactionCoordinator(new Backend(repair, repair, execution), verificationAttempts: 1).ApplyAsync(repair, "unused");
            assert(outcome.Stage == expected,
                $"A same-version repair with exit {execution.ExitCode} (success={execution.IsSuccess}) ended as {outcome.Stage} instead of {expected}.");
        }
        // A real version advance keeps its recovery: the installer failed, but the
        // application reached the target, so it is a success.
        var older = app with { NormalizedVersion = "1.0.0", InstalledVersion = "1.0.0" };
        var upgrade = await Bound(older, "2.0.0", new("fixture", "2.0.0"));
        var advanced = upgrade with { InstalledVersion = "2.0.0", AvailableVersion = null, Status = UpdateStatus.Current, ExecutionPlan = null };
        var recovered = await new UpdateTransactionCoordinator(new Backend(upgrade, advanced, new UpdateExecutionResult(1603, false, "failed")),
            verificationAttempts: 1).ApplyAsync(upgrade, "unused");
        assert(recovered.Stage == UpdateTransactionStage.Succeeded, "A version advance that reached its target was no longer recognized.");

        // Finding 4: a wrapper route runs nodejs 9.0.0, which brings nodejs.install 2.0.0.
        var wrapper = await Bound(older, "2.0.0", new("nodejs", "9.0.0", "nodejs.install", "2.0.0"));
        assert(wrapper.IsInstallable && wrapper.Status == UpdateStatus.Available,
            $"A wrapper route with different root and payload versions failed validation: {wrapper.Status} · {wrapper.Message}");
        var wrongPayload = await Bound(older, "2.0.0", new("nodejs", "9.0.0", "nodejs.install", "2.0.1"));
        var halfPayload = await Bound(older, "2.0.0", new("nodejs", "2.0.0", "nodejs.install", null));
        assert(wrongPayload.Status == UpdateStatus.Error && halfPayload.Status == UpdateStatus.Error,
            "A Chocolatey plan whose payload does not match the approved version, or names only half of it, was accepted.");
        var records = new Dictionary<string, ChocolateyPackageDetails>(StringComparer.OrdinalIgnoreCase)
            { ["nodejs"] = new("nodejs", "9.0.0", null), ["nodejs.install"] = new("nodejs.install", "1.0.0", null) };
        assert(ChocolateyPackageService.RecordMismatch(records, "nodejs", "9.0.0") is null &&
               ChocolateyPackageService.RecordMismatch(records, "nodejs.install", "2.0.0")?.Contains("still 1.0.0") == true &&
               ChocolateyPackageService.RecordMismatch(records, "missing", "1.0.0")?.Contains("no record") == true,
            "Chocolatey's package records were not checked for the run package, the payload, or a missing record.");

        // Finding 5: a desktop shell that denies access keeps the current window.
        assert(UpdateHostLifetime.IsDesktopLaunchFailure(new UnauthorizedAccessException()) &&
               UpdateHostLifetime.IsDesktopLaunchFailure(new System.Runtime.InteropServices.COMException()) &&
               !UpdateHostLifetime.IsDesktopLaunchFailure(new OutOfMemoryException()),
            "Access denied from the desktop shell would escape the relaunch fallback.");

        // WinRAR 7.30 Beta 1 must still own its MSIX shell extension.
        assert(MsixIntegrationCorrelator.CanonicalProductName("WinRAR 7.30 Beta 1 (64-Bit)") == MsixIntegrationCorrelator.CanonicalProductName("WinRAR") &&
               MsixIntegrationCorrelator.CanonicalProductName("WinRAR 7.23.0 (64-bit)") == MsixIntegrationCorrelator.CanonicalProductName("WinRAR") &&
               MsixIntegrationCorrelator.CanonicalProductName("App 2.0 RC2") == MsixIntegrationCorrelator.CanonicalProductName("App"),
            "A prerelease display name kept its version label and lost its MSIX integration package.");
    }

    private sealed class Offer(string available, ChocolateyUpdateTarget target) : IUpdateProvider
    {
        public string Id => "chocolatey-installed";
        public UpdateProviderDescriptor Descriptor { get; } = new(UpdateProviderAuthority.FallbackCatalog, 30, "fixture");
        public bool CanHandle(InstalledApplication application) => true;
        public Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken token) => Task.FromResult(new UpdateCheckResult(
            application.Identity, application.DisplayName, application.NormalizedVersion, available, UpdateStatus.Available, Id, "Chocolatey",
            "neutral", "fixture", "x64", "stable", null, "fixture",
            new UpdateExecutionPlan(UpdateExecutionKind.ChocolateyPackage, null, null, null, null, null, [], true, [], [], ChocolateyTarget: target)));
    }

    // Returns the approved offer until the installer ran, then the observed state.
    private sealed class Backend(UpdateCheckResult before, UpdateCheckResult after, UpdateExecutionResult execution) : IUpdateTransactionBackend
    {
        private bool _applied;
        public Task<UpdateCheckResult?> RevalidateAsync(UpdateCheckResult previous, CancellationToken token) =>
            Task.FromResult<UpdateCheckResult?>(_applied ? after : before);
        public Task<PreparedUpdateExecution> PrepareAsync(UpdateCheckResult update, string cacheRoot, IProgress<double>? progress, CancellationToken token) =>
            Task.FromResult(new PreparedUpdateExecution(null, null, null, null, null));
        public Task<ApplicationCloseResult> QuiesceAsync(UpdateCheckResult update, CancellationToken token) =>
            Task.FromResult(new ApplicationCloseResult(true, false, []));
        public Task DiscardPreparedAsync(PreparedUpdateExecution prepared) => Task.CompletedTask;
        public Task<UpdateExecutionResult> ApplyAsync(UpdateCheckResult update, PreparedUpdateExecution prepared, CancellationToken token)
        {
            _applied = true;
            return Task.FromResult(execution);
        }
    }
}
