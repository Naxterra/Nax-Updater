using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;
using System.Net;
using System.Text.Json;
using Windows.ApplicationModel.Store.Preview.InstallControl;

internal static class StoreBatchRegression
{
    private const string Empty = "Checking for updates…\r\n\r\nNo updates found.\r\n";

    public static async Task RunAsync(Action<bool, string> assert, string fixture)
    {
        OutputMustProveNoUpdates(assert);
        await SharedQueryAndCancellationAsync(assert);
        await BatchAndFallbackAsync(assert, fixture);
        await ReusedServiceStartsFreshBatchAsync(assert);
    }

    private static void OutputMustProveNoUpdates(Action<bool, string> assert)
    {
        assert(StoreUpdateBatchQuery.Parse(new(0, Empty, "")) == StoreBatchCheck.NoUpdates,
            "The complete successful Store no-update response was rejected.");
        foreach (var result in new[] {
            new ProcessQueryResult(1, Empty, ""), new(0, Empty, "warning"), new(0, "", ""),
            new(0, "No updates found.", ""), new(0, Empty + "Error: one app could not be checked.", ""),
            new(0, "Checking for updates…\nFixture app: update available", ""),
            new(0, "Nach Updates suchen…\nKeine Updates gefunden.", ""),
            new(0, "\u001b[31m" + Empty, ""), new(0, new string('x', 65537), "") })
            assert(StoreUpdateBatchQuery.Parse(result) == StoreBatchCheck.CheckIndividually,
                "A failed, partial, localized, ambiguous or positive Store response was treated as Current.");
    }

    private static async Task SharedQueryAndCancellationAsync(Action<bool, string> assert)
    {
        var calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ProcessQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var batch = new StoreUpdateBatchQuery(() => "trusted-store.exe", (path, args, timeout, token) =>
        {
            Interlocked.Increment(ref calls);
            assert(path == "trusted-store.exe" && args.SequenceEqual(["updates"]) && timeout == StoreUpdateBatchQuery.QueryTimeout,
                "The batch command used a deployment, filter or shell argument, or lost its deadline.");
            started.TrySetResult();
            return release.Task;
        });
        using var canceled = new CancellationTokenSource();
        var first = batch.CheckAsync(canceled.Token);
        await started.Task;
        var siblings = Enumerable.Range(0, 40).Select(_ => batch.CheckAsync(CancellationToken.None)).ToArray();
        canceled.Cancel();
        try { await first; assert(false, "Canceling a batch waiter did not stop waiting."); }
        catch (OperationCanceledException) { assert(true, "Batch waiter cancellation propagated."); }
        release.SetResult(new(0, Empty, ""));
        assert((await Task.WhenAll(siblings)).All(r => r == StoreBatchCheck.NoUpdates) && calls == 1,
            "Parallel Store families launched duplicate batch processes, or one waiter canceled the shared query.");

        var missing = new StoreUpdateBatchQuery(() => null, (_, _, _, _) => throw new InvalidOperationException("Must not run."));
        assert(await missing.CheckAsync(CancellationToken.None) == StoreBatchCheck.Unavailable,
            "A missing trusted Store CLI was treated as a completed check.");
        var timedOut = new StoreUpdateBatchQuery(() => "trusted-store.exe",
            async (_, _, _, token) => { await Task.Delay(Timeout.Infinite, token); return new(0, Empty, ""); }, TimeSpan.FromMilliseconds(40));
        assert(await timedOut.CheckAsync(CancellationToken.None) == StoreBatchCheck.CheckIndividually,
            "A timed-out Store batch was treated as a successful empty result.");
        var failure = new StoreUpdateBatchQuery(() => "trusted-store.exe", (_, _, _, _) => throw new IOException("Cannot start."));
        assert(await failure.CheckAsync(CancellationToken.None) == StoreBatchCheck.CheckIndividually,
            "A process-launch failure was treated as a completed no-update check.");
    }

    private static async Task BatchAndFallbackAsync(Action<bool, string> assert, string fixture)
    {
        using var http = new HttpClient(new Metadata());
        var native = new Native();
        var calls = 0;
        var batch = new StoreUpdateBatchQuery(() => "trusted-store.exe", async (_, _, _, token) =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(40, token);
            return new(0, Empty, "");
        });
        var provider = new MsixStoreUpdateProvider(http, new NoStoreUpdate(), native, null, batch);
        var apps = Enumerable.Range(0, 40).Select(i => App("Fixture.App" + i + "_publisher")).ToArray();
        var snapshot = await new UpdateCheckService([provider]).CheckAsync(new(DateTimeOffset.UtcNow, apps, [], []));
        assert(snapshot.Results.Count == 40 && snapshot.AllCurrent && calls == 1 && native.Queries.Count == 0,
            "A cold no-update scan did not replace all per-SKU native queries with one shared Store check.");
        assert(snapshot.Results.All(r => !r.IsInstallable && r.AvailabilityReason == UpdateAvailabilityReason.NoApplicableStoreUpdate),
            "A no-update Store batch invented an offer or lost its checked status.");

