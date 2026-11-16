using System.Net;
using System.Text.Json;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

internal static class SnagitHistoryRegression
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        var catalog = await UpdateProviderCatalogLoader.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Configuration", "update-providers.json"));
        var recipe = catalog.Official.Single(r => r.Id == "TechSmith.Snagit");
        assert(recipe.VersionUrl == "https://support.techsmith.com/api/v2/help_center/en-us/sections/39094961933453/articles.json?per_page=100" &&
            recipe.StayOnInstalledMajor, "Snagit is pinned to one year's history or permits an unintended major upgrade.");
        var body = JsonSerializer.Serialize(new { articles = new[]
        {
            new { title = "Snagit Windows 2026 Version History", body = "<h2>What's New in 2026.4.0 (6 October 2026)</h2>" },
            new { title = "Snagit Windows 2027 Version History", body = "<h2>What's New in 2027.0.1</h2><h2>What's New in 2027.0.0</h2>" },
            new { title = "Snagit Windows 2028 Version History", body = "<h2>What's New in 2028.0.0</h2>" }
        }, next_page = (string?)null }, new JsonSerializerOptions
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        using var http = new HttpClient(new Handler(body));
        var provider = new OfficialSiteUpdateProvider(http, recipe);
        var current2026 = await provider.CheckAsync(App("26.4.0.100"), default);
        var current2027 = await provider.CheckAsync(App("27.0.1.13131"), default);
        var update2027 = await provider.CheckAsync(App("27.0.0.13131"), default);
        assert(current2026.Status == UpdateStatus.Current && current2026.AvailableVersion == "26.4.0" &&
            current2027.Status == UpdateStatus.Current && current2027.AvailableVersion == "27.0.1",
            "Snagit history from a different year caused a failed check, major upgrade or downgrade.");
        assert(update2027.IsInstallable && update2027.AvailableVersion == "27.0.1" &&
            update2027.ExecutionPlan is { SignatureOnly: true, ExpectedSigner: "TechSmith Corporation" } plan &&
            plan.DownloadUri?.AbsoluteUri == "https://download.techsmith.com/snagit/releases/2701/snagit.exe",
            "A Snagit patch on the installed release line lost its exact official URL or signer policy.");
    }

    private static InstalledApplication App(string version) => new("fixture:snagit", "Snagit", "TechSmith Corporation",
        version, version, "fixture", null, null, null, null, InstallScope.Machine, ManagementMode.Registry,
        ConfidenceLevel.High, false, [], null, []);
    private sealed class Handler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
