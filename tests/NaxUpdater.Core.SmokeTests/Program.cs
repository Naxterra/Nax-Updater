using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

Assert(VersionNormalizer.Normalize("34.0.3.20260826", "FirstThreeNumericComponents") == "34.0.3", "Nextcloud version normalization failed.");
Assert(VersionNormalizer.Normalize("26.8.2.20990", null) == "26.8.2.20990", "Unconfigured versions must remain unchanged.");
Assert(VersionOrder.Compare("154.0.1", "154.0") > 0, "Numeric update ordering failed.");
Assert(VersionOrder.Compare("155.0b5", "155.0") < 0, "Prerelease ordering failed.");

var englishResources = LoadResources(Path.Combine(AppContext.BaseDirectory, "Localization", "en-US.resw"));
var germanResources = LoadResources(Path.Combine(AppContext.BaseDirectory, "Localization", "de-DE.resw"));
Assert(englishResources.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(germanResources.Keys),
    "English and German resource keys differ.");
Assert(germanResources["CheckUpdatesButtonText.Text"] == "Nach Updates suchen", "German update-button translation is missing.");
Assert(germanResources["ProviderFirefoxLanguageOverride"].Contains("Firefox fordert jedoch aktiv", StringComparison.Ordinal),
    "German Firefox language-preservation explanation is missing.");

