using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;
using Microsoft.Win32;
using System.IO.Compression;
using System.Management;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

Assert(VersionNormalizer.Normalize("34.0.3.20260826", "FirstThreeNumericComponents") == "34.0.3", "Nextcloud version normalization failed.");
Assert(VersionNormalizer.Normalize("26.8.2.20990", null) == "26.8.2.20990", "Unconfigured versions must remain unchanged.");
Assert(VersionOrder.Compare("154.0.1", "154.0") > 0, "Numeric update ordering failed.");
Assert(VersionOrder.Compare("155.0b5", "155.0") < 0, "Prerelease ordering failed.");
Assert(VersionOrder.Compare("26.08.19.0", "260819") == 0,
    "Equivalent dotted and compact release-date versions were treated as different.");
Assert(ManufacturerDriverService.NormalizeNvidiaVersion("32.0.16.1088") == "610.88",
    "NVIDIA Windows driver version normalization failed.");
Assert(ManufacturerDriverService.NormalizeTpLinkVersion("5102.24.126.4") == "24.126.4",
    "TP-Link platform-prefix normalization failed.");
Assert(ManufacturerDriverService.ProjectTpLinkVersion("5002.24.126.4", "5102.24.126.4") == "5102.24.126.4",
    "TP-Link Windows 11 branch projection failed.");
Assert(!ManufacturerDriverService.RealtekCatalogIsNewer("10.80.50.407", "2026-07-04", "10.80.50", "2026-08-28"),
    "Realtek's matching installed driver branch was incorrectly treated as older because of the package date.");
Assert(ManufacturerDriverService.RealtekCatalogIsNewer("10.80.50.407", "2026-07-04", "10.81.1", "2026-08-28"),
    "A genuinely newer Realtek driver branch was not detected.");
var normalStoreInstallOptions = StorePackageDeploymentService.CreateInstallOptions(force: false);
var forcedStoreInstallOptions = StorePackageDeploymentService.CreateInstallOptions(force: true);
Assert(!normalStoreInstallOptions.Force && forcedStoreInstallOptions.Force &&
       forcedStoreInstallOptions.AllowUpgradeToUnknownVersion &&
       forcedStoreInstallOptions.PackageInstallMode == Microsoft.Management.Deployment.PackageInstallMode.Silent,
    "The exact Store fallback is not configured to force a silent update through a stale applicability result.");
using (var openAiManifestFixture = JsonDocument.Parse("""
{
  "schemaVersion": 1,
  "buildVersion": "26.901.1978.0",
  "storeProductId": "9PLM9XGG6VKS",
  "packageIdentity": "OpenAI.Codex"
}
"""))
{
    var manifest = MsixStoreUpdateProvider.ParseOpenAiManifest(openAiManifestFixture.RootElement);
    Assert(manifest is
           {
               BuildVersion: "26.901.1978.0",
               StoreProductId: "9PLM9XGG6VKS",
               PackageIdentity: "OpenAI.Codex"
           },
        "OpenAI's official Windows update manifest was not accepted for the exact ChatGPT Store identity.");
}
using (var intelClient = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent("Intel(R) Network Connections Software, Release 31.2.2\nJuly 2026\nIntel(R) Ethernet Connection I219")
})))
{
    var intelService = new ManufacturerDriverService(intelClient);
    var intelDriver = new InstalledHardwareDriver(
        "intel-i219-fixture",
        "Intel(R) Ethernet Connection (7) I219-V",
        "Net",
        "Intel",
        "Intel",
        "12.19.2.63",
        "2025-05-07",
        "PCI\\VEN_8086&DEV_15BC",
        "e1d.inf");
    var intelUpdate = await intelService.CheckIntelI219Async(intelDriver, CancellationToken.None);
    Assert(intelUpdate.Status == ManufacturerDriverStatus.Available &&
           intelUpdate.AvailableVersion is { } intelAvailable &&
           intelAvailable.StartsWith("12.19.2.64", StringComparison.Ordinal) &&
           intelUpdate.ExecutableUpdate?.ExecutionPlan is
           {
               Kind: UpdateExecutionKind.DownloadedZipDriver,
               Sha256: "2CBFF42AA02519E49F02D8E95A6572C44310E97FE67C42E299F2ABA6EA9344F5",
               NestedInstallerRelativePath: "PRO1000\\Winx64\\W11\\e1d.inf",
               ExpectedHardwareId: "PCI\\VEN_8086&DEV_15BC"
           },
        "The exact Intel I219 Windows 11 INF update did not produce a verified direct-install plan.");
    var intelCurrent = await intelService.CheckIntelI219Async(
        intelDriver with { InstalledVersion = "12.19.2.64" },
        CancellationToken.None);
    Assert(intelCurrent.Status == ManufacturerDriverStatus.Current && intelCurrent.ExecutableUpdate is null,
        "The exact Intel 31.2.2 Windows 11 I219 INF was not recognized as already installed.");
}
using (var storeFixture = JsonDocument.Parse("""
{
  "Product": {
    "DisplaySkuAvailabilities": [
      {
        "Sku": {
          "Properties": {
            "Packages": [
              {
                "PackageFamilyName": "OpenAI.Codex_2p2nqsd0c76g0",
                "PackageFullName": "OpenAI.Codex_26.825.5331.0_x64__2p2nqsd0c76g0",
                "Architectures": ["x64"],
                "PackageFormat": "Msix"
              },
              {
                "PackageFamilyName": "OpenAI.Codex_2p2nqsd0c76g0",
                "PackageFullName": "OpenAI.Codex_26.900.1.0_arm64__2p2nqsd0c76g0",
                "Architectures": ["arm64"],
                "PackageFormat": "Msix"
              },
              {
                "PackageFamilyName": "OpenAI.Codex_2p2nqsd0c76g0",
                "PackageFullName": "OpenAI.Codex_26.825.5331.70_neutral_~_2p2nqsd0c76g0",
                "Architectures": ["x64"],
                "PackageFormat": "EAppxBundle"
              }
            ]
          }
        }
      }
    ]
  }
}
"""))
{
    var storeVersion = MicrosoftStoreProductMetadataClient.ParseLatestPackageVersion(
        storeFixture.RootElement,
        "OpenAI.Codex_2p2nqsd0c76g0",
        "x64",
        "26.825.4187.0");
    Assert(storeVersion == "26.825.5331.0", $"Store package metadata selected {storeVersion} instead of the applicable x64 ChatGPT version.");
    Assert(MicrosoftStoreProductMetadataClient.IsNewer("26.825.4187.0", storeVersion),
        "The observed ChatGPT Store transition was not detected as an update.");
    Assert(!MicrosoftStoreProductMetadataClient.IsNewer("26.825.5331.0", storeVersion),
        "The installed ChatGPT Store version was incorrectly reported as outdated.");
}
using (var bundleVersionFixture = JsonDocument.Parse("""
{
  "Product": {
    "DisplaySkuAvailabilities": [{
      "Sku": { "Properties": { "Packages": [
        {
          "PackageFamilyName": "Microsoft.PowerShell_8wekyb3d8bbwe",
          "PackageFullName": "Microsoft.PowerShell_2026.811.2337.0_neutral_~_8wekyb3d8bbwe",
          "Architectures": ["x64"],
          "PackageFormat": "MsixBundle"
        }
      ] } }
    }]
  }
}
"""))
{
    var incomparableBundleVersion = MicrosoftStoreProductMetadataClient.ParseLatestPackageVersion(
        bundleVersionFixture.RootElement,
        "Microsoft.PowerShell_8wekyb3d8bbwe",
        "x64",
        "7.6.5.0");
    Assert(incomparableBundleVersion is null,
        $"An unrelated Store bundle version {incomparableBundleVersion} was compared with the installed PowerShell package version.");
}

