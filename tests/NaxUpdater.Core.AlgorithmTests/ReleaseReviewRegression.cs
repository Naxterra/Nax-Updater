using System.Diagnostics;
using System.Net;
using System.Text;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

// Findings of the release review of version 0.17.29. Each method is one scenario.
internal static class ReleaseReviewRegression
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        signature_only_installer_must_declare_the_approved_version(assert);
        await download_url_without_version_requires_the_installer_version(assert);
        await signature_only_installer_is_downloaded_again_instead_of_reused(assert);
        mirror_redirects_without_a_published_hash_are_rejected(assert);
        update_across_a_major_version_is_recognized_by_its_identity(assert);
        await installer_exit_code_1223_is_not_a_canceled_uac_prompt(assert);
        await refused_start_is_recorded_as_failed_before_change(assert);
        await journal_write_failure_keeps_the_verified_outcome(assert);
        await journal_that_cannot_be_started_blocks_the_update(assert);
        await journal_that_cannot_save_the_applying_step_stops_before_the_installer(assert);
    }

    // Recovery reads the saved stage; an installer that ran while the journal still said
    // "Quiescing" would be recovered as "canceled before change".
    private static async Task journal_that_cannot_save_the_applying_step_stops_before_the_installer(Action<bool, string> assert)
    {
        var offer = await Offer();
        var backend = new Backend(offer, new UpdateExecutionResult(0, true, null));
        var result = await new UpdateTransactionCoordinator(backend, new Journal(failOn: UpdateTransactionStage.Applying), verificationAttempts: 1)
            .ApplyAsync(offer, "unused");
        assert(result.Stage == UpdateTransactionStage.FailedBeforeChange && !backend.Applied,
            $"An installer ran although its \"Applying\" step could not be saved in the journal ({result.Stage}).");
    }

    // PotPlayer's "Latest" installer declares 26.10.21.0 when the page lists 261021.
    private static void signature_only_installer_must_declare_the_approved_version(Action<bool, string> assert)
    {
        var self = Environment.ProcessPath!;
        var declared = FileVersionInfo.GetVersionInfo(self).FileVersion!;
        var parts = declared.Split('.');
        var plan = Plan() with { ExpectedInstallerVersion = $"{parts[0]}.{parts[1]}" };
        var wrong = Plan() with { ExpectedInstallerVersion = $"{int.Parse(parts[0]) + 1}.0" };
        assert(UpdatePackageDownloader.InstallerVersionMismatch(self, plan) is null &&
               UpdatePackageDownloader.InstallerVersionMismatch(self, wrong)?.Contains("not the approved") == true &&
               UpdatePackageDownloader.InstallerVersionMismatch(self, Plan()) is null,
            "An installer whose version resource differs from the approved version was accepted, or a matching one was rejected.");
    }

    private static async Task download_url_without_version_requires_the_installer_version(Action<bool, string> assert)
    {
        OfficialSiteRecipe Template(string url, string? format) => new()
        {
            Id = "t", DisplayNamePattern = "t", VersionUrl = "https://t.example/", VersionPattern = "t", DownloadUrl = url, VersionFormat = format,
            AllowedHosts = ["t.example"], InstallerKind = UpdateExecutionKind.DownloadedExe
        };
        assert(OfficialSiteUpdateProvider.TemplateNamesVersion(Template("https://download.internetdownloadmanager.com/idman{maj}{min}build{build}.exe", "{maj}.{min}.{build}")) &&
               OfficialSiteUpdateProvider.TemplateNamesVersion(Template("https://us.download.nvidia.com/nvapp/client/{version}/NVIDIA_app_v{version}.exe", null)) &&
               !OfficialSiteUpdateProvider.TemplateNamesVersion(Template("https://t1.daumcdn.net/potplayer/PotPlayer/Version/Latest/PotPlayerSetup64.exe", "{yy}.{mm}.{dd}")) &&
               !OfficialSiteUpdateProvider.TemplateNamesVersion(Template("https://vendor64.example/win64/latest/setup.exe", null)) &&
               !OfficialSiteUpdateProvider.TemplateNamesVersion(Template("https://javadl.oracle.com/webapps/download/AutoDL?BundleId={bundle}", "8.0.{update}0")),
            "A download was judged as tied, or not tied, to its version by digits instead of by the recipe template.");
        var shipped = await UpdateProviderCatalogLoader.LoadAsync(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../../src/NaxUpdater/Configuration/update-providers.json")));
        var unbound = shipped.Official.Where(static recipe => recipe.SignatureOnly && !recipe.VerifyInstallerVersion &&
            !OfficialSiteUpdateProvider.TemplateNamesVersion(recipe) && recipe.InstallerKind == UpdateExecutionKind.DownloadedExe).Select(static recipe => recipe.Id).ToArray();
        assert(shipped.Official.Count(static recipe => recipe.SignatureOnly) > 0,
            "The shipped recipe file was not read.");
        assert(shipped.Official.Where(static recipe => recipe.SignatureOnly)
                   .All(static recipe => recipe.VerifyInstallerVersion || OfficialSiteUpdateProvider.TemplateNamesVersion(recipe)),
            $"A shipped signature-only recipe is tied to its version neither by its URL nor by an installer-version check: {string.Join(", ", unbound)}");
        using var http = new HttpClient(new Page("[261021] new release"));
        OfficialSiteRecipe Recipe(UpdateExecutionKind kind) => new()
        {
            Id = "Vendor.Latest", DisplayNamePattern = "^Latest App$", VersionUrl = "https://vendor.example/news",
            VersionPattern = @"\[(?<yy>[0-9]{2})(?<mm>[0-9]{2})(?<dd>[0-9]{2})\]", VersionFormat = "{yy}.{mm}.{dd}",
            DownloadUrl = "https://vendor.example/Latest/setup.exe", ExpectedSigner = "Vendor Inc", SignatureOnly = true,
            AllowedHosts = ["vendor.example"], Architecture = "neutral", InstallerKind = kind, InstallerArguments = ["/S"], RequiresElevation = true
        };
        var app = new InstalledApplication("fixture:latest", "Latest App", "Vendor", "26.08.19", "26.08.19", "fixture",
            Environment.ProcessPath, "fixture", null, null, InstallScope.Machine, ManagementMode.Registry, ConfidenceLevel.High, false, [], null, []);
        var exe = await new OfficialSiteUpdateProvider(http, Recipe(UpdateExecutionKind.DownloadedExe)).CheckAsync(app, CancellationToken.None);
        var msi = await new OfficialSiteUpdateProvider(http, Recipe(UpdateExecutionKind.DownloadedMsi)).CheckAsync(app, CancellationToken.None);
        assert(exe is { Status: UpdateStatus.Available, ExecutionPlan.ExpectedInstallerVersion: "26.10.21" } &&
               msi is { Status: UpdateStatus.NewerReleaseKnown, ExecutionPlan: null },
            "A signature-only download without its version in the URL did not require the installer's own version, or an MSI that cannot be checked was offered.");
    }

    private static async Task signature_only_installer_is_downloaded_again_instead_of_reused(Action<bool, string> assert)
    {
        var payload = Encoding.UTF8.GetBytes("signature-only installer");
        var requests = 0;
        using var http = new HttpClient(new Counting(payload, () => requests++));
        var cache = Path.Combine(Path.GetTempPath(), "nax-sigonly-" + Guid.NewGuid().ToString("N"));
        try
        {
            var update = Result(Plan() with { SignatureOnly = true, Sha256 = null });
            var downloader = new UpdatePackageDownloader(http, new Signer("Vendor Inc"));
            (await downloader.DownloadAndVerifyAsync(update, cache)).Dispose();
            (await downloader.DownloadAndVerifyAsync(update, cache)).Dispose();
            assert(requests == 2, $"A cached signature-only installer was reused without a new download ({requests} downloads).");
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }

    private static void mirror_redirects_without_a_published_hash_are_rejected(Action<bool, string> assert)
    {
        var signatureOnly = Bound(Plan() with { SignatureOnly = true, Sha256 = null, AllowHashVerifiedRedirects = true });
        var hashed = Bound(Plan() with { AllowHashVerifiedRedirects = true });
        assert(UpdatePlanValidator.Validate(signatureOnly, DateTimeOffset.UtcNow)?.Contains("Mirror redirects") == true &&
               UpdatePlanValidator.Validate(hashed, DateTimeOffset.UtcNow) is null,
            "Mirror redirects were allowed for an installer without a published hash, or refused for one with a hash.");
    }

    // "7-Zip 24.09" becomes "7-Zip 25.01": the correlation key changes, the identity does not.
    private static void update_across_a_major_version_is_recognized_by_its_identity(Action<bool, string> assert)
    {
        var expected = Result(Plan()) with { AvailableVersion = "25.01", CorrelationKey = "versioned-name:IGOR PAVLOV|7-ZIP (X64)|24" };
        var observed = expected with { InstalledVersion = "25.01", CorrelationKey = "versioned-name:IGOR PAVLOV|7-ZIP (X64)|25" };
        var other = observed with { ApplicationIdentity = "registry:other" };
        assert(UpdateTransactionCoordinator.ReachedTarget(expected, observed) && !UpdateTransactionCoordinator.ReachedTarget(expected, other),
            "An update across a major version was not recognized by its identity, or another application was accepted.");
    }

    private static async Task installer_exit_code_1223_is_not_a_canceled_uac_prompt(Action<bool, string> assert)
    {
        var offer = await Offer();
        var ran = await new UpdateTransactionCoordinator(new Backend(offer, new UpdateExecutionResult(1223, false, "The installer was canceled.")),
            verificationAttempts: 1).ApplyAsync(offer, "unused");
        var refused = await new UpdateTransactionCoordinator(new Backend(offer, new UpdateExecutionResult(1223, false, "UAC canceled", NotStarted: true)),
            verificationAttempts: 1).ApplyAsync(offer, "unused");
        assert(ran.Stage == UpdateTransactionStage.FailedNeedsAttention && refused.Stage == UpdateTransactionStage.CanceledBeforeChange,
            $"An installer that ran and returned 1223 ended as {ran.Stage}, or a canceled UAC prompt as {refused.Stage}.");
    }

    private static async Task refused_start_is_recorded_as_failed_before_change(Action<bool, string> assert)
    {
        var offer = await Offer();
        async Task<UpdateTransactionStage> Run(Exception exception) =>
            (await new UpdateTransactionCoordinator(new Backend(offer, null, exception), verificationAttempts: 1).ApplyAsync(offer, "unused")).Stage;
        var reopened = await Run(new ApplicationStillRunningException(["App"]));
        var changed = await Run(new UpdateExecutionRefusedException("The prepared update payload changed after verification."));
        var broken = await Run(new IOException("pipe broken"));
        assert(reopened == UpdateTransactionStage.FailedBeforeChange && changed == UpdateTransactionStage.FailedBeforeChange &&
               broken == UpdateTransactionStage.FailedNeedsAttention,
            $"A refused start was not recorded as failed before change ({reopened}, {changed}), or an unknown failure was not left for attention ({broken}).");
    }

    private static async Task journal_write_failure_keeps_the_verified_outcome(Action<bool, string> assert)
    {
        var offer = await Offer();
        var reached = offer with { InstalledVersion = "2.0.0", Status = UpdateStatus.Current, ExecutionPlan = null };
        var result = await new UpdateTransactionCoordinator(new Backend(offer, new UpdateExecutionResult(0, true, null), after: reached),
            new Journal(failOn: UpdateTransactionStage.Succeeded), verificationAttempts: 1).ApplyAsync(offer, "unused");
        assert(result.Stage == UpdateTransactionStage.Succeeded && result.JournalWarning?.Contains("could not be saved") == true,
            "A journal write failure after a verified install hid the outcome or ended the transaction with an exception.");
    }

    private static async Task journal_that_cannot_be_started_blocks_the_update(Action<bool, string> assert)
    {
        var offer = await Offer();
        var backend = new Backend(offer, new UpdateExecutionResult(0, true, null));
        var result = await new UpdateTransactionCoordinator(backend, new Journal(failOnBegin: true), verificationAttempts: 1).ApplyAsync(offer, "unused");
        assert(result.Stage == UpdateTransactionStage.FailedBeforeChange && !backend.Applied,
            "An update ran although its journal entry could not be written.");
    }

    private static UpdateExecutionPlan Plan() => new(UpdateExecutionKind.DownloadedExe, new Uri("https://vendor.example/setup.exe"), "setup.exe",
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("signature-only installer"))),
        "Vendor Inc", null, ["/S"], true, ["vendor.example"], []);

    private static UpdateCheckResult Result(UpdateExecutionPlan plan) => new("registry:fixture", "Fixture App", "1.0.0", "2.0.0",
        UpdateStatus.Available, "official:Vendor.App", "Official", "neutral", "fixture", "x64", "stable", null, "fixture", plan,
        CorrelationKey: "fixture");

    private static UpdateCheckResult Bound(UpdateExecutionPlan plan) => Result(plan with
    {
        CheckGenerationId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15), InstalledVersionPrecondition = "1.0.0"
    });

    // A bound offer produced by the real check pipeline, so the coordinator's validation applies.
    private static async Task<UpdateCheckResult> Offer()
    {
        var app = new InstalledApplication("registry:fixture", "Fixture App", "Vendor", "1.0.0", "1.0.0", "fixture",
            null, "fixture", null, null, InstallScope.Machine, ManagementMode.Registry, ConfidenceLevel.High, false, [], null, []);
        return (await new UpdateCheckService([new Provider()]).CheckAsync(new(DateTimeOffset.UtcNow, [app], [], []))).Results.Single();
    }

    private sealed class Provider : IUpdateProvider
    {
        public string Id => "native-fixture";
        public UpdateProviderDescriptor Descriptor { get; } = new(UpdateProviderAuthority.ProducerRelease, 50, "fixture");
        public bool CanHandle(InstalledApplication application) => true;
        public Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken token) => Task.FromResult(new UpdateCheckResult(
            application.Identity, application.DisplayName, application.NormalizedVersion, "2.0.0", UpdateStatus.Available, Id, Id, "neutral", "fixture",
            "x64", "stable", null, "fixture",
            new UpdateExecutionPlan(UpdateExecutionKind.NativeCommand, null, null, null, "Fixture", @"C:\Fixture\updater.exe", [], false, [], [])));
    }

    private sealed class Backend(UpdateCheckResult before, UpdateExecutionResult? execution, Exception? failure = null, UpdateCheckResult? after = null)
        : IUpdateTransactionBackend
    {
        public bool Applied { get; private set; }
        public Task<UpdateCheckResult?> RevalidateAsync(UpdateCheckResult previous, CancellationToken token) =>
            Task.FromResult<UpdateCheckResult?>(Applied && after is not null ? after : before);
        public Task<PreparedUpdateExecution> PrepareAsync(UpdateCheckResult update, string cacheRoot, IProgress<double>? progress, CancellationToken token) =>
            Task.FromResult(new PreparedUpdateExecution(null, null, null, null, null));
        public Task<ApplicationCloseResult> QuiesceAsync(UpdateCheckResult update, CancellationToken token) =>
            Task.FromResult(new ApplicationCloseResult(true, false, []));
        public Task DiscardPreparedAsync(PreparedUpdateExecution prepared) => Task.CompletedTask;
        public Task<UpdateExecutionResult> ApplyAsync(UpdateCheckResult update, PreparedUpdateExecution prepared, CancellationToken token)
        {
            Applied = true;
            return failure is null ? Task.FromResult(execution!) : Task.FromException<UpdateExecutionResult>(failure);
        }
    }

    private sealed class Journal(UpdateTransactionStage? failOn = null, bool failOnBegin = false) : IUpdateOperationJournal
    {
        public UpdateOperationRecord Begin(UpdateCheckResult update, DateTimeOffset now) => failOnBegin
            ? throw new IOException("The disk is full.")
            : new(Guid.NewGuid(), update.ApplicationIdentity, update.CorrelationKey, update.DisplayName, update.ProviderId,
                update.InstalledVersion, update.AvailableVersion, "fingerprint", UpdateTransactionStage.Created, now, now);
        public UpdateOperationRecord Record(UpdateOperationRecord operation, UpdateTransactionStage stage, DateTimeOffset now, string? error = null) =>
            stage == failOn ? throw new IOException("update-operation.json.tmp is locked.") : operation with { Stage = stage, UpdatedAt = now, Error = error };
        public UpdateOperationRecord? ReadLatest() => null;
        public UpdateOperationRecord? ReadIncomplete() => null;
    }

    private sealed class Signer(string actual) : IAuthenticodeVerifier
    {
        public AuthenticodeVerificationResult Verify(string filePath, string expectedSigner) =>
            actual.Equals(expectedSigner, StringComparison.OrdinalIgnoreCase) ? new(true, actual, null) : new(false, null, "wrong signer");
    }

    private sealed class Page(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body) });
    }

    private sealed class Counting(byte[] payload, Action counted) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            counted();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(payload) });
        }
    }
}