        var app = App("Fixture.App_publisher");
        foreach (var batchResult in new[] { new ProcessQueryResult(0, "Updates found: fixture", ""), new(7, Empty, "error"), new(0, "unknown response", "") })
        {
            native = new Native();
            batch = new StoreUpdateBatchQuery(() => "trusted-store.exe", (_, _, _, _) => Task.FromResult(batchResult));
            var cache = new StoreSearchCache(Path.Combine(fixture, "batch-" + Guid.NewGuid().ToString("N") + ".json"));
            cache.RecordNoUpdate(StoreSearchCache.Key("Fixture.App_publisher", "1.0.0.0", "2.0.0.0"));
            provider = new(http, new NoStoreUpdate(), native, cache, batch);
            var result = await provider.CheckAsync(app, CancellationToken.None);
            assert(result.IsInstallable && result.AvailableVersion == "2.0.0.0" &&
                result.ExecutionPlan?.NativeStoreTarget?.SkuId == "0011" && native.Queries.SequenceEqual(["0010", "0011"]),
                "An offer, batch failure or changed format bypassed exact native target checks or reused a stale cached negative.");
        }
        native = new Native { Fail = true };
        batch = new StoreUpdateBatchQuery(() => "trusted-store.exe", (_, _, _, _) => Task.FromResult(new ProcessQueryResult(1, "error", "")));
        provider = new(http, new NoStoreUpdate(), native, null, batch);
        assert((await provider.CheckAsync(app, CancellationToken.None)).Status == UpdateStatus.Error,
            "A failed batch and failed native fallback were falsely reported Current.");

        var queue = new NativeStoreQueueEntry("9FIXTURE", "Fixture.App_publisher", AppInstallState.Pending);
        foreach (var arrivesDuringBatch in new[] { false, true })
        {
            native = new Native { Queue = arrivesDuringBatch ? null : queue };
            calls = 0;
            var queueNative = native;
            batch = new StoreUpdateBatchQuery(() => "trusted-store.exe", (_, _, _, _) =>
            {
                calls++;
                queueNative.Queue = queue;
                return Task.FromResult(new ProcessQueryResult(0, Empty, ""));
            });
            provider = new(http, new NoStoreUpdate(), native, null, batch);
            var queued = await provider.CheckAsync(app, CancellationToken.None);
            assert(queued.Status == UpdateStatus.StoreQueued && queued.IsInstallable &&
                calls == (arrivesDuringBatch ? 1 : 0) && native.Queries.Count == 0,
                "A preexisting or newly queued Store operation was hidden by the batch result.");
        }
    }

    private static async Task ReusedServiceStartsFreshBatchAsync(Action<bool, string> assert)
    {
        using var http = new HttpClient(new Metadata());
        var native = new Native();
        var calls = 0;
        var batch = new StoreUpdateBatchQuery(() => "trusted-store.exe", (_, _, _, _) =>
            Task.FromResult(new ProcessQueryResult(0, ++calls == 1 ? Empty : "An update is available.", "")));
        var service = new UpdateCheckService([new MsixStoreUpdateProvider(http, new NoStoreUpdate(), native, null, batch)]);
        var inventory = new InventorySnapshot(DateTimeOffset.UtcNow, [App("Fixture.App_publisher")], [], []);
        assert((await service.CheckAsync(inventory)).AllCurrent, "The initial Store batch did not finish as current.");
        var next = await service.CheckAsync(inventory);
        assert(calls == 2 && next.Results.Single().IsInstallable,
            "Reusing the checker carried a previous no-update batch into a new scan and hid an update.");
    }

    private static InstalledApplication App(string family) => new("msix:" + family, family, "Fixture", "1.0.0.0", "1.0.0.0", "fixture",
        null, null, null, null, InstallScope.CurrentUser, ManagementMode.Msix, ConfidenceLevel.High, false, [], null,
        [new(EvidenceKind.MsixPackage, "MSIX package architecture", "x64", true)]);
    private sealed class Native : INativeStoreUpdateService
    {
        public List<string> Queries = [];
        public bool Fail;
        public NativeStoreQueueEntry? Queue;
        public Task<NativeStoreQueueEntry?> ReadQueueAsync(string family, CancellationToken token) => Task.FromResult(Queue);
        public Task<NativeStoreOffer> CheckIdentityAsync(StoreProductIdentity identity, CancellationToken token)
        {
            Queries.Add(identity.SkuId);
            return Task.FromResult(new NativeStoreOffer(identity.SkuId == "0011" && !Fail, Fail ? "Native failure" : null, Fail));
        }
        public Task<NativeStoreOffer> CheckAsync(PublishedStorePackage package, CancellationToken token) => throw new InvalidOperationException();
        public Task<PreparedNativeStoreUpdate> PrepareAsync(PublishedStorePackage package, CancellationToken token) => throw new InvalidOperationException("A scan must never prepare or apply.");
    }
    private sealed class Metadata : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken t)
        {
            var family = r.RequestUri!.AbsolutePath.EndsWith("/lookup")
                ? Uri.UnescapeDataString(r.RequestUri.Query.Split("value=")[1].Split('&')[0]) : "Fixture.App_publisher";
            object Sku(string id) => new { Sku = new { SkuId = id, Properties = new { Packages = new[] { new {
                PackageFamilyName = family, PackageFullName = family[..family.LastIndexOf('_')] + "_2.0.0.0_x64__publisher", Architectures = new[] { "x64" }
            } } } } };
            var product = new { ProductId = "9FIXTURE", DisplaySkuAvailabilities = new[] { Sku("0010"), Sku("0011") } };
            var content = r.RequestUri.AbsolutePath.EndsWith("/lookup")
                ? JsonSerializer.Serialize(new { Products = new[] { product } }) : JsonSerializer.Serialize(new { Product = product });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });
        }
    }
}
