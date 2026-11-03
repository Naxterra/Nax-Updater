using System.Net;
using System.Text;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

// Findings of an independent review of 0.17.52: the RST row could take its version from a
// disconnected controller, Store recovery accepted a version the live check rejects, an error
// while verifying erased a restart request, and two download failures could not end on their own.
// Each scenario is one method.
internal static class SecondReviewRegression
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        rst_row_takes_its_version_from_the_connected_controller(assert);
        store_recovery_requires_exactly_the_store_target(assert);
        await restart_request_survives_an_error_while_verifying(assert);
        await stalled_download_ends_with_a_timeout(assert);
        await ignored_byte_ranges_fall_back_to_one_download(assert);
    }

    private static void rst_row_takes_its_version_from_the_connected_controller(Action<bool, string> assert)
    {
        // The retained registration comes first, so taking the first hardware ID or the highest
        // version of the group would describe the disconnected controller.
        var retained = new InstalledHardwareDriver(@"driver-registration:{4d36e97b-e325-11ce-bfc1-08002be10318}:0001",
            "Intel(R) Chipset SATA/PCIe RST Premium Controller", "SCSIAdapter", "Intel Corporation", "Intel Corporation",
            "18.7.6.1010", null, @"PCI\VEN_8086&DEV_2822&CC_0104", "oem40.inf", false);
        var live = new InstalledHardwareDriver(@"pnp:PCI\VEN_8086&DEV_06D6&SUBSYS_7C801462&REV_00\3&11583659&0&B8",
            "Intel(R) Chipset SATA/PCIe RST Premium Controller", "SCSIAdapter", "Intel Corporation", "Intel Corporation",
            "15.44.0.1015", null, @"PCI\VEN_8086&DEV_06D6&SUBSYS_7C801462&REV_00", "iastorav.inf");
        var row = ManufacturerDriverService.AggregateDriverPackages([retained, live]).Single();
        var check = ManufacturerDriverService.CheckIntelRst(row);
        assert(row is { InstalledVersion: "15.44.0.1015", HardwareId: @"PCI\VEN_8086&DEV_06D6&SUBSYS_7C801462&REV_00", IsPresent: true } &&
               check.Status == ManufacturerDriverStatus.Available,
            $"A disconnected RST registration decided the live controller's row: {row.InstalledVersion} · {row.HardwareId} · {check.Status}");
    }

    private static void store_recovery_requires_exactly_the_store_target(Action<bool, string> assert)
    {
        var now = DateTimeOffset.UtcNow;
        var interrupted = new UpdateOperationRecord(Guid.NewGuid(), "msix:Fixture.App_123", "fixture", "Fixture App", "msix-store",
            "1.0.0.0", "2.0.0.0", "fingerprint", UpdateTransactionStage.Applying, now, now, ExecutionKind: UpdateExecutionKind.StorePackage);
        assert(!UpdateRecoveryVerifier.HasReachedTarget(interrupted, "3.0.0.0") && UpdateRecoveryVerifier.HasReachedTarget(interrupted, "2.0.0.0"),
            "Recovery recorded an interrupted Store update as done for a newer version that Windows installed on its own, or rejected the exact target.");
    }

    private static async Task restart_request_survives_an_error_while_verifying(Action<bool, string> assert)
    {
        var offer = await Offer();
        var driver = offer with { ProviderId = "manufacturer-driver:fixture" };
        // The first read after the installer throws; the second one either throws as well or
        // shows the new driver. Both must keep the restart that the installer asked for.
        var unreadable = await new UpdateTransactionCoordinator(new ThrowingAfterApply(driver, null), verificationAttempts: 1)
            .ApplyAsync(driver, "unused");
        var readOnRetry = await new UpdateTransactionCoordinator(new ThrowingAfterApply(driver, driver with { InstalledVersion = "2.0.0" }),
            verificationAttempts: 1).ApplyAsync(driver, "unused");
        assert(unreadable.RequiresRestart && readOnRetry.RequiresRestart,
            $"An error while verifying a driver that asked for a restart lost the restart: {unreadable.Stage} · {readOnRetry.Stage}");
    }

    private static async Task stalled_download_ends_with_a_timeout(Action<bool, string> assert)
    {
        var cache = Path.Combine(Path.GetTempPath(), "nax-stall-" + Guid.NewGuid().ToString("N"));
        // The outer limit only keeps the suite from hanging if the stall is not detected.
        using var outer = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Exception? failure = null;
        try
        {
            using var http = new HttpClient(new Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) }));
            var downloader = new UpdatePackageDownloader(http, new Signer(), stallTimeout: TimeSpan.FromMilliseconds(200));
            (await downloader.DownloadAndVerifyAsync(Result(Plan(Payload)), cache, null, outer.Token)).Dispose();
        }
        catch (Exception exception) { failure = exception; }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
        assert(failure is TimeoutException && !outer.IsCancellationRequested,
            $"A download whose server stopped sending did not end on its own: {failure?.GetType().Name ?? "completed"}");
    }

    private static async Task ignored_byte_ranges_fall_back_to_one_download(Action<bool, string> assert)
    {
        var cache = Path.Combine(Path.GetTempPath(), "nax-ranges-" + Guid.NewGuid().ToString("N"));
        string? error = null;
        try
        {
            // The server advertises byte ranges but answers every request with the whole file.
            using var http = new HttpClient(new Respond(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };
                response.Headers.AcceptRanges.Add("bytes");
                return response;
            }));
            var downloader = new UpdatePackageDownloader(http, new Signer(), segmentedDownloadThresholdBytes: 16);
            using var installer = await downloader.DownloadAndVerifyAsync(Result(Plan(Payload)), cache);
            if (!File.ReadAllBytes(installer.Path).AsSpan().SequenceEqual(Payload)) error = "the file differs from the payload";
        }
        catch (Exception exception) { error = exception.Message; }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
        assert(error is null, $"A server that ignores advertised byte ranges made the download fail: {error}");
    }

    private static readonly byte[] Payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("installer payload ", 64)));

    private static UpdateExecutionPlan Plan(byte[] payload) => new(UpdateExecutionKind.DownloadedExe, new Uri("https://vendor.example/setup.exe"), "setup.exe",
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)), "Vendor Inc", null, ["/S"], true, ["vendor.example"], []);

    private static UpdateCheckResult Result(UpdateExecutionPlan plan) => new("registry:fixture", "Fixture App", "1.0.0", "2.0.0",
        UpdateStatus.Available, "official:Vendor.App", "Official", "neutral", "fixture", "x64", "stable", null, "fixture", plan,
        CorrelationKey: "fixture");

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

    // The installer asks for a restart (3010); reading the installed state afterwards fails once,
    // and then fails again or returns the given state.
    private sealed class ThrowingAfterApply(UpdateCheckResult before, UpdateCheckResult? reread) : IUpdateTransactionBackend
    {
        private bool _applied;
        private int _readsAfterApply;
        public Task<UpdateCheckResult?> RevalidateAsync(UpdateCheckResult previous, CancellationToken token)
        {
            if (!_applied) return Task.FromResult<UpdateCheckResult?>(before);
            if (++_readsAfterApply == 1 || reread is null)
                return Task.FromException<UpdateCheckResult?>(new InvalidOperationException("Windows Management Instrumentation did not answer."));
            return Task.FromResult<UpdateCheckResult?>(reread);
        }
        public Task<PreparedUpdateExecution> PrepareAsync(UpdateCheckResult update, string cacheRoot, IProgress<double>? progress, CancellationToken token) =>
            Task.FromResult(new PreparedUpdateExecution(null, null, null, null, null));
        public Task<ApplicationCloseResult> QuiesceAsync(UpdateCheckResult update, CancellationToken token) =>
            Task.FromResult(new ApplicationCloseResult(true, false, []));
        public Task DiscardPreparedAsync(PreparedUpdateExecution prepared) => Task.CompletedTask;
        public Task<UpdateExecutionResult> ApplyAsync(UpdateCheckResult update, PreparedUpdateExecution prepared, CancellationToken token)
        {
            _applied = true;
            return Task.FromResult(new UpdateExecutionResult(3010, true, null));
        }
    }

    private sealed class Signer : IAuthenticodeVerifier
    {
        public AuthenticodeVerificationResult Verify(string filePath, string expectedSigner) => new(true, expectedSigner, null);
    }

    private sealed class Respond(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    // A response body that never delivers a byte, like a server that stopped sending.
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
