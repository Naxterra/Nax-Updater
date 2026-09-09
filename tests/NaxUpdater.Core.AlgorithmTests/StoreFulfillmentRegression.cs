using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;
using System.Net;
using System.Text.Json;
using Windows.ApplicationModel.Store.Preview.InstallControl;

internal static class StoreFulfillmentRegression
{
    private const string Family = "Fixture.Game_publisher";
    private const string Product = "Fixture.GameProduct";
    private const string FullName = "Fixture.Game_2.0.0.0_x64__publisher";

    public static async Task RunAsync(Action<bool, string> assert)
    {
        object Entry(string sku, string[] actions, string platform = "Windows.Desktop", string? start = null, string? end = null) => new
        {
            Sku = new { SkuId = sku, Properties = new { Packages = new[] { new { PackageFamilyName = Family, PackageFullName = FullName, Architectures = new[] { "x64" } } } } },
            Availabilities = new[] { new { Actions = actions, Conditions = new { StartDate = start, EndDate = end,
                ClientConditions = new { AllowedPlatforms = new[] { new { PlatformName = platform } } } } } }
        };
        var entries = new[] {
            Entry("0017", ["Details", "Redeem"]),
            Entry("0099", ["Purchase", "License"]),
            Entry("console", ["Fulfill"], "Windows.Xbox"),
            Entry("future", ["Fulfill"], start: "9998-01-01T00:00:00Z"),
            Entry("expired", ["Fulfill"], end: "2000-01-01T00:00:00Z"),
            Entry("0010", ["Details", "Fulfill", "Purchase"]),
            Entry("0011", ["License", "Fulfill"])
        };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { Product = new { ProductId = Product, DisplaySkuAvailabilities = entries } }));
        var skuEntries = json.RootElement.GetProperty("Product").GetProperty("DisplaySkuAvailabilities");
        var fixedNow = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 5; i++) assert(!MicrosoftStoreProductMetadataClient.SupportsWindowsFulfillment(skuEntries[i], fixedNow),
            "A non-fulfillable, wrong-platform or inactive SKU was accepted.");
        assert(MicrosoftStoreProductMetadataClient.SupportsWindowsFulfillment(skuEntries[5], fixedNow) &&
            MicrosoftStoreProductMetadataClient.SupportsWindowsFulfillment(skuEntries[6], fixedNow),
            "A valid full/trial fulfillment route was excluded.");
        assert(MicrosoftStoreProductMetadataClient.ParseIdentity(json.RootElement, Product, Family, "x64")?.SkuId == "0010",
            "A redeem-only SKU was selected as the primary update route.");
        assert(MicrosoftStoreProductMetadataClient.ParsePublishedPackage(json.RootElement, Product, Family, "x64", "1.0.0.0", "0017") is null,
            "A redeem-only SKU could still produce a prepared installation target.");

        var handler = new Handler(json.RootElement.GetRawText());
        using var http = new HttpClient(handler);
        var nativeClient = new Client();
        var metadata = new MicrosoftStoreProductMetadataClient(http);
        var native = new NativeStoreUpdateService(nativeClient, (p, t) => metadata.GetPublishedPackageAsync(
            p.ProductId, p.PackageFamilyName, p.Architecture, p.Version, t, p.SkuId));
        var app = new InstalledApplication("msix:" + Family, "Fixture game", "Fixture", "1.0.0.0", "1.0.0.0", "fixture", null,
            "fixture", null, null, InstallScope.CurrentUser, ManagementMode.Msix, ConfidenceLevel.High, false, [], null,
            [new(EvidenceKind.MsixPackage, "MSIX package architecture", "x64", true)]);
        var provider = new MsixStoreUpdateProvider(http, new NoStoreUpdate(), native);
        var result = await provider.CheckAsync(app, CancellationToken.None);
        assert(result.Status == UpdateStatus.Current && result.AvailableVersion is null,
            "Valid no-update results were poisoned by a non-fulfillable alternate SKU.");
        assert(nativeClient.Queried.SequenceEqual(["0010", "0011"]), "A non-fulfillable SKU reached the native Store API.");
        assert(handler.Requests == 1, "Inline package-family metadata caused a redundant product request.");

        await provider.CheckAsync(app, CancellationToken.None);
        assert(handler.Requests == 1 && nativeClient.Queried.Count == 4,
            "Warm checks must reuse routing metadata but query native eligibility again.");

        nativeClient.FailEligible = true;
        var failed = await provider.CheckAsync(app, CancellationToken.None);
        assert(failed.Status == UpdateStatus.Error, "An actual failure on a valid fulfillment SKU was suppressed.");
        nativeClient.FailEligible = false;
        nativeClient.Offer = true;
        handler.ProductJson = handler.ProductJson.Replace("2.0.0.0", "3.0.0.0");
        var available = await provider.CheckAsync(app, CancellationToken.None);
        assert(available.IsInstallable && available.ExecutionPlan?.NativeStoreTarget?.SkuId == "0011",
            "Filtering non-fulfillable SKUs hid a valid alternate update offer.");
        assert(available.AvailableVersion == "3.0.0.0" && handler.Requests == 2,
            "A positive native offer must refresh its exact target rather than reuse cached package versions.");
        handler.ProductJson = handler.ProductJson.Replace("3.0.0.0", "1.0.0.0");
        assert((await provider.CheckAsync(app, CancellationToken.None)).Status == UpdateStatus.Error,
            "A native update offer with an equal public-catalog target was falsely labeled Current.");
        try
        {
            await native.PrepareAsync(new(Product, "0017", Family, "2.0.0.0", FullName, "x64"), CancellationToken.None);
            assert(false, "Preparation accepted a SKU that no longer provides fulfillment.");
        }
        catch (InvalidOperationException) { assert(true, "Non-fulfillable prepared target rejected."); }

        var retryHandler = new Handler(json.RootElement.GetRawText()) { FailuresRemaining = 1 };
        using var retryHttp = new HttpClient(retryHandler);
        var retryMetadata = new MicrosoftStoreProductMetadataClient(retryHttp);
        assert(await retryMetadata.ResolvePackageFamilyAsync(Family, "x64", "1.0.0.0", CancellationToken.None) is not null && retryHandler.Requests == 2,
            "A transient Store catalog failure was not retried once.");
        retryHandler.FailuresRemaining = 2;
        using var failureHttp = new HttpClient(retryHandler);
        var failureMetadata = new MicrosoftStoreProductMetadataClient(failureHttp);
        try
        {
            await failureMetadata.ResolvePackageFamilyAsync(Family, "x64", "1.0.0.0", CancellationToken.None);
            assert(false, "Repeated catalog failure was reported as a successful check.");
        }
        catch (HttpRequestException) { assert(true, "Repeated catalog failure remains an error."); }
        assert(await failureMetadata.ResolvePackageFamilyAsync(Family, "x64", "1.0.0.0", CancellationToken.None) is not null && retryHandler.Requests == 5,
            "A failed Store lookup was cached and prevented a subsequent successful check.");

        var requestsBeforeQueue = handler.Requests;
        var queriesBeforeQueue = nativeClient.Queried.Count;
        foreach (var state in new[] { AppInstallState.ReadyToDownload, AppInstallState.Pending,
            AppInstallState.Downloading, AppInstallState.Installing, AppInstallState.Paused })
        {
            nativeClient.Queue = new(Product, Family, state);
            var queued = await provider.CheckAsync(app, CancellationToken.None);
            assert(queued.Status == UpdateStatus.StoreQueued && queued.IsInstallable && queued.AvailableVersion is null &&
                queued.ExecutionPlan?.Kind == UpdateExecutionKind.NativeStoreQueue,
                $"A {state} Store queue entry did not offer an identity-bound existing-queue action.");
            assert(handler.Requests == requestsBeforeQueue && nativeClient.Queried.Count == queriesBeforeQueue,
                "A known queued package still performed redundant catalog/update requests.");
        }
        nativeClient.Queue = new(Product, Family, AppInstallState.Downloading);
        try
        {
            await native.PrepareAsync(new(Product, "0011", Family, "2.0.0.0", FullName, "x64"), CancellationToken.None);
            assert(false, "A downloading Store package could be prepared for duplicate execution.");
        }
        catch (InvalidOperationException) { assert(true, "An active Store deployment blocked duplicate preparation."); }
        nativeClient.Queue = new(Product, Family, AppInstallState.Error, unchecked((int)0x80004005));
        assert((await provider.CheckAsync(app, CancellationToken.None)) is { Status: UpdateStatus.StoreQueued, IsInstallable: true },
            "An errored Store queue entry did not offer an explicitly approved retry.");
        nativeClient.Queue = new(Product, Family, AppInstallState.Error, NativeStoreUpdateService.PackageIdentityConflictCode);
        assert((await provider.CheckAsync(app, CancellationToken.None)) is { Status: UpdateStatus.Error, IsInstallable: false },
            "An unchanged Store package content conflict was offered for retry or labeled Current.");
        nativeClient.Queue = new(Product, "Wrong_family", AppInstallState.Downloading);
        try
        {
            await native.ReadQueueAsync(Family, CancellationToken.None);
            assert(false, "A queue entry for a different family was accepted.");
        }
        catch (InvalidOperationException) { assert(true, "Queue identity mismatch rejected."); }
        foreach (var terminal in new[] { AppInstallState.Completed, AppInstallState.Canceled })
        {
            nativeClient.Queue = new(Product, Family, terminal);
            assert(await native.ReadQueueAsync(Family, CancellationToken.None) is null,
                "A terminal queue record prevented a fresh update check.");
        }
        nativeClient.Queue = null;
        nativeClient.Offer = false;
        nativeClient.QueueAfterQuery = new(Product, Family, AppInstallState.Downloading);
        assert((await native.CheckIdentityAsync(new(Product, "0010", Family), CancellationToken.None)).QueueEntry?.State == AppInstallState.Downloading,
            "A queue entry appearing during a null update query was mislabeled Current.");
    }

    private sealed class Handler(string productJson) : HttpMessageHandler
    {
        public string ProductJson { get; set; } = productJson;
        public int Requests { get; private set; }
        public int FailuresRemaining { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }
            using var document = JsonDocument.Parse(ProductJson);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                request.RequestUri!.AbsolutePath.EndsWith("/lookup") ? JsonSerializer.Serialize(new { Products = new[] { document.RootElement.GetProperty("Product") } }) : ProductJson)
            });
        }
    }
    private sealed class Client : INativeStoreUpdateClient, INativeStoreUpdateItem
    {
        public List<string> Queried { get; } = [];
        public bool FailEligible { get; set; }
        public bool Offer { get; set; }
        public NativeStoreQueueEntry? Queue { get; set; }
        public NativeStoreQueueEntry? QueueAfterQuery { get; set; }
        public Task<NativeStoreQueueEntry?> ReadQueueAsync(string family, CancellationToken token) => Task.FromResult(Queue);
        public string ProductId => Product;
        public string PackageFamilyName => Family;
        public Task<INativeStoreUpdateItem?> FindPausedUpdateAsync(StoreProductIdentity identity, CancellationToken token)
        {
            Queried.Add(identity.SkuId);
            if (QueueAfterQuery is not null) Queue = QueueAfterQuery;
            if (identity.SkuId is not ("0010" or "0011") || FailEligible && identity.SkuId == "0011") throw new ArgumentException("Rejected SKU");
            return Task.FromResult<INativeStoreUpdateItem?>(Offer && identity.SkuId == "0011" ? this : null);
        }
        public Task<INativeStoreUpdateItem?> StartUpdateAsync(PublishedStorePackage package, CancellationToken token) => throw new InvalidOperationException("Tests must not start installations.");
        public NativeStoreItemState Status() => new(AppInstallState.ReadyToDownload);
    }
}
