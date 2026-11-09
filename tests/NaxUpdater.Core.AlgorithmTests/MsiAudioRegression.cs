using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

internal static class MsiAudioRegression
{
    internal static InstalledHardwareDriver Inbox => new("driver-device:audio", "High Definition Audio Device", "MEDIA",
        "Microsoft", "Microsoft", "10.0.26100.9818", null, DriverUpdateIdentity.MsiAudioHardwareId, "hdaudio.inf");
    internal static ManufacturerDriverService.MsiDriverRelease Release => new("Realtek HD Universal Driver", "6.0.10007.1",
        new("https://download.msi.com/dvr_exe/mb/realtek_audio_R.zip"), ManufacturerDriverService.MsiAudioSha256);

    public static async Task RunAsync(Action<bool, string> assert)
    {
        var offer = ManufacturerDriverService.MsiAudioOffer(Inbox, Release)!;
        var plan = offer.ExecutionPlan!;
        assert(offer.IsInstallable && plan is { Kind: UpdateExecutionKind.DownloadedZipExe, ExtractInstallerDirectory: true,
            RequireAuthenticode: true, RequiresElevation: true, ExpectedSigner: "=Realtek Semiconductor Corp.", SignatureOnly: false } &&
            plan.Arguments.SequenceEqual(["/s"]) && plan.Sha256 == Release.Sha256 && plan.ExpectedHardwareId == Inbox.HardwareId,
            "MSI audio lacks its complete hash-protected, signed silent-install recipe.");
        foreach (var changed in new[] { Release with { Sha256 = new string('A', 64) }, Release with { Version = "6.0.10008.1" },
            Release with { Download = new("https://download.msi.com/dvr_exe/mb/other.zip") } })
            assert(ManufacturerDriverService.MsiAudioOffer(Inbox, changed) is null, "An unreviewed MSI audio archive became executable.");
        foreach (var wrong in new[] { Inbox with { HardwareId = Inbox.HardwareId + "0" }, Inbox with { HardwareId = @"HDAUDIO\OTHER" },
            Inbox with { DeviceClass = "System" }, Inbox with { IsPresent = false }, Inbox with { Provider = "Other publisher" } })
            assert(ManufacturerDriverService.MsiAudioOffer(wrong, Release) is null, "MSI audio was offered for unrelated hardware or provider.");

        var realtek = Inbox with { Provider = "Realtek Semiconductor Corp.", InstalledVersion = Release.Version, InfName = "oem42.inf" };
        var verified = DriverUpdateIdentity.ObservedState(offer, realtek, "verified");
        assert(!DriverUpdateIdentity.HasReachedTarget(Inbox, Release.Version, offer.ProviderId) &&
            !UpdateTransactionCoordinator.ReachedTarget(offer, DriverUpdateIdentity.ObservedState(offer, Inbox, "not installed")) &&
            !UpdateTransactionCoordinator.ReachedTarget(offer, verified with { InstalledVersion = Inbox.InstalledVersion }) &&
            UpdateTransactionCoordinator.ReachedTarget(offer, verified),
            "Microsoft 10.x falsely verified Realtek 6.x, or the actual Realtek target was rejected.");
        foreach (var wrong in new[] { realtek with { Provider = "Microsoft" }, realtek with { IsPresent = false },
            realtek with { HardwareId = @"HDAUDIO\OTHER" }, realtek with { InstalledVersion = "6.0.10006.1" },
            realtek with { DeviceClass = "System" } })
            assert(!DriverUpdateIdentity.HasReachedTarget(wrong, Release.Version, offer.ProviderId), "Unrelated Windows driver evidence confirmed MSI audio.");
        var now = DateTimeOffset.UtcNow;
        var operation = new UpdateOperationRecord(Guid.NewGuid(), offer.ApplicationIdentity, offer.CorrelationKey, offer.DisplayName,
            offer.ProviderId, Inbox.InstalledVersion, Release.Version, "fingerprint", UpdateTransactionStage.Applying, now, now,
            ExecutionKind: plan.Kind);
        assert(!UpdateRecoveryVerifier.HasReachedTarget(operation, Inbox.InstalledVersion, observedDriver: Inbox) &&
            !UpdateRecoveryVerifier.HasReachedTarget(operation, Release.Version) &&
            !UpdateRecoveryVerifier.HasReachedTarget(operation, Release.Version, observedDriver: realtek with { Identity = "other" }) &&
            UpdateRecoveryVerifier.HasReachedTarget(operation, Release.Version, observedDriver: realtek),
            "Audio recovery used raw version ordering or failed to require the same live Realtek device.");
        var bound = offer with { ExecutionPlan = plan with { CreatedAt = now, ExpiresAt = now.AddMinutes(5),
            CheckGenerationId = Guid.NewGuid(), InstalledVersionPrecondition = offer.InstalledVersion } };
        assert(UpdatePlanValidator.Validate(bound, now) is null &&
            UpdatePlanValidator.Validate(bound with { ExecutionPlan = bound.ExecutionPlan! with { SignatureOnly = true, Sha256 = null } }, now) is not null &&
            UpdateExecutionIntent.Fingerprint(plan) != UpdateExecutionIntent.Fingerprint(plan with { ExtractInstallerDirectory = false }),
            "Companion extraction was not bound to a release hash and the approved execution intent.");
        await PackageAsync(assert, bound);
        await TransactionsAsync(assert, bound, realtek);
        UnsafePaths(assert);
    }

