using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.ApplicationModel.Store.Preview.InstallControl;

var checks = 0;
void Assert(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
var pairs = new (string Left, string Right, int Order)[]
{
    ("1.0.0","1.0",0), ("1.0.0+build9","1.0.0",0),
    ("155.0b10","155.0b9",1), ("26.09.01.0","260819",1),
    ("1.0.0+20260904200000000000","1.0.0",0), ("9999999999999999999999.0","9999999999999999999998.9",1),
    ("1.0.0-rc.10","1.0.0-rc.9",1), ("1.0.0-preview.7","1.0.0", -1),
    ("1.0.0","1.0.0-rc.9",1), ("26.08.19.0","260819",0),
    ("v2.55.0.windows.5","2.55.0.5",0), ("1.318.0-38354","1.318.0.38354",0),
    ("155.0esr","155.0",0), ("1, 2, 3, 0","1.2.3",0)
};
foreach (var pair in pairs)
{
    Assert(Math.Sign(VersionOrder.Compare(pair.Left, pair.Right)) == pair.Order, $"Wrong version order: {pair.Left} / {pair.Right}");
    Assert(Math.Sign(VersionOrder.Compare(pair.Right, pair.Left)) == -pair.Order, $"Version comparison is not antisymmetric: {pair.Left}");
}
Assert(WingetPackageService.Copy(new IndexedOnlyList()).SequenceEqual(["first", "second"]),
    "WinGet COM collection handling still requires an unsupported iterator.");
var gpuBefore = new InstalledHardwareDriver(
    @"pnp:PCI\VEN_10DE&DEV_2C02\GPU-A", "NVIDIA GeForce RTX 5080", "Display", "NVIDIA", "NVIDIA",
    "32.0.16.1656", null, @"PCI\VEN_10DE&DEV_2C02", "oem82.inf");
var gpuAfter = gpuBefore with { InstalledVersion = "32.0.16.1664", InfName = "oem182.inf" };
var beforeKey = DriverUpdateIdentity.ForGroup("NVIDIA|oem82.inf|32.0.16.1656", [gpuBefore]);
var afterKey = DriverUpdateIdentity.ForGroup("NVIDIA|oem182.inf|32.0.16.1664", [gpuAfter]);
Assert(beforeKey == afterKey, "Updating an INF/version changed the physical GPU identity.");
Assert(DriverUpdateIdentity.ForGroup("NVIDIA|oem182.inf|32.0.16.1664",
    [gpuAfter with { Identity = @"pnp:PCI\VEN_10DE&DEV_2C02\GPU-B" }]) != afterKey,
    "Different GPU instances received the same update identity.");
Assert(DriverUpdateIdentity.InstalledReleaseVersion(gpuAfter) == "616.64", "Recovery compared a Windows driver version with an NVIDIA release number.");
Assert(DriverUpdateIdentity.InstalledReleaseVersion(gpuAfter with { DeviceClass = "MEDIA", InstalledVersion = "1.4.6.3" }) == "1.4.6.3",
    "NVIDIA audio-driver version was incorrectly normalized as a graphics driver.");
var currentGpu = gpuAfter with { Identity = afterKey };
var legacyIdentity = "driver-package:NVIDIA|oem82.inf|32.0.16.1656";
var retainedOld = gpuBefore with { Identity = legacyIdentity, IsPresent = false };
var recovered = DriverUpdateIdentity.Find([retainedOld, currentGpu], legacyIdentity, "driver:" + legacyIdentity,
    gpuBefore.DeviceName, "manufacturer-driver:nvidia");
Assert(recovered == currentGpu && DriverUpdateIdentity.InstalledReleaseVersion(recovered) == "616.64",
    "The existing NVIDIA journal did not migrate to the unique current GPU.");
Assert(DriverUpdateIdentity.Find([currentGpu], beforeKey, "driver:" + beforeKey,
    gpuBefore.DeviceName, "manufacturer-driver:nvidia") == currentGpu, "Post-install verification lost the stable device.");
Assert(DriverUpdateIdentity.Find([currentGpu, currentGpu with { Identity = "another-gpu" }], legacyIdentity,
    "driver:" + legacyIdentity, gpuBefore.DeviceName, "manufacturer-driver:nvidia") is null,
    "An ambiguous legacy GPU record was silently marked successful.");
Assert(DriverUpdateIdentity.Find([retainedOld], legacyIdentity, "driver:" + legacyIdentity,
    gpuBefore.DeviceName, "manufacturer-driver:nvidia") is null, "A retained driver-store registration was treated as a live GPU.");
Assert(DriverUpdateIdentity.Find([currentGpu], legacyIdentity, "driver:" + legacyIdentity,
    "A different GPU", "manufacturer-driver:nvidia") is null, "Legacy recovery accepted a different device model.");
var fixture = Path.Combine(Path.GetTempPath(), "NaxUpdater-algorithms-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
try
{
    string Pe(string file, ushort machine)
    {
        var path = Path.Combine(fixture, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[256];
        BitConverter.GetBytes((ushort)0x5a4d).CopyTo(bytes, 0);
        BitConverter.GetBytes(128).CopyTo(bytes, 0x3c);
        BitConverter.GetBytes(0x4550).CopyTo(bytes, 128);
        BitConverter.GetBytes(machine).CopyTo(bytes, 132);
        File.WriteAllBytes(path, bytes);
        return path;
    }
    var x64 = Pe("x64/app.exe", 0x8664);
    var x86 = Pe("x86/app.exe", 0x14c);
    var arm = Pe("arm64/app.exe", 0xaa64);
    var catalog = await UpdateProviderCatalogLoader.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Configuration/update-providers.json"));
    var recipe = catalog.GitHub.Single(r => r.Id == "Windscribe.Windscribe");
    object Release(string version = "2.24.12", string assetVersion = "2.24.12", string arch = "amd64", bool prerelease = false) => new
    {
        tag_name = "v" + version,
        prerelease,
        draft = false,
        html_url = "https://github.com/Windscribe/Desktop-App/releases/tag/v" + version,
        assets = new[]{new { name=$"Windscribe_{assetVersion}_{arch}.exe",
            browser_download_url=$"https://github.com/Windscribe/Desktop-App/releases/download/v{version}/Windscribe_{assetVersion}_{arch}.exe",
            digest="sha256:"+new string('a',64) }}
    };
    async Task<UpdateCheckResult> CheckGithub(string path, InstallScope scope, object release)
    {
        using var client = new HttpClient(new Handler(_ => Json(release)));
        var app = App("Windscribe", "Windscribe Limited", "2.24.10", path, scope);
        return (await new UpdateCheckService([new GitHubReleaseUpdateProvider(client, recipe)]).CheckAsync(Snapshot(app))).Results.Single();
    }
    Assert((await CheckGithub(x64, InstallScope.Machine, Release())).IsInstallable, "A valid x64 Windscribe update was lost.");
    Assert(!(await CheckGithub(x86, InstallScope.Machine, Release())).IsInstallable, "x86 install received an x64 update.");
    Assert(!(await CheckGithub(x64, InstallScope.CurrentUser, Release())).IsInstallable, "User install received a machine update.");
    var armUpdate = await CheckGithub(arm, InstallScope.Machine, Release(arch: "arm64"));
    Assert(armUpdate.IsInstallable && armUpdate.Architecture == "arm64" &&
        armUpdate.ExecutionPlan!.DownloadUri!.LocalPath.EndsWith("_arm64.exe"), "ARM64 update did not select the ARM64 asset.");
    Assert((await CheckGithub(x64, InstallScope.Machine, Release(assetVersion: "99.0.0"))).Status == UpdateStatus.Error, "Tag/asset mismatch was accepted.");
    Assert(!(await CheckGithub(x64, InstallScope.Machine, Release(prerelease: true))).IsInstallable, "Prerelease was offered on stable channel.");

    using var key = RSA.Create(2048);
    var ui = Pe("ivpn/ui/IVPN Client.exe", 0x8664);
    var icon = Path.Combine(fixture, "ivpn/icon.ico");
    File.WriteAllBytes(icon, [0]);
    var url = "https://repo.ivpn.net/windows/bin/IVPN-Client-v3.15.15.exe";
    var feed = JsonSerializer.SerializeToUtf8Bytes(new { generic = new { version = "3.15.15", downloadLink = url, signature = url + ".sign.sha256.base64" } });
    var signature = Convert.ToBase64String(key.SignData(feed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    var fallbackCalls = 0;
    using var ivpnClient = new HttpClient(new Handler(request =>
        request.RequestUri!.Host == "api.github.com" ? new HttpResponseMessage(HttpStatusCode.Forbidden) :
        request.RequestUri.AbsolutePath.EndsWith(".base64") ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(signature) } :
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(feed) }));
    var ivpn = new IvpnUpdateProvider(ivpnClient, key.ExportSubjectPublicKeyInfoPem(), (endpoint, token) =>
    {
        fallbackCalls++;
        Assert(endpoint == "repos/ivpn/desktop-app/releases/tags/v3.15.15", "IVPN queried an unrelated API path.");
        return Task.FromResult<string?>(JsonSerializer.Serialize(new
        {
            tag_name = "v3.15.15",
            draft = false,
            prerelease = false,
            body = $"[Download]({url})\nSHA256: {new string('b', 64)}"
        }));
    });
    var ivpnApp = App("IVPN Client", "IVPN Limited", "3.15.13", icon, InstallScope.Machine);
    var ivpnResult = (await new UpdateCheckService([ivpn]).CheckAsync(Snapshot(ivpnApp))).Results.Single();
    Assert(ivpnResult.IsInstallable && fallbackCalls == 1, "GitHub rate limit still blocks a recoverable IVPN offer.");
    Assert(ivpnResult.ExecutionPlan!.RunningExecutablePaths!.SequenceEqual([Path.GetFullPath(ui)]), "IVPN process binding did not use its actual UI path.");
    feed[0] ^= 1;
    Assert((await ivpn.CheckAsync(ivpnApp, CancellationToken.None)).Status == UpdateStatus.Error, "Tampered IVPN feed was accepted.");
    feed[0] ^= 1;

    var index = Path.Combine(fixture, "index.db");
    using (var db = new SqliteConnection($"Data Source={index};Pooling=False"))
    {
        db.Open();
        using var sql = db.CreateCommand();
        sql.CommandText = """
            CREATE TABLE packages(rowid INTEGER PRIMARY KEY,id TEXT,name TEXT,moniker TEXT,latest_version TEXT);
            CREATE TABLE productcodes2(productcode TEXT,package INTEGER);
            INSERT INTO packages VALUES(1,'Fixture.App','Fixture App','','2.0.0');
            INSERT INTO productcodes2 VALUES('{11111111-2222-3333-4444-555555555555}',1);
            """;
        sql.ExecuteNonQuery();
    }
    var target = new WingetUpdateTarget("Fixture.App", WingetPackageService.OfficialSourceId, "2.0.0", "1.0.0",
        ["{11111111-2222-3333-4444-555555555555}"], "X64", "Msi", "", InstallScope.Machine, fixture);
    var packages = new Packages(target);
    var fixtureApp = App("Fixture App", "Fixture", "1.0.0", x64, InstallScope.Machine) with
    {
        Evidence = [new(EvidenceKind.Registry, "Uninstall registry", "LocalMachine Registry64 · {11111111-2222-3333-4444-555555555555}", true)]
    };
    var fallbackProvider = new WingetFallbackUpdateProvider(index, packages);
    var offer = (await new UpdateCheckService([fallbackProvider]).CheckAsync(Snapshot(fixtureApp))).Results.Single();
    Assert(offer.IsInstallable && offer.ExecutionPlan!.Kind == UpdateExecutionKind.WingetPackage, "WinGet fallback still disables automatic updates.");
    Assert(UpdatePlanValidator.Validate(offer, DateTimeOffset.UtcNow) is null, "WinGet plan failed validation.");
    var execution = new UpdateExecutionService(wingetPackageService: packages);
    using var downloadClient = new HttpClient(new Handler(_ => throw new Exception("Catalog updates must use the package manager.")));
    var backend = new DefaultUpdateTransactionBackend(execution, new UpdatePackageDownloader(downloadClient, new NativeAuthenticodeVerifier()), (previous, token) =>
        Task.FromResult<UpdateCheckResult?>(packages.Applied ? previous with { InstalledVersion = "2.0.0", Status = UpdateStatus.Current, ExecutionPlan = null } : offer));
    var transaction = await new UpdateTransactionCoordinator(backend).ApplyAsync(offer, fixture);
    Assert(transaction.IsSuccess && packages.ApplyCount == 1, "Native catalog transaction did not apply and verify through the normal coordinator.");
    packages.Applied = false;
    var prepared = await execution.PrepareAsync(offer, null);
    var altered = offer with { ExecutionPlan = offer.ExecutionPlan! with { WingetTarget = target with { Version = "3.0.0" } } };
    try { await execution.ExecutePreparedAsync(altered, prepared); Assert(false, "Changed target executed."); }
    catch (InvalidOperationException) { Assert(packages.ApplyCount == 1, "Changed target caused an application call."); }

    var options = WingetPackageService.CreateOptions(null, InstallScope.CurrentUser, fixture);
    Assert(!options.AllowHashMismatch && !options.Force && !options.AllowUpgradeToUnknownVersion &&
        options.PreferredInstallLocation == fixture, "WinGet options weaken integrity or lose the existing location.");
    var mixed = new UpdateCheckSnapshot(DateTimeOffset.UtcNow,
        [offer,offer with{Status=UpdateStatus.Current,ExecutionPlan=null},
         offer with{Status=UpdateStatus.ManagedExternally,ExecutionPlan=null},
         offer with{Status=UpdateStatus.Unsupported,ExecutionPlan=null},
         offer with{Status=UpdateStatus.Error,ExecutionPlan=null}], 1);
    Assert(mixed.CheckedVersionCount == 2 && mixed.ManagedExternallyCount == 1 && mixed.FailedCheckCount == 1 &&
        mixed.InstallableUpdateCount == 1 && !mixed.AllCurrent, "Coverage counts include unchecked or failed rows.");
    Assert(new UpdateCheckSnapshot(DateTimeOffset.UtcNow, [], 0).AllCurrent == false, "Empty scan claims all apps are current.");
    var newerInstall = App("Windscribe", "Windscribe Limited", "3.0.0", x64, InstallScope.Machine);
    using (var client = new HttpClient(new Handler(_ => Json(Release()))))
    {
        var result = (await new UpdateCheckService([new GitHubReleaseUpdateProvider(client, recipe)]).CheckAsync(Snapshot(newerInstall))).Results.Single();
        Assert(result.Status == UpdateStatus.Current && result.AvailableVersion is null, "Older release still appears as available.");
    }
    using (var blockedProvider = new BlockingDiscoveryProvider())
    {
        var responsiveService = new UpdateCheckService([blockedProvider], providerTimeout: TimeSpan.FromMilliseconds(120));
        var returned = new TaskCompletionSource<Task<UpdateCheckSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressStates = new System.Collections.Concurrent.ConcurrentQueue<UpdateCheckProgress>();
        var hung = fixtureApp with { Identity = "hung-fixture", DisplayName = "Hung fixture" };
        var healthy = fixtureApp with { Identity = "healthy-fixture", DisplayName = "Healthy fixture" };
        var uiThread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingContext());
            try
            {
                returned.SetResult(responsiveService.CheckAsync(new(DateTimeOffset.UtcNow, [hung, healthy], [], []),
                progress: new ImmediateProgress(progressStates.Enqueue)));
            }
            catch (Exception error) { returned.SetException(error); }
        })
        { IsBackground = true };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        try
        {
            var backgroundScan = await returned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var checkedRows = await backgroundScan.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(blockedProvider.DiscoveryThreadId != uiThread.ManagedThreadId, "Provider discovery ran on the UI thread.");
            Assert(checkedRows.Results.Single(row => row.ApplicationIdentity == hung.Identity).Status == UpdateStatus.Error,
                "A blocking native discovery call did not time out.");
            Assert(checkedRows.Results.Single(row => row.ApplicationIdentity == healthy.Identity).Status == UpdateStatus.Current,
                "A blocked provider prevented another application from completing.");
            Assert(progressStates.Any(state => state.Completed == 2 && state.Total == 2), "Scan completion progress was not reported.");
        }
        finally { blockedProvider.Release(); uiThread.Join(2000); }
    }
    using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
    {
        var service = new UpdateCheckService([new AsyncHangProvider()]);
        var canceled = false;
        try { await service.CheckAsync(Snapshot(fixtureApp), cancellation.Token).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (OperationCanceledException) { canceled = true; }
        Assert(canceled, "Cancel did not promptly stop waiting for a provider.");
    }
    var catalogTimeout = await new UpdateCheckService([new HungCatalogProvider()], sourceTimeout: TimeSpan.FromMilliseconds(60))
        .CheckAsync(Snapshot(fixtureApp)).WaitAsync(TimeSpan.FromSeconds(2));
    Assert(catalogTimeout.Results.Single().Status == UpdateStatus.Error &&
        catalogTimeout.Results.Single().Message!.Contains("timed out"), "Source refresh has no effective timeout.");

    using (var rolloutClient = new HttpClient(new Handler(request =>
        request.RequestUri!.Host == "persistent.oaistatic.com"
        ? Json(new { schemaVersion = 1, buildVersion = "26.901.5003.0", storeProductId = "9PLM9XGG6VKS", packageIdentity = "OpenAI.Codex" })
        : Json(new
        {
            Product = new
            {
                DisplaySkuAvailabilities = new[] { new { Sku = new { Properties = new { Packages = new[] {
            new { PackageFamilyName = "OpenAI.Codex_2p2nqsd0c76g0", PackageFullName = "OpenAI.Codex_26.901.4073.0_x64__2p2nqsd0c76g0",
                Architectures = new[]{"x64"} } } } } } }
            }
        }))))
    {
        var chatGpt = fixtureApp with
        {
            Identity = "msix:OpenAI.Codex_2p2nqsd0c76g0",
            DisplayName = "ChatGPT",
            ManagementMode = ManagementMode.Msix,
            InstalledVersion = "26.901.4073.0",
            NormalizedVersion = "26.901.4073.0"
        };
        var rollout = await new MsixStoreUpdateProvider(rolloutClient, new NoStoreUpdate())
            .CheckAsync(chatGpt, CancellationToken.None);
        Assert(rollout.AvailabilityReason == UpdateAvailabilityReason.AwaitingStorePublication &&
            rollout.PublishedPackageVersion == "26.901.4073.0" && rollout.AvailableVersion == "26.901.5003.0",
            "OpenAI's announced build was not distinguished from the actual published Store package.");
        Assert(!rollout.IsInstallable, "An unpublished Store package was presented as installable.");
    }
    var nativeTarget = new PublishedStorePackage("9PLM9XGG6VKS", "0010", "OpenAI.Codex_2p2nqsd0c76g0",
        "26.901.5003.0", "OpenAI.Codex_26.901.5003.0_x64__2p2nqsd0c76g0", "x64");
    var nativeClient = new FakeNativeStore(nativeTarget);
    foreach (var inconsistentName in new[] {
        "OpenAI.Codex_26.901.5003.0_arm64__2p2nqsd0c76g0",
        "OpenAI.Codex_26.901.5003.0_x64__differentpublisher" })
    {
        using var invalidPackage = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            Product = new
            {
                ProductId = nativeTarget.ProductId,
                DisplaySkuAvailabilities = new[] { new { Sku = new {
                SkuId = "0010", Properties = new { Packages = new[] { new {
                    PackageFamilyName = nativeTarget.PackageFamilyName, PackageFullName = inconsistentName, Architectures = new[]{"x64"}
                } } } } } }
            }
        }));
        Assert(MicrosoftStoreProductMetadataClient.ParsePublishedPackage(invalidPackage.RootElement,
            nativeTarget.ProductId, nativeTarget.PackageFamilyName, "x64", "26.901.4073.0") is null,
            "Conflicting package-full-name architecture or publisher was accepted.");
    }
    var nativeService = new NativeStoreUpdateService(nativeClient,
        (p, t) => Task.FromResult<PublishedStorePackage?>(p), (_, _) => Task.CompletedTask);
    using (var nativeMetadata = new HttpClient(new Handler(request =>
        request.RequestUri!.Host == "persistent.oaistatic.com"
        ? Json(new { schemaVersion = 1, buildVersion = "26.901.5280.0", storeProductId = "9PLM9XGG6VKS", packageIdentity = "OpenAI.Codex" })
        : Json(new
        {
            Product = new
            {
                ProductId = "9PLM9XGG6VKS",
                DisplaySkuAvailabilities = new[] {
            new { Sku = new { SkuId = "0010", Properties = new { Packages = new[] {
                new { PackageFamilyName = nativeTarget.PackageFamilyName, PackageFullName = nativeTarget.PackageFullName, Architectures = new[]{"x64"} },
                new { PackageFamilyName = nativeTarget.PackageFamilyName, PackageFullName = "OpenAI.Codex_26.901.5280.0_arm64__2p2nqsd0c76g0", Architectures = new[]{"arm64"} }
            } } } } }
            }
        }))))
    {
        var app = fixtureApp with
        {
            Identity = "msix:" + nativeTarget.PackageFamilyName,
            DisplayName = "ChatGPT",
            ManagementMode = ManagementMode.Msix,
            InstalledVersion = "26.901.4073.0",
            NormalizedVersion = "26.901.4073.0",
            Evidence = [new(EvidenceKind.MsixPackage, "MSIX package architecture", "x64", true)]
        };
        var selected = (await new UpdateCheckService([new MsixStoreUpdateProvider(nativeMetadata, new NoStoreUpdate(), nativeService)])
            .CheckAsync(Snapshot(app))).Results.Single();
        Assert(selected.IsInstallable && selected.AvailableVersion == "26.901.5003.0" &&
            selected.AnnouncedVersion == "26.901.5280.0", "A later announcement still hides an already published Store update.");
        Assert(selected.AvailabilityReason == UpdateAvailabilityReason.None &&
            selected.ExecutionPlan?.NativeStoreTarget == nativeTarget, "Published update selected the wrong architecture, SKU or availability reason.");
        Assert(nativeClient.StartCount == 0 && !NativeStoreUpdateService.QueryOptions().AutomaticallyDownloadAndInstallUpdateIfFound,
            "Checking for updates started a Store installation.");
        Assert(NativeStoreUpdateService.QueryOptions(true).AutomaticallyDownloadAndInstallUpdateIfFound,
            "Explicit apply cannot start the approved Store update.");
        // Do not close any real package process while exercising the simulated apply boundary.
        var approved = selected with { ExecutionPlan = selected.ExecutionPlan! with { RunningProcessNames = [], RunningExecutablePaths = [] } };
        var nativeExecution = new UpdateExecutionService(nativeStoreService: nativeService);
        var nativePrepared = await nativeExecution.PrepareAsync(approved, null);
        Assert(nativeClient.StartCount == 0, "Preparing the native Store transaction started installation.");
        var changed = approved with
        {
            ExecutionPlan = approved.ExecutionPlan! with
            {
                NativeStoreTarget = nativeTarget with { Version = "26.901.5280.0" }
            }
        };
        try { await nativeExecution.ExecutePreparedAsync(changed, nativePrepared); Assert(false, "Changed Store target executed."); }
        catch (InvalidOperationException) { Assert(nativeClient.StartCount == 0, "A changed target started installation."); }
        var nativeBackend = new DefaultUpdateTransactionBackend(nativeExecution,
            new UpdatePackageDownloader(downloadClient, new NativeAuthenticodeVerifier()),
            (previous, t) => Task.FromResult<UpdateCheckResult?>(nativeClient.Completed
                ? approved with { InstalledVersion = nativeTarget.Version, Status = UpdateStatus.Current, ExecutionPlan = null }
                : approved));
        var applied = await new UpdateTransactionCoordinator(nativeBackend).ApplyAsync(approved, fixture);
        Assert(applied.IsSuccess && nativeClient.StartCount == 1 && nativeClient.PolledStates >= 3,
            "Store apply did not wait for completion and verify the installed target.");
        var drift = new NativeStoreUpdateService(new FakeNativeStore(nativeTarget),
            (p, t) => Task.FromResult<PublishedStorePackage?>(p with { Version = "26.901.5280.0" }));
        try { await drift.PrepareAsync(nativeTarget, CancellationToken.None); Assert(false, "Changed published package was accepted."); }
        catch (InvalidOperationException) { Assert(true, "Published package drift blocked."); }
        var wrongItem = new FakeNativeStore(nativeTarget with { PackageFamilyName = "Wrong.Package_family" });
        var wrongService = new NativeStoreUpdateService(wrongItem, (p, t) => Task.FromResult<PublishedStorePackage?>(p));
        Assert(!(await wrongService.CheckAsync(nativeTarget, CancellationToken.None)).IsAvailable && wrongItem.StartCount == 0,
            "A different Store package family was accepted.");
        foreach (var terminalState in new[] { AppInstallState.Error, AppInstallState.Canceled })
        {
            var failedClient = new FakeNativeStore(nativeTarget) { FinalState = terminalState };
            var failedService = new NativeStoreUpdateService(failedClient,
                (p, t) => Task.FromResult<PublishedStorePackage?>(p), (_, _) => Task.CompletedTask);
            var failed = await (await failedService.PrepareAsync(nativeTarget, CancellationToken.None)).ApplyAsync(CancellationToken.None);
            Assert(!failed.IsSuccess && failedClient.PolledStates >= 3,
                $"Store {terminalState} was reported as a successful update.");
        }
        var unchangedClient = new FakeNativeStore(nativeTarget);
        var unchangedService = new NativeStoreUpdateService(unchangedClient,
            (p, t) => Task.FromResult<PublishedStorePackage?>(p), (_, _) => Task.CompletedTask);
        var unchangedBackend = new DefaultUpdateTransactionBackend(new UpdateExecutionService(nativeStoreService: unchangedService),
            new UpdatePackageDownloader(downloadClient, new NativeAuthenticodeVerifier()),
            (previous, t) => Task.FromResult<UpdateCheckResult?>(approved));
        var unverified = await new UpdateTransactionCoordinator(unchangedBackend, verificationAttempts: 1)
            .ApplyAsync(approved, fixture);
        Assert(!unverified.IsSuccess && unchangedClient.Completed,
            "Store completion alone was accepted without observing the installed target version.");
    }
    Console.WriteLine($"Algorithm regression tests passed: {checks} assertions. No real installers executed.");
}
finally { Directory.Delete(fixture, true); }