var manufacturerHash = new string('e', 64);
using (var manufacturerClient = new HttpClient(new StubHttpMessageHandler(request =>
{
    if (request.RequestUri?.AbsoluteUri.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) == true)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{manufacturerHash} 999.99-desktop-win10-win11-64bit-international-dch-whql.exe")
        };
    }
    return new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""
            <tr id="driverList">
              <td><a href='//www.nvidia.com/download/driverResults.aspx/123456/en-us'>GeForce Game Ready Driver</a></td>
              <td class="gridItem">999.99</td>
            </tr>
            """, Encoding.UTF8, "text/html")
    };
})))
{
    var driverFixture = new InstalledHardwareDriver(
        "driver-fixture",
        "NVIDIA GeForce RTX 5080",
        "Display",
        "NVIDIA",
        "NVIDIA",
        "32.0.16.1088",
        "20260722",
        "pci\\ven_10de&dev_2c02",
        "oem20.inf");
    var driverUpdate = await new ManufacturerDriverService(manufacturerClient)
        .CheckNvidiaAsync(driverFixture, CancellationToken.None);
    Assert(driverUpdate.Status == ManufacturerDriverStatus.Available &&
           driverUpdate.AvailableVersion == "999.99" &&
           driverUpdate.ExecutableUpdate?.ExecutionPlan is
           {
               Sha256: var parsedManufacturerHash,
               ExpectedSigner: "NVIDIA Corporation",
               RequiresElevation: true
           } && parsedManufacturerHash == manufacturerHash,
        "Verified NVIDIA manufacturer-driver discovery failed.");
}

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
    var gitHubCliRecipe = updateCatalog.GitHub.Single(recipe => recipe.Id == "GitHub.cli");
    var gitRecipe = updateCatalog.GitHub.Single(recipe => recipe.Id == "Git.Git");
    Assert(gitHubCliRecipe.Repository == "cli/cli" && gitHubCliRecipe.ExpectedSigner == "GitHub, Inc." &&
           gitRecipe.Repository == "git-for-windows/git" && gitRecipe.ExpectedSigner == "Johannes Schindelin",
        "Producer-owned GitHub CLI or Git release recipes are missing their exact repository/signer policy.");
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

    var gitHubCliHash = new string('a', 64);
    using var gitHubCliClient = new HttpClient(new StubHttpMessageHandler(_ => JsonResponse($$"""
        {
          "tag_name":"v2.100.0",
          "html_url":"https://github.com/cli/cli/releases/tag/v2.100.0",
          "assets":[{
            "name":"gh_2.100.0_windows_amd64.msi",
            "browser_download_url":"https://github.com/cli/cli/releases/download/v2.100.0/gh_2.100.0_windows_amd64.msi",
            "digest":"sha256:{{gitHubCliHash}}"
          }]
        }
        """)));
    var gitHubCliFixture = CreateApplication(
        "github-cli-test",
        "GitHub CLI",
        "GitHub, Inc.",
        "2.99.0",
        Path.Combine(firefoxFixture, "gh.exe"),
        InstallScope.Machine,
        ManagementMode.WindowsInstaller);
    var directGitHubCliUpdate = await new GitHubReleaseUpdateProvider(gitHubCliClient, gitHubCliRecipe)
        .CheckAsync(gitHubCliFixture, CancellationToken.None);
    Assert(directGitHubCliUpdate is
           {
               ProviderId: "github:cli/cli",
               Status: UpdateStatus.Available,
               AvailableVersion: "2.100.0",
               ExecutionPlan:
               {
                   Kind: UpdateExecutionKind.DownloadedMsi,
                   Sha256: var directGitHubCliHash,
                   ExpectedSigner: "GitHub, Inc."
               }
           } && directGitHubCliHash == gitHubCliHash,
        "GitHub CLI did not receive a producer-owned digest-backed MSI plan.");

    using var rateLimitedGitHubClient = new HttpClient(new StubHttpMessageHandler(request =>
    {
        if (request.RequestUri?.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                ReasonPhrase = "rate limit"
            };
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(
                HttpMethod.Get,
                "https://github.com/nextcloud-releases/desktop/releases/tag/v34.0.3")
        };
    }));
    var rateLimitedNextcloud = await new GitHubReleaseUpdateProvider(rateLimitedGitHubClient, nextcloudRecipe)
        .CheckAsync(nextcloudApplication, CancellationToken.None);
    Assert(rateLimitedNextcloud.Status == UpdateStatus.Current &&
           rateLimitedNextcloud.AvailableVersion == "34.0.3" &&
           rateLimitedNextcloud.Message?.Contains("latest-release redirect", StringComparison.OrdinalIgnoreCase) == true,
        $"Nextcloud did not fall back to its immutable latest-release redirect after a GitHub API failure: {rateLimitedNextcloud.Message}");

    var gogUpdateFixture = Directory.CreateDirectory(Path.Combine(firefoxFixture, "gog-autoupdate-verified"));
    var gogUpdaterDirectory = Directory.CreateDirectory(Path.Combine(gogUpdateFixture.FullName, "desktop-galaxy-updater"));
    var gogUpdaterPath = Path.Combine(gogUpdaterDirectory.FullName, "GalaxyUpdater.exe");
    await File.WriteAllBytesAsync(gogUpdaterPath, [1, 2, 3]);
    await File.WriteAllTextAsync(
        Path.Combine(gogUpdateFixture.FullName, "version.update.json"),
        """{"generation":8,"state":"Downloaded","version":"2.1.9.27"}""");
    var gogInstallDirectory = Directory.CreateDirectory(Path.Combine(firefoxFixture, "GOG Galaxy"));
    var gogExecutable = Path.Combine(gogInstallDirectory.FullName, "GalaxyClient.exe");
    await File.WriteAllBytesAsync(gogExecutable, []);
    var gogRedistFixture = Directory.CreateDirectory(Path.Combine(firefoxFixture, "gog-redists"));
    await File.WriteAllBytesAsync(Path.Combine(gogRedistFixture.FullName, "Qt6Core.dll"), [4, 5, 6]);
    await File.WriteAllBytesAsync(Path.Combine(gogRedistFixture.FullName, "GalaxyUpdater.exe"), [9, 9, 9]);
    var gogApplication = CreateApplication(
        "gog-native-test",
        "GOG GALAXY",
        "GOG.com",
        "2.1.8.30",
        gogExecutable,
        InstallScope.Machine,
        ManagementMode.Registry);
    var gogNativeUpdate = await new GogGalaxyUpdateProvider(
            gogUpdateFixture.FullName,
            new StubAuthenticodeVerifier("GOG  sp. z o.o"),
            _ => "2.1.9.27",
            gogRedistFixture.FullName)
        .CheckAsync(gogApplication, CancellationToken.None);
    Assert(gogNativeUpdate.Status == UpdateStatus.Available &&
           gogNativeUpdate.AvailableVersion == "2.1.9.27" &&
           gogNativeUpdate.ExecutionPlan is
           {
               Kind: UpdateExecutionKind.NativeCommand,
               RequiresElevation: true,
               NativeWorkingDirectory: var gogWorkingDirectory
           } &&
           gogWorkingDirectory == gogUpdaterDirectory.FullName &&
           gogNativeUpdate.ExecutionPlan.Arguments.Contains("/updateClient") &&
           gogNativeUpdate.ExecutionPlan.RunningProcessNames.Contains("GalaxyClient"),
        "GOG's downloaded, signed native update did not take precedence over the stale public catalog.");
    var gogStagingDirectory = Path.Combine(firefoxFixture, "gog-native-staging");
    var stagedGogUpdate = UpdateExecutionService.StageNativeCommandFiles(
        gogNativeUpdate.ExecutionPlan!,
        gogStagingDirectory);
    Assert(File.Exists(stagedGogUpdate.Executable) &&
           stagedGogUpdate.Executable.EndsWith("desktop-galaxy-updater\\GalaxyUpdater.exe", StringComparison.OrdinalIgnoreCase) &&
           stagedGogUpdate.WorkingDirectory.EndsWith("desktop-galaxy-updater", StringComparison.OrdinalIgnoreCase) &&
           File.Exists(Path.Combine(stagedGogUpdate.Root, "version.update.json")) &&
           File.Exists(Path.Combine(stagedGogUpdate.WorkingDirectory, "Qt6Core.dll")) &&
           File.ReadAllBytes(stagedGogUpdate.Executable).SequenceEqual(new byte[] { 1, 2, 3 }),
        "GOG's protected vendor update tree and installed runtime dependencies were not merged without replacing the new updater.");

    var metadataFixture = Directory.CreateDirectory(Path.Combine(firefoxFixture, "MetadataApp"));
    var metadataResources = Directory.CreateDirectory(Path.Combine(metadataFixture.FullName, "resources"));
    var metadataExecutable = Path.Combine(metadataFixture.FullName, "MetadataApp.exe");
    await File.WriteAllBytesAsync(metadataExecutable, []);
    await File.WriteAllTextAsync(Path.Combine(metadataResources.FullName, "app-update.yml"), """
        provider: generic
        url: https://updates.example.test/desktop
        publisherName:
          - Example Publisher LLC
        """);
    var metadataPayload = Encoding.UTF8.GetBytes("electron-builder installer fixture");
    var metadataSha512 = Convert.ToBase64String(SHA512.HashData(metadataPayload));
    using var metadataClient = new HttpClient(new StubHttpMessageHandler(request =>
        request.RequestUri?.AbsolutePath.EndsWith("latest.yml", StringComparison.OrdinalIgnoreCase) == true
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
                    version: 2.0.0
                    files:
                      - url: metadata-app-win-x64-2.0.0.exe
                        sha512: {{metadataSha512}}
                    path: metadata-app-win-x64-2.0.0.exe
                    sha512: {{metadataSha512}}
                    """, Encoding.UTF8, "text/yaml")
            }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(metadataPayload) }));
    var metadataApplication = CreateApplication(
        "metadata-test",
        "Metadata App",
        "Example Publisher LLC",
        "1.0.0",
        metadataExecutable,
        InstallScope.CurrentUser,
        ManagementMode.Registry);
    var metadataProvider = new ElectronBuilderUpdateProvider(metadataClient);
    Assert(metadataProvider.CanHandle(metadataApplication), "Installed electron-builder updater metadata was not discovered.");
    var metadataUpdate = await metadataProvider.CheckAsync(metadataApplication, CancellationToken.None);
    Assert(metadataUpdate.Status == UpdateStatus.Available && metadataUpdate.AvailableVersion == "2.0.0",
        "Generic installed updater metadata did not detect an update.");
    Assert(metadataUpdate.ExecutionPlan?.Sha512 == Convert.ToHexString(SHA512.HashData(metadataPayload)) &&
           metadataUpdate.ExecutionPlan.ExpectedSigner == "Example Publisher LLC" &&
           metadataUpdate.ExecutionPlan.Arguments.Contains("/S"),
        "Generic installed updater metadata lost its SHA-512, publisher, or silent-install policy.");
    var metadataInstaller = await new UpdatePackageDownloader(metadataClient, new StubAuthenticodeVerifier("Example Publisher LLC"))
        .DownloadAndVerifyAsync(metadataUpdate, Path.Combine(firefoxFixture, "metadata-cache"));
    Assert(File.ReadAllBytes(metadataInstaller.Path).SequenceEqual(metadataPayload), "SHA-512 verified metadata installer download failed.");

    var winRarFixtureDirectory = Directory.CreateDirectory(Path.Combine(firefoxFixture, "winrar-de"));
    var winRarFixtureExecutable = Path.Combine(winRarFixtureDirectory.FullName, "WinRAR.exe");
    await File.WriteAllBytesAsync(winRarFixtureExecutable, [1]);
    await File.WriteAllTextAsync(
        Path.Combine(winRarFixtureDirectory.FullName, "winrar.lng"),
        "; WinRAR 7.23\n; Deutsche Übersetzung\n");
    var winRarPayload = Encoding.UTF8.GetBytes("original RARLAB installer fixture");
    using var winRarClient = new HttpClient(new StubHttpMessageHandler(request =>
        request.RequestUri?.AbsolutePath.EndsWith("download.htm", StringComparison.OrdinalIgnoreCase) == true
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    <a href="/rar/winrar-x64-724.exe">English</a>
                    <a href="/rar/winrar-x64-724d.exe">German</a>
                    """, Encoding.UTF8, "text/html")
            }
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(winRarPayload)
            }));
    var winRarFixtureApplication = CreateApplication(
        "winrar-producer-test",
        "WinRAR 7.23 (64-bit)",
        "win.rar GmbH",
        "7.23.0",
        winRarFixtureExecutable,
        InstallScope.Machine,
        ManagementMode.Registry);
    var winRarDirectUpdate = await new WinRarUpdateProvider(winRarClient)
        .CheckAsync(winRarFixtureApplication, CancellationToken.None);
    Assert(winRarDirectUpdate is
           {
               ProviderId: "rarlab-winrar",
               Status: UpdateStatus.Available,
               AvailableVersion: "7.24",
               Language: "de",
               ExecutionPlan:
               {
                   DownloadUri.AbsoluteUri: "https://www.rarlab.com/rar/winrar-x64-724d.exe",
                   ExpectedSigner: "win.rar GmbH",
                   Sha256: var winRarFixtureHash
               }
           } && winRarFixtureHash == Convert.ToHexString(SHA256.HashData(winRarPayload)),
        "WinRAR did not receive its German producer-owned RARLAB installer plan.");

    var archiveFixturePath = Path.Combine(firefoxFixture, "nested-installer.nupkg");
    var nestedPayload = Encoding.UTF8.GetBytes("nested MSI fixture");
    using (var archive = ZipFile.Open(archiveFixturePath, ZipArchiveMode.Create))
    {
        var entry = archive.CreateEntry("redist/ArchiveRedist.msi");
        await using var output = entry.Open();
        await output.WriteAsync(nestedPayload);
    }
    var extractionFixture = Directory.CreateDirectory(Path.Combine(firefoxFixture, "nested-extraction"));
    var extractedInstaller = UpdateExecutionService.ExtractNestedInstaller(
        archiveFixturePath,
        "redist\\ArchiveRedist.msi",
        extractionFixture.FullName);
    Assert(File.ReadAllBytes(extractedInstaller).SequenceEqual(nestedPayload),
        "The exact nested MSI was not extracted from the verified archive.");
    var traversalRejected = false;
    try
    {
        UpdateExecutionService.ExtractNestedInstaller(archiveFixturePath, "..\\ArchiveRedist.msi", extractionFixture.FullName);
    }
    catch (InvalidDataException)
    {
        traversalRejected = true;
    }
    Assert(traversalRejected, "A traversal path was accepted for a nested installer.");
    var driverArchivePath = Path.Combine(firefoxFixture, "intel-driver.zip");
    using (var driverArchive = ZipFile.Open(driverArchivePath, ZipArchiveMode.Create))
    {
        foreach (var item in new Dictionary<string, string>
        {
            ["PRO1000/Winx64/W11/e1d.inf"] = "[Version]\nDriverVer = 05/07/2025,12.19.2.64\n[Models]\nDevice=PCI\\VEN_8086&DEV_15BC",
            ["PRO1000/Winx64/W11/e1d.cat"] = "signed catalog fixture",
            ["PRO1000/Winx64/W11/e1d.sys"] = "driver binary fixture",
            ["PRO1000/Winx64/W11/e1dmsg.dll"] = "message fixture",
            ["PRO1000/Winx64/W11/e1dn.inf"] = "unrelated driver"
        })
        {
            var entry = driverArchive.CreateEntry(item.Key);
            await using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            await writer.WriteAsync(item.Value);
        }
    }
    var driverExtraction = Directory.CreateDirectory(Path.Combine(firefoxFixture, "driver-extraction"));
    var extractedInf = new UpdateExecutionService(new StubAuthenticodeVerifier("Microsoft Windows Hardware Compatibility Publisher"))
        .ExtractAndVerifyDriverPackage(
            driverArchivePath,
            "PRO1000\\Winx64\\W11\\e1d.inf",
            driverExtraction.FullName,
            "PCI\\VEN_8086&DEV_15BC",
            "12.19.2.64",
            ["Microsoft Windows Hardware Compatibility Publisher"]);
    Assert(File.Exists(extractedInf) &&
           UpdateExecutionService.ReadInfDriverVersion(File.ReadAllText(extractedInf)) == "12.19.2.64" &&
           File.Exists(Path.Combine(driverExtraction.FullName, "e1d.cat")) &&
           File.Exists(Path.Combine(driverExtraction.FullName, "e1dmsg.dll")),
        "The exact hardware-matched Intel INF package was not extracted and verified.");
    var storeCatalogApplication = CreateApplication(
        "msix:Fixture.StoreApp_1234567890abc",
        "Store App",
        "Example Store Publisher",
        "3.0.0",
        Path.Combine(firefoxFixture, "StoreApp"),
        InstallScope.CurrentUser,
        ManagementMode.Msix);
    var unsupportedApplication = CreateApplication(
        "unsupported-test",
        "Uncatalogued Fixture",
        "Fixture Publisher",
        "1.0.0",
        Path.Combine(firefoxFixture, "Uncatalogued.exe"),
        InstallScope.CurrentUser,
        ManagementMode.Registry);
    var completeAssessment = await new UpdateCheckService(metadataClient, new UpdateProviderCatalog())
        .CheckAsync(new InventorySnapshot(DateTimeOffset.Now, [unsupportedApplication], [], []));
    Assert(completeAssessment.Results.Count == 1 &&
           completeAssessment.Results[0].Status == UpdateStatus.Unsupported &&
           completeAssessment.UnsupportedApplicationCount == 1,
        "The complete assessment omitted an application without a verifiable update source.");
    var storeAssessment = await new UpdateCheckService(metadataClient, new UpdateProviderCatalog())
        .CheckAsync(new InventorySnapshot(DateTimeOffset.Now, [storeCatalogApplication with { Identity = "msix:Uncatalogued.StoreApp_abc" }], [], []));
    Assert(storeAssessment.Results.Count == 1 &&
           storeAssessment.Results[0].Status == UpdateStatus.ManagedExternally &&
           storeAssessment.Results[0].ProviderId == "msix-store" &&
           storeAssessment.Results[0].Language == "application-managed" &&
           !storeAssessment.Results[0].IsInstallable &&
           storeAssessment.Results[0].ExecutionPlan is null &&
           storeAssessment.UnsupportedApplicationCount == 0,
        "An unmatched MSIX package was incorrectly labelled actionable, unknown, or unsupported.");

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
    var hashOnlyUpdate = downloadUpdate with
    {
        ExecutionPlan = downloadPlan with { ExpectedSigner = null, RequireAuthenticode = false }
    };
    var hashOnlyInstaller = await new UpdatePackageDownloader(downloadClient, new ThrowingAuthenticodeVerifier())
        .DownloadAndVerifyAsync(hashOnlyUpdate, Path.Combine(firefoxFixture, "hash-only-cache"));
    Assert(File.Exists(hashOnlyInstaller.Path), "Hash-only update verification failed for an explicitly unsigned exact-match installer.");
    using var redirectClient = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://mirror.example.test/fixture.msi"),
        Content = new ByteArrayContent(installerPayload)
    }));
    var redirectPlan = downloadPlan with { AllowHashVerifiedRedirects = true };
    var redirectedInstaller = await new UpdatePackageDownloader(redirectClient, new StubAuthenticodeVerifier("Fixture Signer"))
        .DownloadAndVerifyAsync(downloadUpdate with { ExecutionPlan = redirectPlan }, Path.Combine(firefoxFixture, "redirect-cache"));
    Assert(File.Exists(redirectedInstaller.Path), "Hash-verified HTTPS mirror redirect was rejected.");
    using var segmentedClient = new HttpClient(new StubHttpMessageHandler(request =>
    {
        if (request.Headers.Range?.Ranges.SingleOrDefault() is { } requestedRange)
        {
            var start = requestedRange.From!.Value;
            var end = requestedRange.To!.Value;
            var segment = installerPayload[(int)start..((int)end + 1)];
            var partialResponse = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(segment)
            };
            partialResponse.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, installerPayload.Length);
            return partialResponse;
        }
        var initialResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(installerPayload)
        };
        initialResponse.Headers.AcceptRanges.Add("bytes");
        return initialResponse;
    }));
    var segmentedInstaller = await new UpdatePackageDownloader(
            segmentedClient,
            new StubAuthenticodeVerifier("Fixture Signer"),
            segmentedDownloadThresholdBytes: 1)
        .DownloadAndVerifyAsync(downloadUpdate, Path.Combine(firefoxFixture, "segmented-cache"));
    Assert(File.ReadAllBytes(segmentedInstaller.Path).SequenceEqual(installerPayload),
        "Parallel ranged download did not reconstruct the verified installer exactly.");
    var resumeUpdate = downloadUpdate with { ProviderId = "resume-test" };
    var resumeCache = Path.Combine(firefoxFixture, "resume-cache");
    var resumeDirectory = Directory.CreateDirectory(Path.Combine(resumeCache, "resume-test", resumeUpdate.AvailableVersion!));
    var resumePartial = Path.Combine(resumeDirectory.FullName, downloadPlan.FileName + ".partial");
    await File.WriteAllBytesAsync(resumePartial, []);
    var resumeSegmentSize = (installerPayload.Length + 5) / 6;
    for (var index = 0; index < 6; index++)
    {
        var start = index * resumeSegmentSize;
        var end = Math.Min(installerPayload.Length, start + resumeSegmentSize);
        await File.WriteAllBytesAsync($"{resumePartial}.segment{index}", installerPayload[start..end]);
    }
    var resumedRangeRequests = 0;
    using var resumeClient = new HttpClient(new StubHttpMessageHandler(request =>
    {
        if (request.Headers.Range is not null)
        {
            Interlocked.Increment(ref resumedRangeRequests);
        }
        var initialResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(installerPayload)
        };
        initialResponse.Headers.AcceptRanges.Add("bytes");
        return initialResponse;
    }));
    var resumedInstaller = await new UpdatePackageDownloader(
            resumeClient,
            new StubAuthenticodeVerifier("Fixture Signer"),
            segmentedDownloadThresholdBytes: 1)
        .DownloadAndVerifyAsync(resumeUpdate, resumeCache);
    Assert(resumedRangeRequests == 0 && File.ReadAllBytes(resumedInstaller.Path).SequenceEqual(installerPayload),
        "A complete interrupted segmented download was not resumed during merge.");

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
Assert(policies.Count >= 4, "The default application policies were not loaded.");
var battleNetPolicy = policies.Single(policy => policy.Id == "Blizzard.BattleNet");
Assert(PolicyService.IsMatch(battleNetPolicy, "Battle.net", "Blizzard Entertainment"), "Battle.net policy matching failed.");
Assert(!PolicyService.IsMatch(battleNetPolicy, "Battle.net", "Unrelated Publisher"), "Publisher matching accepted an unrelated app.");

var snapshot = await new ApplicationInventoryService(policyPath).ScanAsync();
Assert(snapshot.Applications.Count > 0, "The inventory scan did not find any applications.");
Assert(snapshot.Applications.All(static app => !string.IsNullOrWhiteSpace(app.DisplayName)), "An application has an empty display name.");
Assert(snapshot.Applications.All(static app => app.Evidence.Count > 0), "Every application must retain detection evidence.");
var opaqueMsixNames = snapshot.Applications
    .Where(static app => app.ManagementMode == ManagementMode.Msix && Guid.TryParse(app.DisplayName, out _))
    .Select(static app => app.DisplayName)
    .ToArray();
Assert(opaqueMsixNames.Length == 0, $"MSIX applications still expose raw GUID names: {string.Join(", ", opaqueMsixNames)}");
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

var installedSignal = snapshot.Applications.FirstOrDefault(app => app.DisplayName.StartsWith("Signal", StringComparison.OrdinalIgnoreCase));
if (installedSignal is not null)
{
    using var liveSignalClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    var installedMetadataProvider = new ElectronBuilderUpdateProvider(liveSignalClient);
    Assert(installedMetadataProvider.CanHandle(installedSignal), "Signal's installed updater metadata was not discovered.");
    var liveSignalUpdate = await installedMetadataProvider.CheckAsync(installedSignal, CancellationToken.None);
    Assert(liveSignalUpdate.Status is UpdateStatus.Current or UpdateStatus.Available,
        $"Signal's installed updater feed could not be checked: {liveSignalUpdate.Message}");
    Assert(!string.IsNullOrWhiteSpace(liveSignalUpdate.AvailableVersion), "Signal's installed updater feed returned no version.");
}

var installedChatGpt = snapshot.Applications.FirstOrDefault(app =>
    app.Identity.Equals("msix:OpenAI.Codex_2p2nqsd0c76g0", StringComparison.OrdinalIgnoreCase));
if (installedChatGpt is not null)
{
    Assert(installedChatGpt.DisplayName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase),
        $"The OpenAI Store package is still shown as {installedChatGpt.DisplayName} instead of ChatGPT.");
    var chatGptStore = new StorePackageDeploymentService();
    var chatGptIdentity = await chatGptStore.ResolveAsync(
        "OpenAI.Codex_2p2nqsd0c76g0",
        installedChatGpt.DisplayName,
        installedChatGpt.Publisher,
        CancellationToken.None);
    Assert(chatGptIdentity?.ProductId == "9PLM9XGG6VKS",
        $"ChatGPT resolved to {chatGptIdentity?.ProductId ?? chatGptStore.LastError} instead of its exact current Store product.");
    var chatGptAssessment = await new MsixStoreUpdateProvider().CheckAsync(installedChatGpt, CancellationToken.None);
    Assert(chatGptAssessment.Status is UpdateStatus.Current or UpdateStatus.Available,
        $"ChatGPT Store assessment failed: {chatGptAssessment.Status} · {chatGptAssessment.Message}");
    Assert(chatGptAssessment.Status != UpdateStatus.Available ||
           (chatGptAssessment.ExecutionPlan is { Kind: UpdateExecutionKind.StorePackage } && chatGptAssessment.IsInstallable),
        "ChatGPT reports an update without an executable Store update plan.");
    Assert(chatGptAssessment.Status == UpdateStatus.Available ||
           (chatGptAssessment.ExecutionPlan is null && !chatGptAssessment.IsInstallable),
        "ChatGPT received a Store action even though no applicable update was reported.");
    using var openAiManifestClient = new HttpClient(new StubHttpMessageHandler(_ => JsonResponse("""
        {
          "schemaVersion": 1,
          "buildVersion": "26.901.1978.0",
          "storeProductId": "9PLM9XGG6VKS",
          "packageIdentity": "OpenAI.Codex"
        }
        """)));
    var officialManifestAssessment = await new MsixStoreUpdateProvider(openAiManifestClient).CheckAsync(
        installedChatGpt with
        {
            InstalledVersion = "26.831.2377.0",
            NormalizedVersion = "26.831.2377.0"
        },
        CancellationToken.None);
    Assert(officialManifestAssessment is
           {
               ProviderId: "openai-codex-store",
               Status: UpdateStatus.Available,
               AvailableVersion: "26.901.1978.0",
               ExecutionPlan:
               {
                   Kind: UpdateExecutionKind.StorePackage,
                   StoreProductId: "9PLM9XGG6VKS",
                   StorePackageFamilyName: "OpenAI.Codex_2p2nqsd0c76g0"
               } manifestPlan
           } && manifestPlan.RunningProcessNames.Contains("ChatGPT") &&
                manifestPlan.RunningProcessNames.Contains("Codex"),
        $"ChatGPT did not receive its official-manifest Store plan and close-first process guard: " +
        $"{officialManifestAssessment.ProviderId} · {officialManifestAssessment.Status} · " +
        $"{officialManifestAssessment.AvailableVersion} · {officialManifestAssessment.Message} · " +
        $"{string.Join(',', officialManifestAssessment.ExecutionPlan?.RunningProcessNames ?? [])}");
    var observedPreUpdateChatGpt = installedChatGpt with
    {
        InstalledVersion = "26.825.4187.0",
        NormalizedVersion = "26.825.4187.0"
    };
    var observedPreUpdateAssessment = await new MsixStoreUpdateProvider().CheckAsync(observedPreUpdateChatGpt, CancellationToken.None);
    Assert(observedPreUpdateAssessment.Status == UpdateStatus.Available &&
           VersionOrder.Compare(observedPreUpdateAssessment.AvailableVersion, "26.825.4187.0") > 0 &&
           observedPreUpdateAssessment.ExecutionPlan is
           {
               Kind: UpdateExecutionKind.StorePackage,
               StoreProductId: "9PLM9XGG6VKS",
               StorePackageFamilyName: "OpenAI.Codex_2p2nqsd0c76g0"
           },
        $"The observed ChatGPT Store update line was missed: {observedPreUpdateAssessment.Status} · {observedPreUpdateAssessment.AvailableVersion} · {observedPreUpdateAssessment.Message}");
}

var liveStoreIdentities = 0;
var storeDeployment = new StorePackageDeploymentService();
var installedCamera = snapshot.Applications.FirstOrDefault(app =>
    app.Identity.Equals("msix:Microsoft.WindowsCamera_8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase));
if (installedCamera is not null)
{
    var cameraStoreIdentity = await storeDeployment.ResolveAsync(
        "Microsoft.WindowsCamera_8wekyb3d8bbwe",
        installedCamera.DisplayName,
        installedCamera.Publisher,
        CancellationToken.None);
    if (cameraStoreIdentity is not null)
    {
        liveStoreIdentities++;
        Assert(cameraStoreIdentity.ProductId == "9WZDNCRFJBBG", $"Windows Camera resolved to unexpected Store Product ID {cameraStoreIdentity.ProductId}.");
        Assert(cameraStoreIdentity.PackageFamilyMatched, "Windows Camera should resolve through its exact Store package family.");
        var cameraAssessment = await new MsixStoreUpdateProvider().CheckAsync(installedCamera, CancellationToken.None);
        Assert(cameraAssessment.Status != UpdateStatus.Available ||
               (cameraAssessment.ExecutionPlan is { Kind: UpdateExecutionKind.StorePackage } && cameraAssessment.IsInstallable),
            "Windows Camera reports an update without an executable Store update plan.");
        Assert(cameraAssessment.Status == UpdateStatus.Available ||
               (cameraAssessment.ExecutionPlan is null && !cameraAssessment.IsInstallable),
            "Windows Camera received an update button without a reported applicable Store update.");
    }
}

var installedOnePassword = snapshot.Applications.FirstOrDefault(app =>
    app.Identity.Equals("msix:Agilebits.1Password_amwd9z03whsfe", StringComparison.OrdinalIgnoreCase));
if (installedOnePassword is not null)
{
    var onePasswordStoreIdentity = await storeDeployment.ResolveAsync(
        "Agilebits.1Password_amwd9z03whsfe",
        installedOnePassword.DisplayName,
        installedOnePassword.Publisher,
        CancellationToken.None);
    Assert(onePasswordStoreIdentity is not null, $"1Password Store identity was not resolved: {storeDeployment.LastError}");
    liveStoreIdentities++;
    Assert(onePasswordStoreIdentity!.ProductId == "9NZWS5X28P0J" && !onePasswordStoreIdentity.PackageFamilyMatched,
        $"1Password did not resolve through its expected Store migration identity: {onePasswordStoreIdentity.ProductId}.");
}

var installedComfy = snapshot.Applications.FirstOrDefault(app => app.DisplayName.StartsWith("Comfy Desktop", StringComparison.OrdinalIgnoreCase));
if (installedComfy is not null)
{
    using var liveComfyClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    var installedMetadataProvider = new ElectronBuilderUpdateProvider(liveComfyClient);
    Assert(installedMetadataProvider.CanHandle(installedComfy), "Comfy Desktop's installed updater metadata was not discovered.");
    var liveComfyUpdate = await installedMetadataProvider.CheckAsync(installedComfy, CancellationToken.None);
    Assert(liveComfyUpdate.Status is UpdateStatus.Current or UpdateStatus.Available,
        $"Comfy Desktop's installed updater feed could not be checked: {liveComfyUpdate.Message}");
    if (liveComfyUpdate.Status == UpdateStatus.Available)
    {
        Assert(liveComfyUpdate.ExecutionPlan?.ExpectedSigner == "Drip Artificial Inc",
            "Comfy Desktop did not inherit the trusted signer from its installed executable.");
    }
}

var installedGog = snapshot.Applications.FirstOrDefault(app => app.DisplayName.Equals("GOG GALAXY", StringComparison.OrdinalIgnoreCase));
if (installedGog is not null)
{
    var gogUpdate = await new GogGalaxyUpdateProvider().CheckAsync(installedGog, CancellationToken.None);
    Assert(gogUpdate.ProviderId == "gog-galaxy-native" &&
           gogUpdate.Status is UpdateStatus.Current or UpdateStatus.Available or UpdateStatus.ManagedExternally,
        $"GOG GALAXY was not checked through its installed native updater: {gogUpdate.Message}");
    if (File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GOG.com", "Galaxy", "autoupdate-verified", "version.update.json")))
    {
        Assert(gogUpdate.Status == UpdateStatus.Available &&
               VersionOrder.Compare(gogUpdate.AvailableVersion, installedGog.NormalizedVersion) > 0 &&
               gogUpdate.ExecutionPlan?.NativeExecutable?.EndsWith("GalaxyUpdater.exe", StringComparison.OrdinalIgnoreCase) == true &&
               Directory.Exists(gogUpdate.ExecutionPlan.NativeDependencyRoot),
            $"GOG's staged vendor update was missed: {gogUpdate.AvailableVersion} · {gogUpdate.Message}");
    }
}

var installedWinRar = snapshot.Applications.FirstOrDefault(app =>
    app.DisplayName.StartsWith("WinRAR", StringComparison.OrdinalIgnoreCase));
if (installedWinRar is not null)
{
    Assert(installedWinRar.ManagementMode == ManagementMode.Registry && installedWinRar.NormalizedVersion == "7.23.0",
        $"WinRAR's MSIX shell extension was not attached to its real Win32 installation: {installedWinRar.ManagementMode} · {installedWinRar.NormalizedVersion}.");
    Assert(installedWinRar.Evidence.Any(static evidence =>
            evidence.Label == "Attached MSIX integration package" &&
            evidence.Value.Contains("WinRAR.ShellExtension", StringComparison.OrdinalIgnoreCase)),
        "WinRAR lost the evidence for its attached MSIX shell extension.");
    using var liveWinRarClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var liveWinRarUpdate = await new WinRarUpdateProvider(liveWinRarClient)
        .CheckAsync(installedWinRar, CancellationToken.None);
    Assert(liveWinRarUpdate.Status is UpdateStatus.Current or UpdateStatus.Available &&
           liveWinRarUpdate.ProviderId == "rarlab-winrar" &&
           liveWinRarUpdate.ReleaseNotesUrl == "https://www.rarlab.com/download.htm",
        $"WinRAR was not checked directly against RARLAB: {liveWinRarUpdate.Status} · {liveWinRarUpdate.Message}");
}

var installedGitHubCli = snapshot.Applications.FirstOrDefault(app =>
    app.DisplayName.Equals("GitHub CLI", StringComparison.OrdinalIgnoreCase));
if (installedGitHubCli is not null)
{
    var producerCatalog = await UpdateProviderCatalogLoader.LoadAsync(
        Path.Combine(AppContext.BaseDirectory, "Configuration", "update-providers.json"));
    var gitHubCliRecipe = producerCatalog.GitHub.Single(recipe => recipe.Id == "GitHub.cli");
    using var liveGitHubCliClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var gitHubCliUpdate = await new GitHubReleaseUpdateProvider(liveGitHubCliClient, gitHubCliRecipe)
        .CheckAsync(installedGitHubCli, CancellationToken.None);
    Assert(gitHubCliUpdate.Status is UpdateStatus.Current or UpdateStatus.Available,
        $"GitHub CLI's producer-owned release was not assessed: {gitHubCliUpdate.Message}");
    if (gitHubCliUpdate.Status == UpdateStatus.Available)
    {
        Assert(gitHubCliUpdate.ExecutionPlan is
               {
                   Kind: UpdateExecutionKind.DownloadedMsi,
                   Sha256.Length: 64,
                   DownloadUri.Host: "github.com"
               } &&
               gitHubCliUpdate.ExecutionPlan.DownloadUri.AbsoluteUri.Contains("/cli/cli/releases/", StringComparison.OrdinalIgnoreCase),
            $"GitHub CLI {gitHubCliUpdate.AvailableVersion} was detected without its producer-owned digest-backed MSI plan: {gitHubCliUpdate.Message}");
    }
}

var installedOpenSslEntries = snapshot.Applications
    .Where(app => app.DisplayName.StartsWith("OpenSSL ", StringComparison.OrdinalIgnoreCase))
    .ToArray();
Assert(installedOpenSslEntries.Length <= 1,
    $"Multiple installed versions from one OpenSSL MSI upgrade family were not collapsed: {string.Join(", ", installedOpenSslEntries.Select(static app => app.InstalledVersion))}");
var installedOpenSsl = installedOpenSslEntries.FirstOrDefault();
if (installedOpenSsl is not null)
{
    if (installedOpenSsl.Evidence.Count(static evidence => evidence.Label == "Registry version") > 1)
    {
        var highestRegisteredVersion = installedOpenSsl.Evidence
            .Where(static evidence => evidence.Label == "Registry version")
            .Select(static evidence => evidence.Value)
            .OrderByDescending(static version => version, Comparer<string>.Create(VersionOrder.Compare))
            .First();
        Assert(VersionOrder.Compare(installedOpenSsl.NormalizedVersion, highestRegisteredVersion) >= 0,
            $"OpenSSL retained an older MSI product generation instead of {highestRegisteredVersion}.");
    }
}

var doom = snapshot.Applications.FirstOrDefault(app => app.DisplayName.Equals("DOOM The Dark Ages", StringComparison.OrdinalIgnoreCase));
if (doom is not null && string.IsNullOrWhiteSpace(doom.Evidence.FirstOrDefault(static evidence => evidence.Label == "Install date")?.Value))
{
    Assert(doom.InstalledOn.HasValue, "The installation-folder date fallback did not resolve DOOM's missing registry date.");
    Assert(doom.InstallDateSource == "Installation folder modified date", $"DOOM used an unexpected date source: {doom.InstallDateSource}");
}

AssertProtectedApplication(
    snapshot,
    "DeepL",
    ManagementMode.ZeroInstall,
    expectedPathFileName: "DeepL.exe",
    requireVersion: true);
var installedDeepL = snapshot.Applications.FirstOrDefault(app =>
    app.DisplayName.Equals("DeepL", StringComparison.OrdinalIgnoreCase));
if (installedDeepL is not null)
{
    var deepLAssessment = await new ZeroInstallUpdateProvider(new ProcessQueryRunner())
        .CheckAsync(installedDeepL, CancellationToken.None);
    Assert(deepLAssessment.Status is UpdateStatus.Current or UpdateStatus.Available &&
           deepLAssessment.AvailableVersion == "26.8.2.20990" &&
           deepLAssessment.Message?.Contains("digest", StringComparison.OrdinalIgnoreCase) == true,
        $"DeepL's Zero Install refresh and cached-selection fallback both failed: {deepLAssessment.Message}");
}
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
var installedBrave = snapshot.Applications.FirstOrDefault(app =>
    app.DisplayName.Equals("Brave Origin", StringComparison.OrdinalIgnoreCase));
if (installedBrave is not null)
{
    var braveAssessment = await new UpdateCheckService([])
        .CheckAsync(new InventorySnapshot(DateTimeOffset.Now, [installedBrave], [], []));
    Assert(braveAssessment.Results.Single().Status == UpdateStatus.ManagedExternally &&
           braveAssessment.Results.Single().AvailableVersion is null,
        "Brave's native updater was replaced by a Chromium/catalog version comparison.");
}

var nextcloud = snapshot.Applications.FirstOrDefault(app => app.DisplayName.Equals("Nextcloud", StringComparison.OrdinalIgnoreCase));
if (nextcloud is not null)
{
    Assert(nextcloud.ManagementMode == ManagementMode.DirectVendor, "Nextcloud should prefer its direct vendor release source.");
    Assert(nextcloud.NormalizedVersion == "34.0.3", $"Unexpected normalized Nextcloud version: {nextcloud.NormalizedVersion}");
    Assert(nextcloud.PrimaryInstallPath?.EndsWith("nextcloud.exe", StringComparison.OrdinalIgnoreCase) == true, "Nextcloud executable path was not resolved.");
    Assert(nextcloud.RemovalPlan?.Kind == RemovalKind.WindowsInstaller, "Nextcloud MSI removal plan was not detected.");
    var liveProviderCatalog = await UpdateProviderCatalogLoader.LoadAsync(
        Path.Combine(AppContext.BaseDirectory, "Configuration", "update-providers.json"));
    var liveNextcloudRecipe = liveProviderCatalog.GitHub.Single(recipe => recipe.Id == "Nextcloud.NextcloudDesktop");
    using var liveNextcloudClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var liveNextcloudAssessment = await new GitHubReleaseUpdateProvider(liveNextcloudClient, liveNextcloudRecipe)
        .CheckAsync(nextcloud, CancellationToken.None);
    Assert(liveNextcloudAssessment.Status is UpdateStatus.Current or UpdateStatus.Available,
        $"Nextcloud's live release check and latest-tag fallback both failed: {liveNextcloudAssessment.Message}");
}

var deepLRemoval = snapshot.Applications.FirstOrDefault(app => app.DisplayName.Equals("DeepL", StringComparison.OrdinalIgnoreCase));
if (deepLRemoval is not null)
{
    Assert(deepLRemoval.RemovalPlan?.Kind == RemovalKind.ZeroInstall, "DeepL Zero Install removal plan was not detected.");
}

Assert(snapshot.UnmatchedPolicies.All(static policy => policy.Id != "CreativeTechnology.OpenAL"),
    "OpenAL must not be presented as a persistent policy when it is not installed.");

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

using var liveManufacturerClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var liveManufacturerDrivers = await new ManufacturerDriverService(liveManufacturerClient).CheckAsync();
var liveNvidiaDriver = liveManufacturerDrivers.Results.FirstOrDefault(static result =>
    result.Driver.DeviceName.Contains("NVIDIA GeForce", StringComparison.OrdinalIgnoreCase));
if (liveNvidiaDriver is not null)
{
    Assert(liveNvidiaDriver.Status is ManufacturerDriverStatus.Current or ManufacturerDriverStatus.Available,
        $"The installed NVIDIA GPU was not checked against NVIDIA's manufacturer catalog: {liveNvidiaDriver.Message}");
    Assert(liveNvidiaDriver.Status != ManufacturerDriverStatus.Available ||
           liveNvidiaDriver.ExecutableUpdate?.ExecutionPlan is { Sha256.Length: 64, ExpectedSigner: "NVIDIA Corporation" },
        "The live NVIDIA update lacks a verified manufacturer execution plan.");
}
var liveRealtek = liveManufacturerDrivers.Results.FirstOrDefault(static result =>
    result.Driver.HardwareId?.Contains("VEN_10EC&DEV_8125", StringComparison.OrdinalIgnoreCase) == true);
if (HasDriverRegistration("VEN_10EC&DEV_8125"))
{
    Assert(liveRealtek is not null &&
           liveRealtek.SourceName.Contains("Realtek", StringComparison.OrdinalIgnoreCase) &&
           liveRealtek.SourceUri?.Host.Equals("www.realtek.com", StringComparison.OrdinalIgnoreCase) == true,
        "The RTL8125 Ethernet driver was not checked against Realtek's exact official catalog.");
}
var liveTpLink = liveManufacturerDrivers.Results.FirstOrDefault(static result =>
    result.Driver.HardwareId?.Contains("VID_3625&PID_010A", StringComparison.OrdinalIgnoreCase) == true);
if (HasDriverRegistration("VID_3625&PID_010A"))
{
    Assert(liveTpLink is not null &&
           liveTpLink.SourceName.Contains("TBE400UH", StringComparison.OrdinalIgnoreCase) &&
           liveTpLink.SourceUri?.AbsoluteUri.Contains("archer-tbe400uh", StringComparison.OrdinalIgnoreCase) == true,
        "The disconnected TP-Link Archer TBE400UH driver registration was not matched to its exact manufacturer page.");
}
var liveIntelEthernet = liveManufacturerDrivers.Results.FirstOrDefault(static result =>
    result.Driver.HardwareId?.Contains("VEN_8086&DEV_15BC", StringComparison.OrdinalIgnoreCase) == true);
if (HasDriverRegistration("VEN_8086&DEV_15BC"))
{
    Assert(liveIntelEthernet is not null &&
           liveIntelEthernet.Status is ManufacturerDriverStatus.Current or ManufacturerDriverStatus.Available &&
           liveIntelEthernet.SourceName.Contains("I219", StringComparison.OrdinalIgnoreCase) &&
           liveIntelEthernet.SourceUri?.Host.Contains("intel.com", StringComparison.OrdinalIgnoreCase) == true,
        "The Intel I219-V driver was not checked against Intel's exact supported Ethernet release.");
}
var liveDellMonitor = liveManufacturerDrivers.Results.FirstOrDefault(static result =>
    result.Driver.HardwareId?.Contains("DELA1E4", StringComparison.OrdinalIgnoreCase) == true);
if (HasDriverRegistration("DELA1E4"))
{
    Assert(liveDellMonitor is not null &&
           liveDellMonitor.SourceUri?.Query.Contains("driverid=m46j9", StringComparison.OrdinalIgnoreCase) == true,
        "The Dell AW3423DW monitor driver did not receive its exact Dell driver page.");
}
var liveWdDrive = liveManufacturerDrivers.Results.FirstOrDefault(static result =>
    result.SourceName.StartsWith("WD Elements", StringComparison.OrdinalIgnoreCase));
if (HasPresentPnpDevice("VID_1058&PID_25A3", "WD Elements"))
{
    Assert(liveWdDrive is not null && liveWdDrive.Status == ManufacturerDriverStatus.NoUpdateRequired &&
           liveWdDrive.Driver.DeviceName.Contains("WD Elements", StringComparison.OrdinalIgnoreCase) &&
           liveWdDrive.AvailableVersion?.Contains("SES", StringComparison.OrdinalIgnoreCase) == true &&
           liveWdDrive.SourceUri?.AbsoluteUri.Contains("13977", StringComparison.OrdinalIgnoreCase) == true,
        "The present WD Elements external drive was omitted or misclassified.");
}
var razerRows = liveManufacturerDrivers.Results.Count(static result =>
    result.Driver.Provider.Contains("Razer", StringComparison.OrdinalIgnoreCase));
if (HasDriverRegistration("VID_1532"))
{
    Assert(razerRows == 1, $"Razer interface drivers were not collapsed into one Synapse/firmware category: {razerRows} rows.");
    if (snapshot.Applications.Any(static app => app.DisplayName.Equals("Razer Synapse", StringComparison.OrdinalIgnoreCase)))
    {
        var razer = liveManufacturerDrivers.Results.Single(static result =>
            result.Driver.Provider.Contains("Razer", StringComparison.OrdinalIgnoreCase));
        Assert(razer.Status == ManufacturerDriverStatus.VendorSoftwareManaged &&
               razer.Driver.DeviceName == "Razer peripherals" &&
               razer.Driver.GroupMembers is { Count: > 1 } &&
               razer.AvailableVersion == "3" &&
               razer.Message.Contains("Huntsman V3 Pro 8KHz", StringComparison.OrdinalIgnoreCase) &&
               razer.Message.Contains("Kiyo Pro", StringComparison.OrdinalIgnoreCase) &&
               razer.Message.Contains("Nommo Pro", StringComparison.OrdinalIgnoreCase) &&
               razer.Message.Contains("Installed Synapse", StringComparison.OrdinalIgnoreCase) &&
               razer.SourceUri?.AbsoluteUri.Contains("4166", StringComparison.OrdinalIgnoreCase) == true,
            "Razer products were not grouped under the detected Synapse installation and official firmware catalog.");
    }
}
var intelSourceOnlyRows = liveManufacturerDrivers.Results
    .Where(static result => result.Driver.Provider.Contains("Intel", StringComparison.OrdinalIgnoreCase) &&
                            result.Driver.HardwareId?.Contains("VEN_8086&DEV_15BC", StringComparison.OrdinalIgnoreCase) != true)
    .ToArray();
Assert(intelSourceOnlyRows.All(static result =>
        result.Status == ManufacturerDriverStatus.OfficialSourceOnly &&
        result.SourceUri?.Host.Contains("intel.com", StringComparison.OrdinalIgnoreCase) == true &&
        (result.AvailableVersion is not null || result.Message.Contains("No update is claimed", StringComparison.OrdinalIgnoreCase))),
    "An Intel component without exact INF applicability was labelled managed/outdated or lacked an official Intel source.");
var intelChipsetGroup = intelSourceOnlyRows.FirstOrDefault(static result =>
    result.Driver.Identity.EndsWith(":intel:chipset", StringComparison.OrdinalIgnoreCase));
if (intelChipsetGroup is not null)
{
    Assert(intelChipsetGroup.Driver.DeviceName.Contains("Chipset Device Software", StringComparison.OrdinalIgnoreCase) &&
           intelChipsetGroup.Driver.GroupMembers is { Count: > 1 } &&
           intelChipsetGroup.AvailableVersion == "10.1.20658.8883" &&
           intelChipsetGroup.SourceUri?.AbsoluteUri.Contains("19347", StringComparison.OrdinalIgnoreCase) == true,
        "Intel chipset component INFs were not grouped and checked against Intel's official package.");
}

using var capabilityClient = new HttpClient();
var installedMetadataCoverage = snapshot.Applications.Count(new ElectronBuilderUpdateProvider(capabilityClient).CanHandle);
var productionProviderCatalog = await UpdateProviderCatalogLoader.LoadAsync(
    Path.Combine(AppContext.BaseDirectory, "Configuration", "update-providers.json"));
var productionUpdateSnapshot = await new UpdateCheckService(capabilityClient, productionProviderCatalog)
    .CheckAsync(snapshot, CancellationToken.None);
Assert(productionUpdateSnapshot.Results.All(static result =>
        result.ProviderId != "federated-public-catalogs" &&
        !result.ProviderDisplayName.Contains("WinGet", StringComparison.OrdinalIgnoreCase) &&
        !result.ProviderDisplayName.Contains("Scoop", StringComparison.OrdinalIgnoreCase)),
    "The production update chain still exposed a community package-manager catalog.");
if (installedGitHubCli is not null)
{
    Assert(productionUpdateSnapshot.Results.Single(result => result.ApplicationIdentity == installedGitHubCli.Identity).ProviderId == "github:cli/cli",
        "The production provider chain did not route GitHub CLI directly to its producer-owned release.");
}
var installedGit = snapshot.Applications.FirstOrDefault(app => app.DisplayName.Equals("Git", StringComparison.OrdinalIgnoreCase));
if (installedGit is not null)
{
    Assert(productionUpdateSnapshot.Results.Single(result => result.ApplicationIdentity == installedGit.Identity).ProviderId == "github:git-for-windows/git",
        "The production provider chain did not route Git directly to the producer-owned Git for Windows release.");
}
var producerOwnedCoverage = productionUpdateSnapshot.Results.Count(static result => result.Status != UpdateStatus.Unsupported);
Console.WriteLine($"NaxUpdater core smoke tests passed. {snapshot.Applications.Count} applications, {liveManufacturerDrivers.Results.Count} manufacturer drivers, {liveManufacturerDrivers.Results.Count(static result => result.Status == ManufacturerDriverStatus.Available)} driver updates, {installedMetadataCoverage} installed-metadata providers, {producerOwnedCoverage} producer-owned assessments, {liveStoreIdentities} live Store identities, {snapshot.UnmatchedPolicies.Count} unmatched guards, {snapshot.Issues.Count} scan issues.");
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
    Assert(application.BlockedProviders.Count == 0, $"{displayName} still carries obsolete community-catalog guards.");
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

static bool HasDriverRegistration(string pattern)
{
    try
    {
        using var classes = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class");
        if (classes is null) return false;
        foreach (var className in classes.GetSubKeyNames())
        {
            using var classKey = classes.OpenSubKey(className);
            if (classKey is null) continue;
            foreach (var instanceName in classKey.GetSubKeyNames())
            {
                using var instance = classKey.OpenSubKey(instanceName);
                if (instance?.GetValue("MatchingDeviceId")?.ToString()?.Contains(pattern, StringComparison.OrdinalIgnoreCase) == true)
                {
                    return true;
                }
            }
        }
    }
    catch
    {
        // The corresponding machine-specific assertion is skipped when raw evidence is inaccessible.
    }
    return false;
}

static bool HasPresentPnpDevice(string identityPattern, string namePattern)
{
    try
    {
        using var searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, HardwareID, Present FROM Win32_PnPEntity");
        foreach (ManagementObject device in searcher.Get())
        {
            if (device["Present"] is not true) continue;
            var identity = string.Join(';', device["HardwareID"] as string[] ?? []) + ";" + device["PNPDeviceID"];
            var name = device["Name"]?.ToString() ?? string.Empty;
            if (identity.Contains(identityPattern, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(namePattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
    }
    catch
    {
        // The corresponding machine-specific assertion is skipped when raw evidence is inaccessible.
    }
    return false;
}

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

sealed class ThrowingAuthenticodeVerifier : IAuthenticodeVerifier
{
    public AuthenticodeVerificationResult Verify(string filePath, string expectedSigner) =>
        throw new InvalidOperationException("Authenticode verification must not run for an explicitly hash-only plan.");
}
