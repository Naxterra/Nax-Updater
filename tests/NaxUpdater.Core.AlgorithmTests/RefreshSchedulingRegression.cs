using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

internal static class RefreshSchedulingRegression
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        var storeApp = new InstalledApplication("msix:Fixture_family", "Fixture", "Fixture", "1.0", "1.0", "fixture",
            null, null, null, null, InstallScope.CurrentUser, ManagementMode.Msix, ConfidenceLevel.High, false, [], null, []);
        var conventional = new Source("conventional", [ManagementMode.Registry]);
        var store = new Source("store", [ManagementMode.Msix]);
        var service = new UpdateCheckService([conventional, store]);
        var checkedStore = await service.CheckAsync(new(DateTimeOffset.UtcNow, [storeApp], [], []));
        assert(conventional.Refreshes == 0 && store.Refreshes == 1 && checkedStore.AllCurrent,
            "Checking one Store package unnecessarily refreshed an unrelated conventional catalog.");
        var blockedApp = storeApp with { BlockedProviders = ["store"] };
        await service.CheckAsync(new(DateTimeOffset.UtcNow, [blockedApp], [], []));
        assert(store.Refreshes == 1 && conventional.Refreshes == 0,
            "A source blocked for every selected application was still refreshed.");
        await service.CheckAsync(new(DateTimeOffset.UtcNow, [storeApp with { IsSystemComponent = true }], [], []));
        assert(store.Refreshes == 1 && conventional.Refreshes == 0,
            "An empty visible inventory still refreshed catalogs.");
        var registryApp = storeApp with { Identity = "registry:fixture", ManagementMode = ManagementMode.Registry };
        var checkedRegistry = await service.CheckAsync(new(DateTimeOffset.UtcNow, [registryApp], [], []));
        assert(conventional.Refreshes == 1 && store.Refreshes == 1 && checkedRegistry.AllCurrent,
            "Skipping irrelevant sources also skipped the catalog required by an eligible application.");
    }

    private sealed class Source(string id, IReadOnlyList<ManagementMode> modes) : IUpdateProvider, IUpdateProviderSourceRefresher
    {
        public int Refreshes;
        public string Id => id;
        public UpdateProviderDescriptor Descriptor { get; } = new(UpdateProviderAuthority.FallbackCatalog, 100, "fixture", modes);
        public bool CanHandle(InstalledApplication app) => modes.Contains(app.ManagementMode);
        public Task RefreshSourceAsync(CancellationToken token) { Interlocked.Increment(ref Refreshes); return Task.CompletedTask; }
        public Task<UpdateCheckResult> CheckAsync(InstalledApplication app, CancellationToken token) => Task.FromResult(
            new UpdateCheckResult(app.Identity, app.DisplayName, app.NormalizedVersion, null, UpdateStatus.Current,
                id, id, "neutral", "fixture", "x64", "stable", null, null, null));
    }
}