    private static async Task TransactionsAsync(Action<bool, string> assert, UpdateCheckResult offer, InstalledHardwareDriver realtek)
    {
        foreach (var (driver, exitCode, expected) in new[]
        {
            (Inbox, 0, UpdateTransactionStage.Indeterminate),
            (realtek, 0, UpdateTransactionStage.Succeeded),
            (Inbox, 3010, UpdateTransactionStage.PendingReboot),
            (realtek, 3010, UpdateTransactionStage.PendingReboot),
            (Inbox, 1641, UpdateTransactionStage.PendingReboot),
            (Inbox, 1603, UpdateTransactionStage.FailedNeedsAttention)
        })
        {
            var backend = new Backend(offer, DriverUpdateIdentity.ObservedState(offer, driver, "observed"), exitCode);
            var result = await new UpdateTransactionCoordinator(backend, verificationAttempts: 1, driverVerificationAttempts: 1)
                .ApplyAsync(offer, "unused");
            assert(backend.Applied && result.Stage == expected,
                $"Audio transaction falsely skipped apply or reported {result.Stage} for {driver.Provider}, exit {exitCode}.");
        }
    }

    private sealed class Backend(UpdateCheckResult offer, UpdateCheckResult observed, int exitCode) : IUpdateTransactionBackend
    {
        public bool Applied { get; private set; }
        public Task<UpdateCheckResult?> RevalidateAsync(UpdateCheckResult previous, CancellationToken token) => Task.FromResult<UpdateCheckResult?>(Applied ? observed : offer);
        public Task<PreparedUpdateExecution> PrepareAsync(UpdateCheckResult update, string cacheRoot, IProgress<double>? progress, CancellationToken token) =>
            Task.FromResult(new PreparedUpdateExecution(null, null, null, null, null));
        public Task<ApplicationCloseResult> QuiesceAsync(UpdateCheckResult update, CancellationToken token) => Task.FromResult(new ApplicationCloseResult(true, false, []));
        public Task DiscardPreparedAsync(PreparedUpdateExecution prepared) => Task.CompletedTask;
        public Task<UpdateExecutionResult> ApplyAsync(UpdateCheckResult update, PreparedUpdateExecution prepared, CancellationToken token)
        { Applied = true; return Task.FromResult(new UpdateExecutionResult(exitCode, UpdateExecutionService.IsSuccessfulExitCode(exitCode), null)); }
    }

