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
        assert(staged.Status == UpdateStatus.NewerReleaseKnown && staged.ExecutionPlan is null && staged.Message?.Contains("25 %") == true,
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
    }

    private sealed class Respond(Dictionary<string, string> pages) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(pages.TryGetValue(request.RequestUri!.AbsoluteUri, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
