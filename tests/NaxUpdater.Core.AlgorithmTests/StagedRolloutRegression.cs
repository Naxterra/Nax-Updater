using System.Net;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

internal static class StagedRolloutRegression
{
    // CurseForge's real updater ID; its log reads "Staging percentage: 0.25,
    // percentage: 0.9036619034371483, user id: a4324395-0b5c-512f-b483-9119e75662f1".
    private const string CurseForgeId = "a4324395-0b5c-512f-b483-9119e75662f1";

    public static async Task RunAsync(Action<bool, string> assert, string fixture)
    {
        var root = Directory.CreateDirectory(Path.Combine(fixture, "staged-rollout"));
        var appData = Directory.CreateDirectory(Path.Combine(root.FullName, "AppData")).FullName;
        var install = Directory.CreateDirectory(Path.Combine(root.FullName, "Feed App Windows"));
        var resources = Directory.CreateDirectory(Path.Combine(install.FullName, "resources"));
        var executable = Path.Combine(install.FullName, "Feed App.exe");
        File.WriteAllBytes(executable, []);
        File.WriteAllText(Path.Combine(resources.FullName, "app-update.yml"),
            "provider: generic\nurl: https://feed.example/\npublisherName:\n  - Vendor Ltd\n");
        // An installation without version resources: userData is named after its install folder.
        var userData = Directory.CreateDirectory(Path.Combine(appData, "Feed App Windows")).FullName;
        void Id(string? id)
        {
            var path = Path.Combine(userData, ".updaterId");
            if (id is null) File.Delete(path); else File.WriteAllText(path, id);
        }

        var idFile = Path.Combine(root.FullName, ".updaterId");
        File.WriteAllText(idFile, CurseForgeId);
        var position = ElectronStagedRollout.Position(idFile);
        assert(position is { } p && Math.Abs(p - 0.9036619034371483) < 1e-12 &&
               !ElectronStagedRollout.Includes(25, p) && !ElectronStagedRollout.Includes(90, p) && ElectronStagedRollout.Includes(91, p) &&
               ElectronStagedRollout.ThresholdPercent(p) == 91,
            "The rollout position differs from electron-updater's own computation for CurseForge's updater ID.");
        File.WriteAllText(idFile, "not-a-uuid");
        assert(ElectronStagedRollout.Position(idFile) is null, "An invalid updater ID was given a rollout position.");
        assert(ElectronStagedRollout.Percentage("version: 1\nstagingPercentage: 25\n") == 25 &&
               ElectronStagedRollout.Percentage("stagingPercentage: '40'\n") == 40 && ElectronStagedRollout.Percentage("version: 1\n") is null,
            "The feed's staging percentage was misread.");

        var feed = "files:\n  - url: https://feed.example/1.3.0-7/setup.exe\n    sha512: " +
                   Convert.ToBase64String(Enumerable.Repeat((byte)0xab, 64).ToArray()) + "\nversion: 1.3.0-7\nstagingPercentage: 25\n";
        using var http = new HttpClient(new Respond(() => feed));
        var app = new InstalledApplication("fixture:feed", "Feed App 1.2.0", "Vendor", "1.2.0", "1.2.0", "fixture", executable, "fixture",
            null, null, InstallScope.CurrentUser, ManagementMode.Registry, ConfidenceLevel.High, false, [], null, []);
        var recipe = new OfficialSiteRecipe
        {
            Id = "Vendor.Feed", DisplayNamePattern = "^Feed App", VersionUrl = "https://feed.example/latest.yml",
            VersionPattern = @"(?ms)^\s*-\s*url:\s*(?<url>https://\S+)\s+sha512:\s*(?<hash>\S+).*?^version:\s*(?<version>[0-9]+(?:[.-][0-9]+)+)\s*$",
            DownloadUrl = "{url}", HashAlgorithm = "sha512", HashEncoding = "base64", RolloutPattern = @"(?m)^stagingPercentage:\s*(?<percent>[0-9]+)",
            ExpectedSigner = "Vendor Ltd", AllowedHosts = ["feed.example"], Architecture = "neutral", InstallerKind = UpdateExecutionKind.DownloadedExe,
            InstallerArguments = ["/S"], CurrentUserInstallerArguments = ["/S"]
        };
        IUpdateProvider[] providers = [new OfficialSiteUpdateProvider(http, recipe, appData), new ElectronBuilderUpdateProvider(http, appData)];
        foreach (var provider in providers)
        {
            Id(null);
            var unknown = await provider.CheckAsync(app, CancellationToken.None);
            assert(unknown is { Status: UpdateStatus.NewerReleaseKnown, ExecutionPlan: null, AvailabilityReason: UpdateAvailabilityReason.StagedRollout } &&
                   unknown.Message?.Contains("has not stored its rollout ID") == true && unknown.RolloutThresholdPercent is null,
                $"{provider.Id}: a staged release was offered to an installation without an updater ID.");
            Id(CurseForgeId);
            var outside = await provider.CheckAsync(app, CancellationToken.None);
            assert(outside is { Status: UpdateStatus.NewerReleaseKnown, ExecutionPlan: null, AvailabilityReason: UpdateAvailabilityReason.StagedRollout } &&
                   outside.Message?.Contains("90.4 %") == true && outside.Message.Contains("reaches 91 %") && outside.RolloutThresholdPercent == 91,
                $"{provider.Id}: a staged release was offered to an installation outside the rollout.");
            Id("00000000-0000-4000-8000-000010000000");
            var inside = await provider.CheckAsync(app, CancellationToken.None);
            assert(inside.Status == UpdateStatus.Available && inside.ExecutionPlan is not null && inside.AvailableVersion == "1.3.0-7",
                $"{provider.Id}: an installation inside the rollout was not offered the release its own updater offers.");
        }

        // A held rollout settles the application: a lower catalog with the same
        // release must not deliver it early.
        Id(CurseForgeId);
        var held = await new UpdateCheckService([new OfficialSiteUpdateProvider(http, recipe, appData), new Catalog()])
            .CheckAsync(new(DateTimeOffset.UtcNow, [app], [], []));
        var result = held.Results.Single();
        assert(!result.IsInstallable && result.AvailabilityReason == UpdateAvailabilityReason.StagedRollout && held.InstallableUpdateCount == 0 &&
               result.SourceChecks?.Any(c => c.ProviderId == "fixture-catalog" && c.Status == UpdateStatus.Available) == true &&
               result.SourceChecks.Any(c => c.AvailabilityReason == UpdateAvailabilityReason.StagedRollout && c.RolloutThresholdPercent == 91),
            "A fallback catalog delivered a release the publisher is still withholding from this installation.");
    }

    private sealed class Catalog : IUpdateProvider
    {
        public string Id => "fixture-catalog";
        public UpdateProviderDescriptor Descriptor { get; } = new(UpdateProviderAuthority.FallbackCatalog, 50, "fixture");
        public bool CanHandle(InstalledApplication app) => true;
        public Task<UpdateCheckResult> CheckAsync(InstalledApplication app, CancellationToken token) => Task.FromResult(new UpdateCheckResult(
            app.Identity, app.DisplayName, app.NormalizedVersion, "1.3.0-7", UpdateStatus.Available, Id, Id, "neutral", "fixture", "x64", "stable",
            null, null, new(UpdateExecutionKind.NativeCommand, null, null, null, "Fixture", @"C:\Fixture\updater.exe", [], false, [], [])));
    }

    private sealed class Respond(Func<string> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body()) });
    }
}
