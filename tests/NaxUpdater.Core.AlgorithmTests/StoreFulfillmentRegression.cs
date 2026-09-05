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

        using var http = new HttpClient(new Handler(json.RootElement.GetRawText()));
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

        nativeClient.FailEligible = true;
        var failed = await provider.CheckAsync(app, CancellationToken.None);
        assert(failed.Status == UpdateStatus.Error, "An actual failure on a valid fulfillment SKU was suppressed.");
        nativeClient.FailEligible = false;
        nativeClient.Offer = true;
        var available = await provider.CheckAsync(app, CancellationToken.None);
        assert(available.IsInstallable && available.ExecutionPlan?.NativeStoreTarget?.SkuId == "0011",
            "Filtering non-fulfillable SKUs hid a valid alternate update offer.");
        try
        {
            await native.PrepareAsync(new(Product, "0017", Family, "2.0.0.0", FullName, "x64"), CancellationToken.None);
            assert(false, "Preparation accepted a SKU that no longer provides fulfillment.");
        }
        catch (InvalidOperationException) { assert(true, "Non-fulfillable prepared target rejected."); }
    }

    private sealed class Handler(string productJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                request.RequestUri!.AbsolutePath.EndsWith("/lookup") ? JsonSerializer.Serialize(new { Products = new[] { new { ProductId = Product } } }) : productJson)
            });
    }
    private sealed class Client : INativeStoreUpdateClient, INativeStoreUpdateItem
    {
        public List<string> Queried { get; } = [];
        public bool FailEligible { get; set; }
        public bool Offer { get; set; }
        public string ProductId => Product;
        public string PackageFamilyName => Family;
        public Task<INativeStoreUpdateItem?> FindPausedUpdateAsync(StoreProductIdentity identity, CancellationToken token)
        {
            Queried.Add(identity.SkuId);
            if (identity.SkuId is not ("0010" or "0011") || FailEligible && identity.SkuId == "0011") throw new ArgumentException("Rejected SKU");
            return Task.FromResult<INativeStoreUpdateItem?>(Offer && identity.SkuId == "0011" ? this : null);
        }
        public Task<INativeStoreUpdateItem?> StartUpdateAsync(PublishedStorePackage package, CancellationToken token) => throw new InvalidOperationException("Tests must not start installations.");
        public NativeStoreItemState Status() => new(AppInstallState.ReadyToDownload);
    }
}