var manifestFixture = Path.Combine(Path.GetTempPath(), "NaxUpdater.ManifestTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(manifestFixture);
try
{
    var manifestPath = Path.Combine(manifestFixture, "AppxManifest.xml");
    await File.WriteAllTextAsync(manifestPath, """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
                 xmlns:desktop4="http://schemas.microsoft.com/appx/manifest/desktop/windows10/4">
          <Applications>
            <Application Id="Fixture" Executable="fixture.exe" uap10:RuntimeBehavior="win32App">
              <Extensions>
                <desktop4:Extension Category="windows.fileExplorerContextMenus" />
              </Extensions>
            </Application>
          </Applications>
        </Package>
        """);
    var sparseInspection = MsixManifestInspector.Inspect(manifestPath, manifestFixture);
    Assert(sparseInspection.IsExternalIntegrationPackage, "A manifest-only external executable registration should be classified as an integration package.");
    Assert(sparseInspection.DeclaredExecutables.SequenceEqual(["fixture.exe"]), "The declared external executable was not retained.");
    Assert(sparseInspection.ExtensionCategories.Contains("windows.fileExplorerContextMenus"), "The registered extension category was not retained.");

    await File.WriteAllBytesAsync(Path.Combine(manifestFixture, "fixture.exe"), []);
    var payloadInspection = MsixManifestInspector.Inspect(manifestPath, manifestFixture);
    Assert(!payloadInspection.IsExternalIntegrationPackage, "A package containing its declared executable must remain a standalone application.");
}
finally
{
    Directory.Delete(manifestFixture, recursive: true);
}

var firefoxFixture = Path.Combine(Path.GetTempPath(), "NaxUpdater.FirefoxTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(firefoxFixture);
try
{
    var installDirectory = Directory.CreateDirectory(Path.Combine(firefoxFixture, "Mozilla Firefox")).FullName;
    var firefoxExecutable = Path.Combine(installDirectory, "firefox.exe");
    await File.WriteAllBytesAsync(firefoxExecutable, []);
    var preferenceDirectory = Directory.CreateDirectory(Path.Combine(installDirectory, "defaults", "pref")).FullName;
    await File.WriteAllTextAsync(Path.Combine(preferenceDirectory, "channel-prefs.js"), "pref(\"app.update.channel\", \"release\");");

    var firefoxData = Directory.CreateDirectory(Path.Combine(firefoxFixture, "FirefoxData")).FullName;
    var profile = Directory.CreateDirectory(Path.Combine(firefoxData, "Profiles", "default-release")).FullName;
    await File.WriteAllTextAsync(Path.Combine(firefoxData, "installs.ini"), "[fixture]\nDefault=Profiles/default-release\nLocked=1\n");
    await File.WriteAllTextAsync(Path.Combine(profile, "compatibility.ini"), $"[Compatibility]\nLastPlatformDir={installDirectory}\n");
    await File.WriteAllTextAsync(Path.Combine(profile, "prefs.js"), "user_pref(\"intl.locale.requested\", \"de,en-US\");\n");
    await File.WriteAllTextAsync(Path.Combine(profile, "extensions.json"), """
        {"addons":[{"id":"langpack-de@firefox.mozilla.org","type":"locale","active":true}]}
        """);

    var firefoxApplication = CreateApplication(
        "firefox-test",
        "Mozilla Firefox (x64 en-US)",
        "Mozilla",
        "154.0.1",
        firefoxExecutable,
        InstallScope.Machine,
        ManagementMode.Registry);
    var detector = new FirefoxMetadataDetector(firefoxData);
    var detectedFirefox = detector.Detect(firefoxApplication);
    Assert(detectedFirefox.PackagedLanguage == "en-US", "Firefox packaged language detection failed.");
    Assert(detectedFirefox.EffectiveLanguage == "de", "Firefox active German language pack was not selected.");
    Assert(detectedFirefox.Architecture == "x64" && detectedFirefox.Channel == "release", "Firefox architecture or channel detection failed.");

    var firefoxHash = new string('a', 64);
    using var firefoxClient = new HttpClient(new StubHttpMessageHandler(request =>
    {
        if (request.RequestUri?.AbsoluteUri.EndsWith("firefox_versions.json", StringComparison.Ordinal) == true)
        {
            return JsonResponse("{\"LATEST_FIREFOX_VERSION\":\"155.0.1\"}");
        }
        if (request.RequestUri?.AbsoluteUri.EndsWith("/155.0.1/SHA256SUMS", StringComparison.Ordinal) == true)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{firefoxHash}  win64/de/Firefox Setup 155.0.1.exe\n", Encoding.UTF8, "text/plain")
            };
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }));
    var firefoxUpdate = await new FirefoxUpdateProvider(firefoxClient, detector).CheckAsync(firefoxApplication, CancellationToken.None);
    Assert(firefoxUpdate.Status == UpdateStatus.Available && firefoxUpdate.AvailableVersion == "155.0.1", "Firefox update availability failed.");
    Assert(firefoxUpdate.Language == "de" && firefoxUpdate.ExecutionPlan?.DownloadUri?.AbsoluteUri.Contains("/de/", StringComparison.Ordinal) == true,
        "Firefox update did not preserve the German UI language.");
    Assert(firefoxUpdate.ExecutionPlan?.Sha256 == firefoxHash, "Firefox release checksum was not retained.");
    Assert(firefoxUpdate.ExecutionPlan?.Arguments.Contains($"/InstallDirectoryPath={installDirectory}") == true,
        "Firefox install directory was not preserved.");

    var updateCatalogPath = Path.Combine(AppContext.BaseDirectory, "Configuration", "update-providers.json");
    var updateCatalog = await UpdateProviderCatalogLoader.LoadAsync(updateCatalogPath);
    var nextcloudRecipe = updateCatalog.GitHub.Single(recipe => recipe.Id == "Nextcloud.NextcloudDesktop");
    var nextcloudHash = new string('b', 64);
    using var githubClient = new HttpClient(new StubHttpMessageHandler(_ => JsonResponse($$"""
        {
          "tag_name":"v34.0.4",
          "html_url":"https://github.com/nextcloud-releases/desktop/releases/tag/v34.0.4",
          "assets":[{
            "name":"Nextcloud-34.0.4-x64.msi",
            "browser_download_url":"https://github.com/nextcloud-releases/desktop/releases/download/v34.0.4/Nextcloud-34.0.4-x64.msi",
            "digest":"sha256:{{nextcloudHash}}"
          }]
        }
        """)));
    var nextcloudApplication = CreateApplication(
        "nextcloud-test",
        "Nextcloud",
        "Nextcloud GmbH",
        "34.0.3",
        Path.Combine(firefoxFixture, "nextcloud.exe"),
        InstallScope.Machine,
        ManagementMode.DirectVendor);
    var nextcloudUpdate = await new GitHubReleaseUpdateProvider(githubClient, nextcloudRecipe)
        .CheckAsync(nextcloudApplication, CancellationToken.None);
    Assert(nextcloudUpdate.Status == UpdateStatus.Available && nextcloudUpdate.AvailableVersion == "34.0.4", "Nextcloud GitHub update detection failed.");
    Assert(nextcloudUpdate.Language == "neutral" && nextcloudUpdate.ExecutionPlan?.Kind == UpdateExecutionKind.DownloadedMsi,
        "Nextcloud multi-language MSI plan is incorrect.");

    var installerPayload = Encoding.UTF8.GetBytes("verified installer fixture");
    var installerHash = Convert.ToHexString(SHA256.HashData(installerPayload));
    var downloadPlan = nextcloudUpdate.ExecutionPlan! with
    {
        DownloadUri = new Uri("https://github.com/fixture.msi"),
        FileName = "fixture.msi",
        Sha256 = installerHash,
        AllowedDownloadHosts = ["github.com"]
    };
    var downloadUpdate = nextcloudUpdate with { ExecutionPlan = downloadPlan };
    using var downloadClient = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(installerPayload)
    }));
    var verifiedInstaller = await new UpdatePackageDownloader(downloadClient, new StubAuthenticodeVerifier("Fixture Signer"))
        .DownloadAndVerifyAsync(downloadUpdate, Path.Combine(firefoxFixture, "cache"));
    Assert(File.Exists(verifiedInstaller.Path) && File.ReadAllBytes(verifiedInstaller.Path).SequenceEqual(installerPayload),
        "Verified update package download failed.");

    var installedFirefox = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox", "firefox.exe");
    if (File.Exists(installedFirefox))
    {
        var signature = new NativeAuthenticodeVerifier().Verify(installedFirefox, "Mozilla Corporation");
        Assert(signature.IsValid, $"Native Authenticode verification rejected Firefox: {signature.Error}");
    }
}
finally
{
    Directory.Delete(firefoxFixture, recursive: true);
}