    private static async Task PackageAsync(Action<bool, string> assert, UpdateCheckResult offer)
    {
        var root = Path.Combine(Path.GetTempPath(), "nax-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archive = Path.Combine(root, "fixture.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                foreach (var name in new[] { "setup.exe", "setup.iss", "setup.ini", "data1.cab", "Config/device.ini", "Win64/HDXMSI.inf" })
                { using var writer = new StreamWriter(zip.CreateEntry("Realtek HD Universal Driver/" + name).Open()); writer.Write(name); }
                using (var unrelated = new StreamWriter(zip.CreateEntry("Other/setup.exe").Open()))
                    unrelated.Write("unrelated");
                zip.CreateEntry("Realtek HD Universal Driver/EmptyConfig/");
            }
            var bytes = File.ReadAllBytes(archive);
            var update = offer with { ExecutionPlan = offer.ExecutionPlan! with { Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) } };
            var signer = new Signer();
            using var http = new HttpClient(new Handler(bytes));
            var downloader = new UpdatePackageDownloader(http, signer);
            using var installer = await downloader.DownloadAndVerifyAsync(update, Path.Combine(root, "cache"), null, default);
            assert(signer.Calls == 0, "The unsigned MSI ZIP was incorrectly treated as a signed executable.");
            var execution = new UpdateExecutionService(signer);
            var prepared = await execution.PrepareAsync(update, installer);
            var preparedRoot = prepared.CleanupDirectory!;
            try
            {
                assert(signer.Calls == 1 && signer.LastPublisher == "=Realtek Semiconductor Corp." &&
                    prepared.WorkingDirectory == preparedRoot && Directory.GetFiles(preparedRoot, "*", SearchOption.AllDirectories).Length == 6 &&
                    File.Exists(Path.Combine(preparedRoot, "setup.iss")) && File.Exists(Path.Combine(preparedRoot, "Win64", "HDXMSI.inf")) &&
                    Directory.Exists(Path.Combine(preparedRoot, "EmptyConfig")),
                    "The audio installer lost its response file, CABs, INF or working directory before apply.");
                var command = UpdateExecutionService.CreateInstallerStartInfo(update.ExecutionPlan!, prepared);
                assert(command.FileName == prepared.ExecutablePath && command.WorkingDirectory == preparedRoot &&
                    command.UseShellExecute && command.Verb == "runas" && command.ArgumentList.SequenceEqual(["/s"]),
                    "The audio installer would run without its publisher response file, correct working directory or elevation.");
                var blocked = false;
                try { File.WriteAllText(Path.Combine(preparedRoot, "data1.cab"), "tampered"); }
                catch (IOException) { blocked = true; }
                assert(blocked, "A prepared MSI companion file could be changed before execution.");
            }
            finally { execution.DiscardPrepared(prepared); }
            assert(!Directory.Exists(preparedRoot), "Discard left the prepared audio payload behind.");
            using var rejectedInstaller = new VerifiedInstaller(archive, "fixture");
            var wrongSigner = new UpdateExecutionService(new Signer { Accept = false });
            var rejected = false;
            try { wrongSigner.DiscardPrepared(await wrongSigner.PrepareAsync(update, rejectedInstaller)); }
            catch (InvalidDataException) { rejected = true; }
            assert(rejected, "MSI audio could run with an unapproved Authenticode publisher.");
            var changed = update with { ExecutionPlan = update.ExecutionPlan! with { Sha256 = new string('B', 64) } };
            rejected = false;
            try { (await downloader.DownloadAndVerifyAsync(changed, Path.Combine(root, "bad-cache"), null, default)).Dispose(); }
            catch (InvalidDataException) { rejected = true; }
            assert(rejected, "An archive with the wrong MSI release hash was accepted.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void UnsafePaths(Action<bool, string> assert)
    {
        foreach (var bad in new[] { "../escape.dll", "Config/../../escape.dll", "setup.exe", "SETUP.EXE", "data.cab:stream", "NUL.txt", "Config./bad", "Config\\..\\escape", "link.dll" })
        {
            var root = Path.Combine(Path.GetTempPath(), "nax-audio-path-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var archive = Path.Combine(root, "bad.zip");
                using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                    foreach (var path in new[] { "Audio/setup.exe", "Audio/" + bad })
                    {
                        var entry = zip.CreateEntry(path);
                        if (path.EndsWith("link.dll", StringComparison.Ordinal)) entry.ExternalAttributes = unchecked((int)0xA1FF0000);
                        using var writer = new StreamWriter(entry.Open()); writer.Write("fixture");
                    }
                var rejected = false;
                try
                {
                    var payload = UpdateExecutionService.ExtractNestedInstaller(archive, "Audio/setup.exe", Path.Combine(root, "out"), ".exe", true);
                    foreach (var hold in payload.ContentLocks) hold.Dispose();
                }
                catch (InvalidDataException) { rejected = true; }
                assert(rejected && !File.Exists(Path.Combine(root, "escape.dll")), "Unsafe or duplicate audio companion paths were extracted.");
            }
            finally { Directory.Delete(root, true); }
        }
    }

    private sealed class Handler(byte[] bytes) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); }
    private sealed class Signer : IAuthenticodeVerifier
    {
        public bool Accept { get; init; } = true;
        public int Calls { get; private set; }
        public string? LastPublisher { get; private set; }
        public AuthenticodeVerificationResult Verify(string path, string signer)
        { Calls++; LastPublisher = signer; return new(Accept && path.EndsWith("setup.exe", StringComparison.OrdinalIgnoreCase), signer, Accept ? null : "Rejected fixture"); }
    }
}
