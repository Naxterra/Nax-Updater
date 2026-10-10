using System.Net;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

internal static class VanishedFeedRegression
{
    public static async Task RunAsync(Action<bool, string> assert, string fixture)
    {
        // Upgrade Sync's app-update.yml names a GitHub repository that no longer
        // exists publicly; its own updater gets the same 404.
        var directory = Directory.CreateDirectory(Path.Combine(fixture, "vanished-feed"));
        var resources = Directory.CreateDirectory(Path.Combine(directory.FullName, "resources"));
        var executable = Path.Combine(directory.FullName, "Upgrade Sync.exe");
        File.WriteAllBytes(executable, []);
        File.WriteAllText(Path.Combine(resources.FullName, "app-update.yml"), "owner: ddp-kyocera\nrepo: upgrade-sync\nprovider: github\n");
        var application = new InstalledApplication("fixture:upgrade-sync", "Upgrade Sync 1.1.5", "Kyocera Document Solutions Philippines, Inc.",
            "1.1.5", "1.1.5", "fixture", executable, "fixture", null, null, InstallScope.CurrentUser, ManagementMode.Registry,
            ConfidenceLevel.High, false, [], null, []);
        var status = HttpStatusCode.NotFound;
        using var http = new HttpClient(new Respond(() => new HttpResponseMessage(status)));
        var provider = new ElectronBuilderUpdateProvider(http);
        assert(provider.CanHandle(application), "The installed GitHub updater metadata was not discovered.");
        var vanished = await provider.CheckAsync(application, CancellationToken.None);
        assert(vanished.Status == UpdateStatus.Unsupported && vanished.ExecutionPlan is null &&
               vanished.Message?.Contains("HTTP 404") == true && vanished.Message.Contains("ddp-kyocera/upgrade-sync"),
            "A vanished updater feed was reported as a failed check instead of an unavailable update source.");
        status = HttpStatusCode.ServiceUnavailable;
        try
        {
            await new ElectronBuilderUpdateProvider(http).CheckAsync(application, CancellationToken.None);
            assert(false, "A temporary feed outage was not reported as a failure.");
        }
        catch (HttpRequestException) { assert(true, "A temporary feed outage remains a failure."); }
    }

    private sealed class Respond(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response());
    }
}
