using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;
using System.Net;
using System.Text.Json;
using Windows.ApplicationModel.Store.Preview.InstallControl;

internal static class CoverageRegression
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        var app = new InstalledApplication("fixture:coverage", "Coverage fixture", "Fixture", "1.0.0", "1.0.0", "fixture",
            @"C:\Fixture\fixture.exe", "fixture", null, null, InstallScope.CurrentUser, ManagementMode.NativeSelfUpdater,
            ConfidenceLevel.High, false, [], null, []);
        var high = new Probe("producer", UpdateProviderAuthority.ProducerRelease, UpdateStatus.Current);
        var low = new Probe("store", UpdateProviderAuthority.PlatformStore, UpdateStatus.Available);
        var combined = (await new UpdateCheckService([low, high]).CheckAsync(new(DateTimeOffset.UtcNow, [app], [], []))).Results.Single();
        assert(high.Calls == 1 && low.Calls == 1, "Native ownership or provider priority skipped a compatible source.");
        assert(combined.IsInstallable && combined.SourceChecks?.Count == 2, "A valid offer was lost or source results were not retained.");
        high.Status = UpdateStatus.Error;
        var guarded = (await new UpdateCheckService([high, low]).CheckAsync(new(DateTimeOffset.UtcNow, [app], [], []))).Results.Single();
        assert(guarded.Status == UpdateStatus.Error && !guarded.IsInstallable, "A lower-priority installer bypassed a higher-priority verification failure.");
        high.Status = UpdateStatus.ManagedExternally;
        var fallback = (await new UpdateCheckService([high, low]).CheckAsync(new(DateTimeOffset.UtcNow, [app], [], []))).Results.Single();
        assert(fallback.IsInstallable, "An unsupported native protocol suppressed a compatible working source.");
        var beforeLow = low.Calls;
        high.Status = UpdateStatus.Current;
        var preferred = app with { Evidence = [new(EvidenceKind.Policy, "Preferred update provider", "producer", true)] };
        await new UpdateCheckService([high, low]).CheckAsync(new(DateTimeOffset.UtcNow, [preferred], [], []));
        assert(low.Calls == beforeLow, "Source aggregation bypassed an explicit exclusive provider policy.");
        high.Status = UpdateStatus.Available;
        var partial = (await new UpdateCheckService([high, new SlowProbe()], sourceCheckTimeout: TimeSpan.FromMilliseconds(80))
            .CheckAsync(new(DateTimeOffset.UtcNow, [app], [], []))).Results.Single();
        assert(partial.IsInstallable && partial.SourceChecks?.Any(s => s.Status == UpdateStatus.Error) == true,
            "A slow secondary source discarded a confirmed primary offer or its failure evidence.");

        const string family = "Fixture.Store_publisher";
        var storeApp = app with
        {
            Identity = "msix:" + family,
            ManagementMode = ManagementMode.Msix,
            InstalledVersion = "1.0.0.0",
            NormalizedVersion = "1.0.0.0",
            Evidence = [new(EvidenceKind.MsixPackage, "MSIX package architecture", "x64", true)]
        };
        using var metadata = new HttpClient(new MetadataHandler(family));
        var nativeClient = new NativeClient("Fixture.Product", family);
        var native = new NativeStoreUpdateService(nativeClient, (p, t) => Task.FromResult<PublishedStorePackage?>(p));
        var storeProvider = new MsixStoreUpdateProvider(metadata, new NoStoreUpdate(), native);
        var offer = await storeProvider.CheckAsync(storeApp, CancellationToken.None);
        assert(offer.IsInstallable && offer.ExecutionPlan?.NativeStoreTarget?.SkuId == "0011",
            "Generic Store checking did not find the applicable second SKU.");
        assert(nativeClient.CheckedSkus.SequenceEqual(["0010", "0011"]), "Store SKU eligibility checks were incomplete.");
        assert(offer.ExecutionPlan?.StorePackageFamilyName == family && offer.AvailableVersion == "2.0.0.0",
            "Generic Store offer lost its exact package/version binding.");
        var sameVersion = await storeProvider.CheckAsync(storeApp with { NormalizedVersion = "2.0.0.0" }, CancellationToken.None);
        assert(sameVersion.Status == UpdateStatus.Current && !sameVersion.IsInstallable,
            "A same-version Store queue item was treated as an upgrade or check failure.");
        nativeClient.Available = false;
        var current = await storeProvider.CheckAsync(storeApp, CancellationToken.None);
        assert(current.Status == UpdateStatus.Current && current.AvailableVersion is null,
            "Generic Store checking promoted catalog-only versions to updates.");
        using var wrongMetadata = new HttpClient(new MetadataHandler("Wrong_family"));
        var identity = await new MicrosoftStoreProductMetadataClient(wrongMetadata).ResolvePackageFamilyAsync(family, "x64", "1.0.0.0", CancellationToken.None);
        assert(identity is null, "Package-family lookup accepted a different family's product metadata.");
        using var opaque = JsonDocument.Parse("""
            {"Product":{"DisplaySkuAvailabilities":[{"Sku":{"Properties":{"Packages":[{
            "PackageFamilyName":"Fixture.Store_publisher","Version":"281474976710656","Architectures":["x64"]}]}}}]}}
            """);
        assert(MicrosoftStoreProductMetadataClient.ParseLatestPackageVersion(opaque.RootElement, family, "x64", "1.0.0.0") == "1.0.0.0",
            "A string-encoded Store version caused a parse failure.");

        var releaseUri = new Uri("https://updates.macrium.com/reflect/v10/v10.0.8843/details10.0.8843.htm");
        using var macriumHttp = new HttpClient(new RedirectHandler(releaseUri));
        var macrium = new MacriumUpdateProvider(macriumHttp);
        var reflect = app with
        {
            DisplayName = "Macrium Reflect Home",
            Publisher = "Paramount Software (UK) Ltd.",
            InstalledVersion = "10, 0, 8843, 0",
            NormalizedVersion = "10, 0, 8843, 0"
        };
        assert(macrium.CanHandle(reflect), "Macrium's installed publisher identity was not recognized.");
        assert((await macrium.CheckAsync(reflect, CancellationToken.None)).Status == UpdateStatus.Current,
            "Macrium's official current patch was not reconciled with its comma-separated installed version.");
        using var redirect = new HttpClient(new LegacyRedirectHandler(releaseUri));
        assert((await new MacriumUpdateProvider(redirect).CheckAsync(reflect, CancellationToken.None)).Status == UpdateStatus.Current,
            "Macrium's legacy HTTP redirect was not followed using HTTPS.");
        assert(MacriumUpdateProvider.ParseReleaseVersion(releaseUri, "8") is null &&
            MacriumUpdateProvider.ParseReleaseVersion(new Uri("https://other.example/reflect/v10/v10.0.8843/details10.0.8843.htm"), "10") is null,
            "Macrium release checks accepted a different major/license line or publisher host.");

        var xaml = await File.ReadAllTextAsync(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../src/NaxUpdater/MainPage.xaml")));
        assert(!xaml.Contains("x:Uid=\"LanguageHeader\"") && !xaml.Contains("{Binding Language}") && !xaml.Contains("UpdateLanguageText"),
            "The language column or detail section is still displayed.");
        var uiCode = await File.ReadAllTextAsync(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../src/NaxUpdater/MainPage.xaml.cs")));
        assert(!uiCode.Contains("row.Language.Contains"), "Language still participates in update filtering.");
    }

    private sealed class Probe(string id, UpdateProviderAuthority authority, UpdateStatus status) : IUpdateProvider
    {
        public string Id => id;
        public UpdateStatus Status { get; set; } = status;
        public int Calls { get; private set; }
        public UpdateProviderDescriptor Descriptor { get; } = new(authority, 100, "fixture");
        public bool CanHandle(InstalledApplication app) => true;
        public Task<UpdateCheckResult> CheckAsync(InstalledApplication app, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new UpdateCheckResult(app.Identity, app.DisplayName, app.NormalizedVersion,
                Status == UpdateStatus.Available ? "2.0.0" : null, Status, Id, Id, "neutral", "fixture", "x64", "stable", null, null,
                Status == UpdateStatus.Available ? new(UpdateExecutionKind.NativeCommand, null, null, null, "Fixture", @"C:\Fixture\updater.exe", [], false, [], []) : null));
        }
    }
    private sealed class MetadataHandler(string family) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            object body = request.RequestUri!.AbsolutePath.EndsWith("/lookup")
                ? new { Products = new[] { new { ProductId = "Fixture.Product" } } }
                : new
                {
                    Product = new
                    {
                        ProductId = "Fixture.Product",
                        DisplaySkuAvailabilities = new[] { "0010", "0011" }.Select(s =>
                    new
                    {
                        Sku = new
                        {
                            SkuId = s,
                            Properties = new
                            {
                                Packages = new[] { new { PackageFamilyName = family,
                        PackageFullName = "Fixture.Store_2.0.0.0_x64__publisher", Architectures = new[] { "x64" }, PackageFormat = "EMsix" } }
                            }
                        }
                    })
                    }
                };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) });
        }
    }
    private sealed class RedirectHandler(Uri final) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = new(HttpMethod.Get, final), Content = new StringContent("Macrium Reflect") });
    }
    private sealed class LegacyRedirectHandler(Uri final) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.Scheme != "https") throw new InvalidOperationException("A release request downgraded to HTTP.");
            var response = new HttpResponseMessage(request.RequestUri.AbsolutePath.EndsWith(".asp") ? HttpStatusCode.Found : HttpStatusCode.OK)
            { RequestMessage = request, Content = new StringContent("Macrium Reflect") };
            if (response.StatusCode == HttpStatusCode.Found) response.Headers.Location = new UriBuilder(final) { Scheme = "http", Port = -1 }.Uri;
            return Task.FromResult(response);
        }
    }
    private sealed class SlowProbe : IUpdateProvider
    {
        public string Id => "slow-secondary";
        public UpdateProviderDescriptor Descriptor { get; } = new(UpdateProviderAuthority.FallbackCatalog, 1, "fixture");
        public bool CanHandle(InstalledApplication app) => true;
        public async Task<UpdateCheckResult> CheckAsync(InstalledApplication app, CancellationToken token)
        { await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException(); }
    }
    private sealed class NativeClient(string product, string family) : INativeStoreUpdateClient, INativeStoreUpdateItem
    {
        public bool Available { get; set; } = true;
        public List<string> CheckedSkus { get; } = [];
        public string ProductId => product;
        public string PackageFamilyName => family;
        public Task<INativeStoreUpdateItem?> FindPausedUpdateAsync(StoreProductIdentity identity, CancellationToken token)
        {
            CheckedSkus.Add(identity.SkuId);
            return Task.FromResult<INativeStoreUpdateItem?>(Available && identity.SkuId == "0011" ? this : null);
        }
        public Task<INativeStoreUpdateItem?> StartUpdateAsync(PublishedStorePackage package, CancellationToken token) => throw new InvalidOperationException("Tests must not install Store packages.");
        public NativeStoreItemState Status() => new(AppInstallState.ReadyToDownload);
    }
}