static InstalledApplication App(string name, string publisher, string version, string path, InstallScope scope) => new(
    "fixture:" + name, name, publisher, version, version, "fixture", path, "fixture", null, null, scope,
    ManagementMode.Registry, ConfidenceLevel.High, false, [], null, []);
static InventorySnapshot Snapshot(InstalledApplication app) => new(DateTimeOffset.UtcNow, [app], [], []);
static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response(request));
}
sealed class Packages(WingetUpdateTarget target) : IWingetPackageService
{
    public bool Applied { get; set; }
    public int ApplyCount { get; private set; }
    public Task<WingetPackageOffer> AssessAsync(InstalledApplication app, string id, string version, CancellationToken token) => Task.FromResult(new WingetPackageOffer(target, null));
    public Task<PreparedCatalogUpdate> PrepareAsync(UpdateCheckResult update, CancellationToken token) => Task.FromResult(
        new PreparedCatalogUpdate(target, _ => { Applied = true; ApplyCount++; return Task.FromResult(new UpdateExecutionResult(0, true, null)); }));
}
sealed class IndexedOnlyList : IReadOnlyList<string>
{
    public int Count => 2;
    public string this[int index] => index == 0 ? "first" : "second";
    public IEnumerator<string> GetEnumerator() => throw new NotSupportedException("IIterable is unavailable.");
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
sealed class ImmediateProgress(Action<UpdateCheckProgress> report) : IProgress<UpdateCheckProgress>
{
    public void Report(UpdateCheckProgress progress) => report(progress);
}
sealed class NonPumpingContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback callback, object? state) =>
        throw new InvalidOperationException("The provider captured the UI synchronization context.");
}
sealed class BlockingDiscoveryProvider : IUpdateProvider, IDisposable
{
    private readonly ManualResetEventSlim _release = new(false);
    public int DiscoveryThreadId { get; private set; }
    public string Id => "blocking-discovery";
    public UpdateProviderDescriptor Descriptor { get; } = new(UpdateProviderAuthority.ProducerRelease, 100, "fixture");
    public bool CanHandle(InstalledApplication app)
    {
        if (app.DisplayName == "Hung fixture") { DiscoveryThreadId = Environment.CurrentManagedThreadId; _release.Wait(5000); }
        return true;
    }
    public Task<UpdateCheckResult> CheckAsync(InstalledApplication app, CancellationToken token) =>
        Task.FromResult(new UpdateCheckResult(app.Identity, app.DisplayName, app.NormalizedVersion, app.NormalizedVersion,
            UpdateStatus.Current, Id, Id, "neutral", "fixture", "x64", "stable", null, null, null));
    public void Release() => _release.Set();
    public void Dispose() => _release.Set();
}
sealed class AsyncHangProvider : IUpdateProvider
{
    public string Id => "async-hang";
    public UpdateProviderDescriptor Descriptor { get; } = new(UpdateProviderAuthority.ProducerRelease, 100, "fixture");
    public bool CanHandle(InstalledApplication app) => true;
    public async Task<UpdateCheckResult> CheckAsync(InstalledApplication app, CancellationToken token)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new InvalidOperationException();
    }
}
sealed class HungCatalogProvider : IUpdateProvider, IUpdateProviderSourceRefresher
{
    public string Id => "hung-source";
    public UpdateProviderDescriptor Descriptor { get; } = new(UpdateProviderAuthority.FallbackCatalog, 100, "fixture");
    public bool CanHandle(InstalledApplication app) => true;
    public Task RefreshSourceAsync(CancellationToken token) => new TaskCompletionSource().Task;
    public Task<UpdateCheckResult> CheckAsync(InstalledApplication app, CancellationToken token) => throw new InvalidOperationException("Stale source was used.");
}
sealed class NoStoreUpdate : IStorePackageDeploymentService
{
    public string? LastError => null;
    public Task<StoreUpdateAvailability> CheckForUpdateAsync(string family, string? name, string? publisher, string? version,
        string? architecture, CancellationToken token) => Task.FromResult(new StoreUpdateAvailability(true, false, "9PLM9XGG6VKS", null, null));
    public Task<StoreCatalogIdentity?> ResolveAsync(string family, string? name, string? publisher, CancellationToken token) =>
        Task.FromResult<StoreCatalogIdentity?>(new("9PLM9XGG6VKS", name!, family, true));
    public Task<UpdateExecutionResult> InstallOrUpdateAsync(string id, string family, string? name, string? publisher,
        string version, CancellationToken token) => throw new InvalidOperationException("Read-only rollout check tried to install.");
}
sealed class FakeNativeStore(PublishedStorePackage package) : INativeStoreUpdateClient, INativeStoreUpdateItem
{
    public AppInstallState FinalState { get; init; } = AppInstallState.Completed;
    public int StartCount { get; private set; }
    public int PolledStates { get; private set; }
    public bool Completed { get; private set; }
    public string ProductId => package.ProductId;
    public string PackageFamilyName => package.PackageFamilyName;
    public Task<INativeStoreUpdateItem?> FindPausedUpdateAsync(PublishedStorePackage p, CancellationToken t) =>
        Task.FromResult<INativeStoreUpdateItem?>(this);
    public Task<INativeStoreUpdateItem?> StartUpdateAsync(PublishedStorePackage p, CancellationToken t)
    {
        StartCount++;
        return Task.FromResult<INativeStoreUpdateItem?>(this);
    }
    public NativeStoreItemState Status()
    {
        if (StartCount == 0) return new(AppInstallState.ReadyToDownload);
        PolledStates++;
        Completed = PolledStates >= 3 && FinalState == AppInstallState.Completed;
        return new(PolledStates == 1 ? AppInstallState.Downloading : PolledStates == 2 ? AppInstallState.Installing : FinalState);
    }
}
