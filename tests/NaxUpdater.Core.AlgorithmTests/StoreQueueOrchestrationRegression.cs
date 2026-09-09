using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;
using Windows.ApplicationModel.Store.Preview.InstallControl;

internal static class StoreQueueOrchestrationRegression
{
    private const string Family = "Fixture.Queue_publisher";
    public static async Task RunAsync(Action<bool, string> assert)
    {
        var app = new InstalledApplication("msix:" + Family, "Queue fixture", "Fixture", "1.0.0.0", "1.0.0.0", "fixture",
            null, "fixture", null, null, InstallScope.CurrentUser, ManagementMode.Msix, ConfidenceLevel.High, false, [], null, []);
        foreach (var installedAfter in new[] { "2.0.0.0", "1.0.0.0", "0.9.0.0" })
        {
            var client = new QueueClient();
            var service = new NativeStoreUpdateService(client, (_, _) => throw new InvalidOperationException("Queue operations do not invent a catalog target."),
                (_, _) => Task.CompletedTask);
            using var http = new HttpClient(new NoNetwork());
            var provider = new MsixStoreUpdateProvider(http, new NoStoreUpdate(), service);
            async Task<UpdateCheckResult?> Assess(UpdateCheckResult? prior, CancellationToken token)
            {
                if (client.State == AppInstallState.Completed)
                    return prior! with { InstalledVersion = installedAfter, AvailableVersion = null, Status = UpdateStatus.Current, ExecutionPlan = null };
                return (await new UpdateCheckService([provider]).CheckAsync(new(DateTimeOffset.UtcNow, [app], [], []), token)).Results.Single();
            }
            var approved = (await Assess(null, CancellationToken.None))!;
            assert(approved.IsInstallable && approved.ExecutionPlan?.ProcessPolicy == UpdateProcessPolicy.PlatformManaged,
                "Queue action was not executable without closing the application.");
            assert(UpdatePlanValidator.Validate(approved, DateTimeOffset.UtcNow) is null && client.Restarts == 0,
                "Scan or plan validation mutated the Store queue.");
            var backend = new DefaultUpdateTransactionBackend(new UpdateExecutionService(nativeStoreService: service),
                new UpdatePackageDownloader(http, new NativeAuthenticodeVerifier()), (p, t) => Assess(p, t));
            var result = await new UpdateTransactionCoordinator(backend, verificationAttempts: 1).ApplyAsync(approved, "unused");
            assert(result.IsSuccess == (installedAfter != "0.9.0.0"),
                "Queue completion did not require independently observed non-downgraded installed state.");
            assert(client.Restarts == 1 && client.NewInstalls == 0,
                "Queue execution started a new installation or restarted more than once.");
            assert(result.FreshAssessment?.InstalledVersion == installedAfter, "Queue completion lost the installed-version evidence.");
            assert(UpdatePlanValidator.Validate(approved with { ExecutionPlan = approved.ExecutionPlan! with {
                StoreQueueTarget = new("Wrong", Family, "1.0.0.0") } }, DateTimeOffset.UtcNow) is not null,
                "A substituted Store product passed queue-action validation.");
        }
        var active = new QueueClient { State = AppInstallState.Downloading };
        var activeService = new NativeStoreUpdateService(active, (_, _) => throw new InvalidOperationException(), (_, _) => Task.CompletedTask);
        var prepared = await activeService.PrepareQueueAsync(new("Fixture.Product", Family, "1.0.0.0"), CancellationToken.None);
        active.Advance = true;
        var progress = new List<double>();
        var finished = await prepared.Apply(new InlineProgress(progress.Add), CancellationToken.None);
        assert(finished.IsSuccess && active.Restarts == 0 && active.NewInstalls == 0 && progress.Count > 0,
            "Tracking an active queue item restarted it or lost progress.");
        var blocked = new QueueClient { MayAffectOtherItems = true };
        var blockedService = new NativeStoreUpdateService(blocked, (_, _) => throw new InvalidOperationException());
        var blockedPrepared = await blockedService.PrepareQueueAsync(new("Fixture.Product", Family, "1.0.0.0"), CancellationToken.None);
        assert(!(await blockedPrepared.Apply(null, CancellationToken.None)).IsSuccess && blocked.Restarts == 0,
            "Restarting one queue item affected other unapproved packages.");
        var conflict = new QueueClient { State = AppInstallState.Error, ErrorCode = NativeStoreUpdateService.PackageIdentityConflictCode };
        var conflictService = new NativeStoreUpdateService(conflict, (_, _) => throw new InvalidOperationException());
        var conflictPrepared = await conflictService.PrepareQueueAsync(new("Fixture.Product", Family, "1.0.0.0"), CancellationToken.None);
        var conflictResult = await conflictPrepared.Apply(null, CancellationToken.None);
        assert(!conflictResult.IsSuccess && conflictResult.ExitCode == NativeStoreUpdateService.PackageIdentityConflictCode && conflict.Restarts == 0,
            "An unchanged Store package identity conflict was retried or reported successful.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { await blockedPrepared.Apply(null, cancellation.Token); assert(false, "A canceled action ran."); }
        catch (OperationCanceledException) { assert(blocked.Restarts == 0, "Cancellation changed the Store queue."); }

        var ensuRecipe = new GitHubUpdateRecipe { Id = "Ente.Ensu", DisplayName = "Ensu", PublisherContains = "ente",
            Repository = "ente/ente", ReleaseTagPrefix = "ensu-v", AssetNamePattern = @"^Ensu_(?<version>[0-9.]+)_x64-setup\.exe$",
            Architecture = "x64", ExpectedSigner = "ENTE TECHNOLOGIES, INC.", InstallerKind = UpdateExecutionKind.DownloadedExe };
        using var releases = new HttpClient(new Releases());
        var ensu = await new GitHubReleaseUpdateProvider(releases, ensuRecipe).CheckAsync(app with {
            DisplayName = "Ensu", Publisher = "ente", NormalizedVersion = "0.1.19", InstalledVersion = "0.1.19" }, CancellationToken.None);
        assert(ensu.Status == UpdateStatus.Current && ensu.AvailableVersion == "0.1.19",
            "The Ensu adapter selected another monorepo product or a prerelease.");
    }
    private sealed class QueueClient : INativeStoreUpdateClient, INativeStoreUpdateItem
    {
        public AppInstallState State = AppInstallState.ReadyToDownload;
        public bool Advance;
        public int Restarts;
        public int NewInstalls;
        public int ErrorCode;
        private int polls;
        public bool MayAffectOtherItems { get; init; }
        public string ProductId => "Fixture.Product";
        public string PackageFamilyName => Family;
        public Task<NativeStoreQueueEntry?> ReadQueueAsync(string family, CancellationToken token) => Task.FromResult<NativeStoreQueueEntry?>(
            new(ProductId, Family, State, ErrorCode, MayAffectOtherItems));
        public Task<INativeStoreUpdateItem?> GetQueueItemAsync(string family, CancellationToken token) => Task.FromResult<INativeStoreUpdateItem?>(this);
        public Task<INativeStoreUpdateItem?> FindPausedUpdateAsync(StoreProductIdentity p, CancellationToken t) => throw new InvalidOperationException("Queue item already exists.");
        public Task<INativeStoreUpdateItem?> StartUpdateAsync(PublishedStorePackage p, CancellationToken t)
        { NewInstalls++; throw new InvalidOperationException("A duplicate install was attempted."); }
        public void Restart() { Restarts++; Advance = true; State = AppInstallState.Downloading; }
        public NativeStoreItemState Status()
        {
            if (Advance) State = ++polls < 3 ? AppInstallState.Downloading : AppInstallState.Completed;
            return new(State, ErrorCode, Math.Min(1, polls / 3d));
        }
    }
    private sealed class InlineProgress(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken t) => throw new InvalidOperationException("Queued operations must not query external catalogs.");
    }
    private sealed class Releases : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken t) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = new StringContent("""
            [{"tag_name":"photos-v99.0.0","draft":false,"prerelease":false,"assets":[]},
             {"tag_name":"ensu-v0.2.0","draft":false,"prerelease":true,"assets":[]},
             {"tag_name":"ensu-v0.1.19","draft":false,"prerelease":false,"assets":[{"name":"Ensu_0.1.19_x64-setup.exe","browser_download_url":"https://github.com/ente/ente/releases/download/ensu-v0.1.19/Ensu_0.1.19_x64-setup.exe"}]}]
            """) });
    }
}