var policyPath = Path.Combine(AppContext.BaseDirectory, "Configuration", "application-policies.json");
Assert(File.Exists(policyPath), "The default policy catalog was not copied to the test output.");
var policies = await new PolicyService().LoadAsync(policyPath);
Assert(policies.Count >= 5, "The default guard policies were not loaded.");
var battleNetPolicy = policies.Single(policy => policy.Id == "Blizzard.BattleNet");
Assert(PolicyService.IsMatch(battleNetPolicy, "Battle.net", "Blizzard Entertainment"), "Battle.net policy matching failed.");
Assert(!PolicyService.IsMatch(battleNetPolicy, "Battle.net", "Unrelated Publisher"), "Publisher matching accepted an unrelated app.");

var snapshot = await new ApplicationInventoryService(policyPath).ScanAsync();
Assert(snapshot.Applications.Count > 0, "The inventory scan did not find any applications.");
Assert(snapshot.Applications.All(static app => !string.IsNullOrWhiteSpace(app.DisplayName)), "An application has an empty display name.");
Assert(snapshot.Applications.All(static app => app.Evidence.Count > 0), "Every application must retain detection evidence.");
Assert(snapshot.Applications.Any(static app => app.InstalledOn.HasValue), "No Windows-reported installation dates were retained.");
Assert(snapshot.Applications.Where(static app => app.InstalledOn.HasValue).All(static app => app.InstalledOn!.Value.Year >= 2000),
    "An invalid installation date was retained.");
Assert(snapshot.Applications.Any(static app => app.RemovalPlan is not null), "No registered application-removal plans were retained.");
Assert(snapshot.Applications.Where(static app => app.IsSystemComponent).All(static app => app.RemovalPlan is null),
    "A protected Windows system component received a removal plan.");
Assert(ApplicationRemovalService.IsSuccessfulExitCode(0) && ApplicationRemovalService.IsSuccessfulExitCode(3010) &&
       !ApplicationRemovalService.IsSuccessfulExitCode(1), "Removal exit-code classification failed.");
var exactDuplicates = snapshot.Applications
    .Where(static app => !string.IsNullOrWhiteSpace(app.PrimaryInstallPath))
    .GroupBy(static app => $"{app.DisplayName}|{app.Publisher}|{app.InstalledVersion}|{app.PrimaryInstallPath}", StringComparer.OrdinalIgnoreCase)
    .Where(static group => group.Count() > 1)
    .ToArray();
Assert(exactDuplicates.Length == 0, $"Exact duplicate application records remain: {string.Join(", ", exactDuplicates.Select(static group => group.Key))}");

AssertProtectedApplication(
    snapshot,
    "DeepL",
    ManagementMode.ZeroInstall,
    expectedPathFileName: "DeepL.exe",
    requireVersion: true);
AssertProtectedApplication(
    snapshot,
    "Battle.net",
    ManagementMode.NativeSelfUpdater,
    expectedPathFileName: "Battle.net.exe",
    requireVersion: true);
AssertProtectedApplication(
    snapshot,
    "Brave Origin",
    ManagementMode.NativeSelfUpdater,
    expectedPathFileName: "brave.exe",
    requireVersion: true);

var nextcloud = snapshot.Applications.FirstOrDefault(app => app.DisplayName.Equals("Nextcloud", StringComparison.OrdinalIgnoreCase));
if (nextcloud is not null)
{
    Assert(nextcloud.ManagementMode == ManagementMode.DirectVendor, "Nextcloud should prefer its direct vendor release source.");
    Assert(nextcloud.NormalizedVersion == "34.0.3", $"Unexpected normalized Nextcloud version: {nextcloud.NormalizedVersion}");
    Assert(nextcloud.PrimaryInstallPath?.EndsWith("nextcloud.exe", StringComparison.OrdinalIgnoreCase) == true, "Nextcloud executable path was not resolved.");
    Assert(nextcloud.RemovalPlan?.Kind == RemovalKind.WindowsInstaller, "Nextcloud MSI removal plan was not detected.");
}

