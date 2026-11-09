using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

internal static class RetryChecksRegression
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        await DerivedStatusesAreRecheckedAsync(assert);
        await ExpiredOffersAreRecheckedAsync(assert);
        await CompanionChecksUseTheFullInventoryAsync(assert);
    }

    private static async Task ExpiredOffersAreRecheckedAsync(Action<bool, string> assert)
    {
        var apps = new[] { App("expired"), App("fresh"), App("current"), App("failed") };
        var calls = new List<string>();
        var provider = new Provider("fixture", _ => true, app =>
        {
            lock (calls) calls.Add(app.Identity);
            return Result(app, UpdateStatus.Current, "fixture");
        });
        var now = DateTimeOffset.UtcNow;
        UpdateCheckResult Offer(InstalledApplication app, DateTimeOffset expires) => Result(app, UpdateStatus.Available, "fixture") with
        {
            AvailableVersion = "2.0",
            CorrelationKey = UpdateCorrelation.ForApplication(app),
            ExecutionPlan = new(UpdateExecutionKind.DownloadedExe, new Uri("https://fixture.test/setup.exe"),
                "setup.exe", new string('A', 64), null, null, [], false, ["fixture.test"], [],
                RequireAuthenticode: false, CreatedAt: now.AddMinutes(-20), ExpiresAt: expires,
                InstalledVersionPrecondition: "1.0", CheckGenerationId: Guid.NewGuid())
        };
        var fresh = Offer(apps[1], now.AddMinutes(10));
        var previous = new UpdateCheckSnapshot(now.AddMinutes(-20),
            [Offer(apps[0], now.AddMinutes(-5)), fresh, Result(apps[2], UpdateStatus.Current, "fixture"),
                Result(apps[3], UpdateStatus.Error, "fixture")], 0);
        var retried = await new UpdateCheckService([provider]).RetryFailedAsync(new(now, apps, [], []), previous);
        assert(calls.Order().SequenceEqual(new[] { "expired", "failed" }),
            "Retry preserved an expired offer or repeated a fresh, independent successful assessment.");
        assert(retried.Results.Single(r => r.ApplicationIdentity == "expired").Status == UpdateStatus.Current,
            "Retry kept an obsolete update button after the source reported the app current.");
        assert(ReferenceEquals(retried.Results.Single(r => r.ApplicationIdentity == "fresh"), fresh),
            "Retry modified or renewed a still-valid plan without checking its source.");
    }

    private static async Task DerivedStatusesAreRecheckedAsync(Action<bool, string> assert)
    {
        var firefox = App("firefox") with { NormalizedVersion = "2.0", InstalledVersion = "2.0" };
        var maintenance = App("maintenance") with { DisplayName = "Mozilla Maintenance Service", NormalizedVersion = "2.0" };
        var older = App("older") with { DisplayName = "Runtime - 1.0" };
        var newer = App("newer") with { DisplayName = "Runtime - 2.0", NormalizedVersion = "2.0" };
        var calls = new List<string>();
        Provider Source(string id, string identity, UpdateStatus status) => new(id, app => app.Identity == identity, app =>
        {
            lock (calls) calls.Add(app.Identity);
            return Result(app, status, id) with { AvailableVersion = status == UpdateStatus.NewerReleaseKnown ? "2.0" : null };
        });
        var previous = new UpdateCheckSnapshot(DateTimeOffset.UtcNow,
            [Result(firefox, UpdateStatus.Current, "mozilla-firefox") with { InstalledVersion = "1.0" },
                Result(maintenance, UpdateStatus.Current, "mozilla-maintenance"),
                Result(older, UpdateStatus.Current, "superseded-registration"),
                Result(newer, UpdateStatus.Error, "fixture")], 0);
        var service = new UpdateCheckService([
            Source("mozilla-firefox", "firefox", UpdateStatus.Error),
            Source("native-updater", "maintenance", UpdateStatus.ManagedExternally),
            Source("older-source", "older", UpdateStatus.NewerReleaseKnown),
            Source("newer-source", "newer", UpdateStatus.Error)]);
        var retried = await service.RetryFailedAsync(new(DateTimeOffset.UtcNow, [firefox, maintenance, older, newer], [], []), previous);
        assert(calls.Count == 4, "Retry reused a derived status without checking the evidence it depends on.");
        assert(retried.Results.Single(r => r.ApplicationIdentity == "maintenance").Status == UpdateStatus.ManagedExternally,
            "Maintenance was reported current even though the Firefox check failed.");
        assert(retried.Results.Single(r => r.ApplicationIdentity == "older").Status == UpdateStatus.NewerReleaseKnown,
            "An old runtime registration stayed current after its newer sibling could no longer be verified.");
    }

    private static async Task CompanionChecksUseTheFullInventoryAsync(Action<bool, string> assert)
    {
        var olderFirefox = App("firefox-old");
        var newerFirefox = App("firefox-new") with { NormalizedVersion = "3.0" };
        var maintenance = App("maintenance") with { DisplayName = "Mozilla Maintenance Service", NormalizedVersion = "2.0" };
        var calls = new List<string>();
        var firefoxProvider = new Provider("mozilla-firefox", app => app.Identity.StartsWith("firefox"), app =>
        {
            lock (calls) calls.Add(app.Identity);
            return Result(app, UpdateStatus.Current, "mozilla-firefox");
        });
        var maintenanceProvider = new Provider("native-updater", app => app.Identity == "maintenance",
            app => Result(app, UpdateStatus.ManagedExternally, "native-updater"));
        var previous = new UpdateCheckSnapshot(DateTimeOffset.UtcNow,
            [Result(olderFirefox, UpdateStatus.Error, "mozilla-firefox"),
                Result(newerFirefox, UpdateStatus.Current, "mozilla-firefox"),
                Result(maintenance, UpdateStatus.Error, "native-updater")], 0);
        var result = await new UpdateCheckService([firefoxProvider, maintenanceProvider]).RetryFailedAsync(
            new(DateTimeOffset.UtcNow, [olderFirefox, newerFirefox, maintenance], [], []), previous);
        assert(calls.SequenceEqual(["firefox-old"]), "Retry repeated a successful Firefox check.");
        assert(result.Results.Single(r => r.ApplicationIdentity == "maintenance").Status == UpdateStatus.ManagedExternally,
            "Retry marked maintenance current using only the retried Firefox and ignored the newer retained installation.");
    }

    private static InstalledApplication App(string id) => new(id, id, "Fixture", "1.0", "1.0", "fixture",
        null, null, null, null, InstallScope.CurrentUser, ManagementMode.Registry, ConfidenceLevel.High, false, [], null, []);
    private static UpdateCheckResult Result(InstalledApplication app, UpdateStatus status, string provider) => new(app.Identity,
        app.DisplayName, app.NormalizedVersion, null, status, provider, provider, "neutral", "fixture", "x64", "stable", null, null, null);
    private sealed class Provider(string id, Func<InstalledApplication, bool> accepts,
        Func<InstalledApplication, UpdateCheckResult> check) : IUpdateProvider
    {
        public string Id => id;
        public UpdateProviderDescriptor Descriptor { get; } = new(UpdateProviderAuthority.ProducerRelease, 100, "fixture");
        public bool CanHandle(InstalledApplication app) => accepts(app);
        public Task<UpdateCheckResult> CheckAsync(InstalledApplication app, CancellationToken token) => Task.FromResult(check(app));
    }
}
