using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