var deepLRemoval = snapshot.Applications.FirstOrDefault(app => app.DisplayName.Equals("DeepL", StringComparison.OrdinalIgnoreCase));
if (deepLRemoval is not null)
{
    Assert(deepLRemoval.RemovalPlan?.Kind == RemovalKind.ZeroInstall, "DeepL Zero Install removal plan was not detected.");
}

var openAlIsDetected = snapshot.Applications.Any(app => app.DisplayName.Equals("OpenAL", StringComparison.OrdinalIgnoreCase));
var openAlGuardIsUnmatched = snapshot.UnmatchedPolicies.Any(policy => policy.Id == "CreativeTechnology.OpenAL");
Assert(openAlIsDetected || openAlGuardIsUnmatched, "The preventive OpenAL provider guard was lost.");

AssertIntegrationAttached(
    snapshot,
    displayNamePrefix: "Notepad++",
    expectedMainVersionPrefix: "8.",
    expectedPackageFamily: "NotepadPlusPlus_2247w0b46hfww");
AssertIntegrationAttached(
    snapshot,
    displayNamePrefix: "Razer Chroma",
    expectedMainVersionPrefix: "4.",
    expectedPackageFamily: "RazerDynamicLighting_qemfkr3nbbywc");

Console.WriteLine($"NaxUpdater core smoke tests passed. {snapshot.Applications.Count} applications, {snapshot.UnmatchedPolicies.Count} unmatched guards, {snapshot.Issues.Count} scan issues.");
return 0;

static void AssertProtectedApplication(
    InventorySnapshot snapshot,
    string displayName,
    ManagementMode expectedMode,
    string expectedPathFileName,
    bool requireVersion)
{
    var application = snapshot.Applications.FirstOrDefault(app => app.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase));
    if (application is null)
    {
        return;
    }

    Assert(application.ManagementMode == expectedMode, $"{displayName} has unexpected management mode {application.ManagementMode}.");
    Assert(application.BlockedProviders.Contains("WinGet", StringComparer.OrdinalIgnoreCase), $"{displayName} lost its WinGet safety guard.");
    Assert(application.PrimaryInstallPath?.EndsWith(expectedPathFileName, StringComparison.OrdinalIgnoreCase) == true, $"{displayName} executable path was not resolved: {application.PrimaryInstallPath}");
    if (requireVersion)
    {
        Assert(!string.IsNullOrWhiteSpace(application.InstalledVersion), $"{displayName} version was not recovered.");
    }
}

static void AssertIntegrationAttached(
    InventorySnapshot snapshot,
    string displayNamePrefix,
    string expectedMainVersionPrefix,
    string expectedPackageFamily)
{
    var applications = snapshot.Applications
        .Where(app => app.DisplayName.StartsWith(displayNamePrefix, StringComparison.OrdinalIgnoreCase))
        .ToArray();
    if (applications.Length == 0)
    {
        return;
    }

    Assert(applications.Length == 1, $"{displayNamePrefix} still appears as {applications.Length} separate application records.");
    var application = applications[0];
    Assert(application.InstalledVersion?.StartsWith(expectedMainVersionPrefix, StringComparison.OrdinalIgnoreCase) == true,
        $"{displayNamePrefix} is showing the integration package version instead of the real application version: {application.InstalledVersion}");
    Assert(application.Evidence.Any(evidence =>
            evidence.Label == "Attached MSIX integration package" &&
            evidence.Value.Contains(expectedPackageFamily, StringComparison.OrdinalIgnoreCase)),
        $"{displayNamePrefix} lost its attached MSIX integration evidence.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static InstalledApplication CreateApplication(
    string identity,
    string name,
    string publisher,
    string version,
    string path,
    InstallScope scope,
    ManagementMode managementMode) => new(
        identity,
        name,
        publisher,
        version,
        version,
        "Test fixture",
        path,
        "Test fixture",
        null,
        null,
        scope,
        managementMode,
        ConfidenceLevel.High,
        false,
        [],
        null,
        []);

static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
{
    Content = new StringContent(json, Encoding.UTF8, "application/json")
};

static Dictionary<string, string> LoadResources(string path)
{
    var document = XDocument.Load(path);
    return document.Root!
        .Elements("data")
        .ToDictionary(
            element => element.Attribute("name")!.Value,
            element => element.Element("value")?.Value ?? string.Empty,
            StringComparer.Ordinal);
}

sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(responder(request));
}

sealed class StubAuthenticodeVerifier(string signer) : IAuthenticodeVerifier
{
    public AuthenticodeVerificationResult Verify(string filePath, string expectedSigner) =>
        new(true, signer, null);
}
