using System.Net;
using System.Text;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

internal static class OfficialSiteRegression
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        assert(OfficialSiteUpdateProvider.Expand("x/{version}/{locale}/{missing}{64}", new Dictionary<string, string> { ["version"] = "1.2", ["locale"] = "de" }, false) == "x/1.2/de/{missing}{64}" &&
               OfficialSiteUpdateProvider.Expand("{version}", new Dictionary<string, string> { ["version"] = "1.2" }, true) == @"1\.2",
            "Recipe templates expanded unknown names, regex quantifiers, or unescaped values in patterns.");

        var hash = new string('d', 64);
        var pages = new Dictionary<string, string>
        {
            ["https://vendor.example/versions.json"] = "{\"files\":[{\"v\":\"4.0.3\"},{\"v\":\"4.1.0\"},{\"v\":\"5.0.0\"}]}",
            ["https://vendor.example/4.1.0/SHA256SUMS"] = $"{hash}  win64/de/App Setup 4.1.0.exe\n{new string('e', 64)}  win64/en-US/App Setup 4.1.0.exe\n",
            ["https://feed.example/latest.yml"] = "files:\n  - url: https://cdn.example/1.3.0-7/setup.exe\n    sha512: " +
                Convert.ToBase64String(Enumerable.Repeat((byte)0xab, 64).ToArray()) + "\nversion: 1.3.0-7\nstagingPercentage: 25\n"
        };
        using var http = new HttpClient(new Respond(pages));
        InstalledApplication App(string name, string version, InstallScope scope = InstallScope.Machine) => new("fixture:" + name, name, "Vendor", version, version,
            "fixture", Environment.ProcessPath, "fixture", null, null, scope, ManagementMode.Registry, ConfidenceLevel.High, false, [], null, []);
        var recipe = new OfficialSiteRecipe
        {
            Id = "Vendor.App", DisplayNamePattern = @"^App \(x64 (?<locale>[a-z]{2})\)$", PublisherContains = "Vendor",
            VersionUrl = "https://vendor.example/versions.json", VersionPattern = "\"v\":\"(?<version>[0-9.]+)\"", StayOnInstalledMajor = true,
            DownloadUrl = "https://vendor.example/{version}/win64/{locale}/App%20Setup%20{version}.exe",
            HashUrl = "https://vendor.example/{version}/SHA256SUMS", HashPattern = @"(?m)^(?<hash>[0-9a-f]{64})\s+win64/{locale}/App Setup {version}\.exe$",
            ExpectedSigner = "Vendor Inc", AllowedHosts = ["vendor.example"], InstallerKind = UpdateExecutionKind.DownloadedExe,
            InstallerArguments = ["/S"], RequiresElevation = true
        };
        var provider = new OfficialSiteUpdateProvider(http, recipe);
        var offer = await provider.CheckAsync(App("App (x64 de)", "4.0.3"), CancellationToken.None);
        assert(offer.Status == UpdateStatus.Available && offer.AvailableVersion == "4.1.0" &&
               offer.ExecutionPlan is { Sha256: var sha, ExpectedSigner: "Vendor Inc", RequireAuthenticode: true, RequiresElevation: true, FileName: "App Setup 4.1.0.exe" } &&
               sha == hash && offer.ExecutionPlan.DownloadUri!.AbsoluteUri.EndsWith("/win64/de/App%20Setup%204.1.0.exe"),
            "The official site did not offer the highest same-major release with the locale's published hash and signer.");
        assert((await provider.CheckAsync(App("App (x64 de)", "4.1.0"), CancellationToken.None)).Status == UpdateStatus.Current,
            "A current installation was offered its own version.");

        var perUser = new OfficialSiteUpdateProvider(http, new OfficialSiteRecipe
        {
            Id = recipe.Id, DisplayNamePattern = recipe.DisplayNamePattern, PublisherContains = "Vendor", VersionUrl = recipe.VersionUrl,
            VersionPattern = recipe.VersionPattern, StayOnInstalledMajor = true, DownloadUrl = recipe.DownloadUrl, HashUrl = recipe.HashUrl,
            HashPattern = recipe.HashPattern, Unsigned = true, AllowedHosts = ["vendor.example"], InstallerKind = UpdateExecutionKind.DownloadedExe,
            InstallerArguments = ["/S"], CurrentUserInstallerArguments = ["/S"], RequiresElevation = true, AlwaysElevate = true
        });
        var elevated = await perUser.CheckAsync(App("App (x64 de)", "4.0.3", InstallScope.CurrentUser), CancellationToken.None);
        assert(elevated.ExecutionPlan is { RequiresElevation: true, RequireAuthenticode: false, ExpectedSigner: null },
            "A per-user registration of a Program Files install was not elevated, or an unsigned recipe still required a signer.");

        var feed = new OfficialSiteUpdateProvider(http, new OfficialSiteRecipe
        {
            Id = "Vendor.Feed", DisplayNamePattern = "^Feed App", VersionUrl = "https://feed.example/latest.yml",
            VersionPattern = @"(?ms)^\s*-\s*url:\s*(?<url>https://\S+)\s+sha512:\s*(?<hash>\S+).*?^version:\s*(?<version>[0-9]+(?:[.-][0-9]+)+)\s*$",
            DownloadUrl = "{url}", HashAlgorithm = "sha512", HashEncoding = "base64", RolloutPattern = @"(?m)^stagingPercentage:\s*(?<percent>[0-9]+)",
            ExpectedSigner = "Vendor Ltd", AllowedHosts = ["cdn.example"], InstallerKind = UpdateExecutionKind.DownloadedExe, InstallerArguments = ["/S"]
        });
        var staged = await feed.CheckAsync(App("Feed App 1.2.0", "1.2.0.5"), CancellationToken.None);
        assert(staged.Status == UpdateStatus.Current && staged.ExecutionPlan is null && staged.AnnouncedVersion == "1.3.0-7" && staged.Message?.Contains("25 %") == true,
            "A staged rollout was offered before the publisher released it to everyone.");
        pages["https://feed.example/latest.yml"] = pages["https://feed.example/latest.yml"].Replace("stagingPercentage: 25", "stagingPercentage: 100");
        var released = await feed.CheckAsync(App("Feed App 1.2.0", "1.2.0.5"), CancellationToken.None);
        assert(released.IsInstallable && released.ExecutionPlan?.Sha512 == string.Concat(Enumerable.Repeat("ab", 64)) && released.ExecutionPlan.Sha256 is null,
            "A base64 SHA-512 from an update feed was not converted, or the released version was not offered.");

        var foreign = new OfficialSiteUpdateProvider(http, new OfficialSiteRecipe
        {
            Id = "Vendor.Foreign", DisplayNamePattern = recipe.DisplayNamePattern, VersionUrl = recipe.VersionUrl, VersionPattern = recipe.VersionPattern,
            StayOnInstalledMajor = true, DownloadUrl = "https://elsewhere.example/{version}.exe", HashUrl = recipe.HashUrl, HashPattern = recipe.HashPattern,
            ExpectedSigner = "Vendor Inc", AllowedHosts = ["vendor.example"], InstallerKind = UpdateExecutionKind.DownloadedExe
        });
        assert((await foreign.CheckAsync(App("App (x64 de)", "4.0.3"), CancellationToken.None)) is { Status: UpdateStatus.Error, ExecutionPlan: null },
            "A recipe download outside its allowed hosts was accepted.");
        pages.Remove("https://vendor.example/4.1.0/SHA256SUMS");
        var unverified = await new OfficialSiteUpdateProvider(http, recipe).CheckAsync(App("App (x64 de)", "4.0.3"), CancellationToken.None);
        assert(unverified.Status == UpdateStatus.NewerReleaseKnown && unverified.ExecutionPlan is null,
            "A release without its published hash was made installable.");

        // Signature only (approved for publishers without a published hash): a version
        // built from page parts, the link from a second page, and a named file.
        pages["https://news.example/"] = "What's new in version 6.43 Build 16 ... What's new in version 6.43 Build 15";
        pages["https://news.example/download"] = "<a href=\"AutoDL?BundleId=777_abc\" title=\"Windows (64-bit)\">";
        var signatureOnly = new OfficialSiteRecipe
        {
            Id = "Vendor.Sig", DisplayNamePattern = "^Sig App$", VersionUrl = "https://news.example/",
            VersionPattern = @"What.s new in version (?<maj>[0-9]+)\.(?<min>[0-9]+) Build (?<build>[0-9]+)", VersionFormat = "{maj}.{min}.{build}",
            DownloadPageUrl = "https://news.example/download", DownloadPattern = "AutoDL\\?BundleId=(?<bundle>[0-9]+_[0-9a-f]+)\" title=\"Windows \\(64-bit\\)\"",
            DownloadUrl = "https://dl.example/AutoDL?BundleId={bundle}", FileName = "setup-{maj}{min}b{build}.exe",
            ExpectedSigner = "Vendor Inc", SignatureOnly = true, AllowedHosts = ["dl.example"],
            InstallerKind = UpdateExecutionKind.DownloadedExe, InstallerArguments = ["/S"], RequiresElevation = true
        };
        var signed = await new OfficialSiteUpdateProvider(http, signatureOnly).CheckAsync(App("Sig App", "6, 43, 15, 2"), CancellationToken.None);
        assert(signed.Status == UpdateStatus.Available && signed.AvailableVersion == "6.43.16" &&
               signed.ExecutionPlan is { SignatureOnly: true, Sha256: null, Sha512: null, ExpectedSigner: "Vendor Inc", RequireAuthenticode: true, FileName: "setup-643b16.exe" } &&
               signed.ExecutionPlan.DownloadUri!.AbsoluteUri == "https://dl.example/AutoDL?BundleId=777_abc",
            "A signature-only release lost its built version, second-page link, file name or signer pin.");
        var bound = signed with { ExecutionPlan = signed.ExecutionPlan! with { CheckGenerationId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15), InstalledVersionPrecondition = signed.InstalledVersion }, CorrelationKey = "fixture" };
        assert(UpdatePlanValidator.Validate(bound, DateTimeOffset.UtcNow) is null,
            "A signature-only plan with an exact signer was rejected.");
        assert(UpdatePlanValidator.Validate(bound with { ExecutionPlan = bound.ExecutionPlan! with { RequireAuthenticode = false } }, DateTimeOffset.UtcNow) is not null &&
               UpdatePlanValidator.Validate(bound with { ExecutionPlan = bound.ExecutionPlan! with { SignatureOnly = false } }, DateTimeOffset.UtcNow) is not null,
            "A plan without both a hash and a required signer was accepted.");
        var noSigner = await new OfficialSiteUpdateProvider(http, new OfficialSiteRecipe
        {
            Id = "Vendor.NoSigner", DisplayNamePattern = "^Sig App$", VersionUrl = signatureOnly.VersionUrl, VersionPattern = signatureOnly.VersionPattern,
            VersionFormat = signatureOnly.VersionFormat, DownloadUrl = "https://dl.example/x.exe", SignatureOnly = true, AllowedHosts = ["dl.example"],
            InstallerKind = UpdateExecutionKind.DownloadedExe
        }).CheckAsync(App("Sig App", "6.43.15"), CancellationToken.None);
        assert(noSigner.Status == UpdateStatus.Error && noSigner.ExecutionPlan is null, "A signature-only recipe without a signer was accepted.");

        // The downloader accepts a signature-only file only with the exact publisher.
        var payload = Encoding.UTF8.GetBytes("signature-only fixture");
        using var downloads = new HttpClient(new Bytes(payload));
        var cache = Path.Combine(Path.GetTempPath(), "naxupdater-signature-only-" + Guid.NewGuid().ToString("N"));
        try
        {
            var accepted = await new UpdatePackageDownloader(downloads, new Signer("Vendor Inc")).DownloadAndVerifyAsync(bound, cache);
            assert(File.ReadAllBytes(accepted.Path).SequenceEqual(payload) && accepted.Signer == "Vendor Inc", "A correctly signed signature-only installer was rejected.");
            accepted.Dispose();
            Directory.Delete(cache, true);
            try
            {
                await new UpdatePackageDownloader(downloads, new Signer("Someone Else")).DownloadAndVerifyAsync(bound, cache);
                assert(false, "A signature-only installer from another publisher was accepted.");
            }
            catch (InvalidDataException) { assert(true, "Wrong publisher rejected."); }
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }

    private sealed class Bytes(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(payload) });
    }

    private sealed class Signer(string actual) : IAuthenticodeVerifier
    {
        public AuthenticodeVerificationResult Verify(string filePath, string expectedSigner) =>
            actual.Equals(expectedSigner, StringComparison.OrdinalIgnoreCase)
                ? new(true, actual, null)
                : new(false, null, $"Expected signer '{expectedSigner}', found '{actual}'.");
    }

    private sealed class Respond(Dictionary<string, string> pages) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(pages.TryGetValue(request.RequestUri!.AbsoluteUri, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
