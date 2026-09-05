using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;
using System.Net;
using System.Text;

var application = CreateApplication();
var msiFamilyEvidence = new ApplicationEvidence(
    EvidenceKind.Registry,
    "Windows Installer upgrade family",
    "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}",
    true);
var oldMsi = application with { Identity = "registry:old-product", Evidence = [msiFamilyEvidence] };
var newMsi = application with { Identity = "registry:new-product", Evidence = [msiFamilyEvidence] };
Assert(UpdateCorrelation.ForApplication(oldMsi) == UpdateCorrelation.ForApplication(newMsi),
    "MSI major-upgrade ProductCode changes did not retain a stable transaction correlation identity.");
var installedProvider = new StubProvider(
    "installed-protocol",
    new(UpdateProviderAuthority.InstalledUpdateProtocol, 100, "installed fixture"),
    Result(application, "installed-protocol", UpdateStatus.Current));
var fallbackProvider = new StubProvider(
    "winget-fallback",
    new(UpdateProviderAuthority.FallbackCatalog, 100, "fallback fixture"),
    Result(application, "winget-fallback", UpdateStatus.NewerReleaseKnown, "2.0.0"));
var authority = await new UpdateCheckService([fallbackProvider, installedProvider])
    .CheckAsync(new(DateTimeOffset.UtcNow, [application], [], []));
Assert(authority.Results.Single() is
{
    ProviderId: "installed-protocol",
    ProviderAuthority: UpdateProviderAuthority.InstalledUpdateProtocol
}, "Provider authority still depends on registration order.");

var msixApplication = application with
{
    Identity = "msix:Fixture.Package_123",
    ManagementMode = ManagementMode.Msix
};
var desktopProducer = new StubProvider(
    "desktop-producer",
    new(
        UpdateProviderAuthority.ProducerRelease,
        100,
        "desktop fixture",
        [ManagementMode.Registry, ManagementMode.WindowsInstaller]),
    Result(msixApplication, "desktop-producer", UpdateStatus.Current));
var platformStore = new StubProvider(
    "platform-store",
    new(
        UpdateProviderAuthority.PlatformStore,
        100,
        "MSIX fixture",
        [ManagementMode.Msix]),
    Result(msixApplication, "platform-store", UpdateStatus.Current));
var msixAuthority = await new UpdateCheckService([desktopProducer, platformStore])
    .CheckAsync(new(DateTimeOffset.UtcNow, [msixApplication], [], []));
Assert(msixAuthority.Results.Single().ProviderId == "platform-store",
    "An MSIX application was routed to a higher-ranked desktop installer provider.");

var blocked = await new UpdateCheckService([fallbackProvider])
    .CheckAsync(new(
        DateTimeOffset.UtcNow,
        [application with { BlockedProviders = ["winget-fallback"] }],
        [],
        []));
Assert(blocked.Results.Single().Status == UpdateStatus.Unsupported,
    "A blocked fallback provider was selected.");

var preferredMissing = await new UpdateCheckService([fallbackProvider])
    .CheckAsync(new(
        DateTimeOffset.UtcNow,
        [application with
        {
            Evidence = [new ApplicationEvidence(
                EvidenceKind.Policy,
                "Preferred update provider",
                "missing-provider",
                true)]
        }],
        [],
        []));
Assert(preferredMissing.Results.Single() is { Status: UpdateStatus.Error, ProviderId: "provider-policy" },
    "An unavailable preferred provider silently fell through to a fallback source.");

var tied = await new UpdateCheckService([
        installedProvider,
        new StubProvider(
            "installed-protocol-2",
            installedProvider.Descriptor,
            Result(application, "installed-protocol-2", UpdateStatus.Current))
    ])
    .CheckAsync(new(DateTimeOffset.UtcNow, [application], [], []));
Assert(tied.Results.Single() is { Status: UpdateStatus.Error, ProviderId: "provider-arbitration" },
    "Equally authoritative providers did not fail closed.");

var maliciousProvider = new RawStubProvider(
    "malicious-provider",
    new(UpdateProviderAuthority.ProducerRelease, 100, "malicious fixture"),
    Result(application, "malicious-provider", UpdateStatus.Current) with
    {
        ApplicationIdentity = "other-app"
    });
var malicious = await new UpdateCheckService([maliciousProvider])
    .CheckAsync(new(DateTimeOffset.UtcNow, [application], [], []));
Assert(malicious.Results.Single() is { Status: UpdateStatus.Error, ExecutionPlan: null },
    "A provider attributed another application's assessment to the selected inventory item.");

var invalidDownloadPlan = new UpdateExecutionPlan(
    UpdateExecutionKind.DownloadedExe,
    new Uri("https://downloads.example.test/fixture.exe"),
    "fixture.exe",
    null,
    "Fixture Publisher",
    null,
    [],
    false,
    ["downloads.example.test"],
    []);
var invalidPlanProvider = new StubProvider(
    "invalid-plan",
    new(UpdateProviderAuthority.ProducerRelease, 100, "invalid plan fixture"),
    Result(application, "invalid-plan", UpdateStatus.Available, "2.0.0", invalidDownloadPlan));
var invalidPlan = await new UpdateCheckService([invalidPlanProvider])
    .CheckAsync(new(DateTimeOffset.UtcNow, [application], [], []));
Assert(invalidPlan.Results.Single() is { Status: UpdateStatus.Error, ExecutionPlan: null },
    "An invalid provider plan reached the UI as an installable update.");
var elevatedExeProvider = new StubProvider(
    "elevated-exe",
    new(UpdateProviderAuthority.ProducerRelease, 100, "elevated EXE fixture"),
    Result(
        application,
        "elevated-exe",
        UpdateStatus.Available,
        "2.0.0",
        invalidDownloadPlan with
        {
            Sha256 = new string('a', 64),
            RequiresElevation = true
        }));
var elevatedExe = await new UpdateCheckService([elevatedExeProvider])
    .CheckAsync(new(DateTimeOffset.UtcNow, [application], [], []));
Assert(elevatedExe.Results.Single() is
{
    Status: UpdateStatus.Available,
    Applicability: UpdateApplicability.Applicable,
    ExecutionPlan:
    {
        Kind: UpdateExecutionKind.DownloadedExe,
        RequiresElevation: true
    }
}, "A producer-hashed and publisher-bound EXE lost its executable elevation plan.");
var contradictoryProvider = new StubProvider(
    "contradictory-plan",
    new(UpdateProviderAuthority.ProducerRelease, 100, "contradictory fixture"),
    Result(application, "contradictory-plan", UpdateStatus.Current, null, plan: invalidDownloadPlan));
var contradictory = await new UpdateCheckService([contradictoryProvider])
    .CheckAsync(new(DateTimeOffset.UtcNow, [application], [], []));
Assert(contradictory.Results.Single() is { Status: UpdateStatus.Error, ExecutionPlan: null },
    "A provider attached an execution plan to a Current result without being rejected.");

var createdAt = DateTimeOffset.UtcNow;
var plan = new UpdateExecutionPlan(
    UpdateExecutionKind.StorePackage,
    null,
    null,
    null,
    "Microsoft Store",
    null,
    [],
    false,
    [],
    ["Fixture"],
    StoreProductId: "Fixture.Product",
    StorePackageFamilyName: "Fixture.Package_123",
    CreatedAt: createdAt,
    ExpiresAt: createdAt + TimeSpan.FromMinutes(5),
    InstalledVersionPrecondition: "1.0.0",
    CheckGenerationId: Guid.NewGuid(),
    RunningExecutablePaths: [application.PrimaryInstallPath!]);
var offer = Result(application, "fixture-store", UpdateStatus.Available, "2.0.0", plan) with
{
    Applicability = UpdateApplicability.Applicable,
    CorrelationKey = "fixture-correlation"
};
var journalPath = Path.Combine(Path.GetTempPath(), $"naxupdater-transaction-{Guid.NewGuid():N}.json");
var journal = new FileUpdateOperationJournal(journalPath);
var journalOperation = journal.Begin(offer, createdAt);
journal.Record(journalOperation, UpdateTransactionStage.Applying, createdAt + TimeSpan.FromSeconds(1));
var reloadedJournal = new FileUpdateOperationJournal(journalPath).ReadIncomplete();
Assert(reloadedJournal is
{
    Stage: UpdateTransactionStage.Applying,
    ApplicationIdentity: "fixture-app",
    TargetVersion: "2.0.0"
} && reloadedJournal.ExecutionFingerprint == UpdateExecutionIntent.Fingerprint(plan),
    "An applying transaction could not be recovered from its durable operation journal.");
journal.Record(reloadedJournal!, UpdateTransactionStage.Indeterminate, createdAt + TimeSpan.FromSeconds(2));
var indeterminateJournal = new FileUpdateOperationJournal(journalPath).ReadIncomplete();
Assert(indeterminateJournal?.Stage == UpdateTransactionStage.Indeterminate,
    "An indeterminate transaction was cleared before a later recovery could verify it.");
journal.Record(indeterminateJournal!, UpdateTransactionStage.FailedNeedsAttention, createdAt + TimeSpan.FromSeconds(3));
Assert(new FileUpdateOperationJournal(journalPath).ReadIncomplete() is null,
    "A terminal failed-needs-attention transaction remained marked as incomplete.");
File.Delete(journalPath);
var leasePath = Path.Combine(Path.GetTempPath(), $"naxupdater-lease-{Guid.NewGuid():N}.lock");
var heldLeaseProvider = new FileUpdateTransactionLeaseProvider(leasePath);
using (heldLeaseProvider.TryAcquire() ?? throw new InvalidOperationException("Could not acquire fixture transaction lease."))
{
    var competingBackend = new ScriptedBackend([offer], new(0, true, null));
    var competing = await new UpdateTransactionCoordinator(
            competingBackend,
            leaseProvider: new FileUpdateTransactionLeaseProvider(leasePath))
        .ApplyAsync(offer, Path.GetTempPath());
    Assert(competing.Stage == UpdateTransactionStage.FailedBeforeChange && competingBackend.Calls.Count == 0,
        "Two NaxUpdater processes acquired the same update transaction concurrently.");
}
File.Delete(leasePath);
var installed = offer with
{
    InstalledVersion = "2.0.0",
    AvailableVersion = null,
    Status = UpdateStatus.Current,
    ExecutionPlan = null,
    Applicability = UpdateApplicability.NotRequired
};
var normalBackend = new ScriptedBackend([offer, offer, installed], new(0, true, null));
var normal = await new UpdateTransactionCoordinator(normalBackend).ApplyAsync(offer, Path.GetTempPath());
Assert(normal.Stage == UpdateTransactionStage.Succeeded &&
       normalBackend.Calls.SequenceEqual(["Revalidate", "Prepare", "Revalidate", "Quiesce", "Apply", "Revalidate"]),
    "Transaction order is not revalidate, prepare, quiesce, apply, verify.");

var staleBackend = new ScriptedBackend([null], new(0, true, null));
var stale = await new UpdateTransactionCoordinator(staleBackend).ApplyAsync(offer, Path.GetTempPath());
Assert(stale.Stage == UpdateTransactionStage.NoLongerApplicable &&
       staleBackend.Calls.SequenceEqual(["Revalidate"]),
    "A stale offer reached preparation or application.");
var expiredQueuedOffer = offer with
{
    ExecutionPlan = plan with { ExpiresAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1) }
};
var refreshedQueueBackend = new ScriptedBackend([offer, offer, installed], new(0, true, null));
var refreshedQueueResult = await new UpdateTransactionCoordinator(refreshedQueueBackend)
    .ApplyAsync(expiredQueuedOffer, Path.GetTempPath());
Assert(refreshedQueueResult.Stage == UpdateTransactionStage.Succeeded,
    "A late Update All entry failed solely because its original plan expired before safe revalidation.");
var preparationFailureBackend = new ScriptedBackend(
    [offer],
    new(0, true, null),
    new InvalidDataException("corrupt prepared archive"));
var preparationFailure = await new UpdateTransactionCoordinator(preparationFailureBackend)
    .ApplyAsync(offer, Path.GetTempPath());
Assert(preparationFailure.Stage == UpdateTransactionStage.FailedBeforeChange &&
       preparationFailureBackend.Calls.SequenceEqual(["Revalidate", "Prepare"]),
    "A preparation failure reached quiescence or application.");

foreach (var unapproved in new[]
         {
             offer with { Status = UpdateStatus.Current },
             offer with { Applicability = UpdateApplicability.NotApplicable },
             offer with { AvailableVersion = null },
             offer with { ExecutionPlan = plan with { CheckGenerationId = Guid.Empty } }
         })
{
    var unapprovedBackend = new ScriptedBackend([offer], new(0, true, null));
    var rejected = await new UpdateTransactionCoordinator(unapprovedBackend)
        .ApplyAsync(unapproved, Path.GetTempPath());
    Assert(rejected.Stage == UpdateTransactionStage.FailedBeforeChange && unapprovedBackend.Calls.Count == 0,
        "A caller-supplied result without a valid approved intent reached revalidation or application.");
}

UpdateExecutionPlan[] changedTrustPlans =
[
    plan with { StoreProductId = "Fixture.OtherProduct" },
    plan with { StorePackageFamilyName = "Fixture.OtherPackage_123" },
    plan with { RequiresElevation = true },
    plan with { RunningProcessNames = ["OtherProcess"] },
    plan with { RunningExecutablePaths = [@"C:\Other\Other.exe"] }
];
foreach (var changedPlan in changedTrustPlans)
{
    var changedOffer = offer with { ExecutionPlan = changedPlan };
    var changedBackend = new ScriptedBackend([changedOffer], new(0, true, null));
    var changed = await new UpdateTransactionCoordinator(changedBackend)
        .ApplyAsync(offer, Path.GetTempPath());
    Assert(changed.Stage == UpdateTransactionStage.NoLongerApplicable &&
           changedBackend.Calls.SequenceEqual(["Revalidate"]),
        "A same-version offer with changed execution trust fields was applied without renewed approval.");
}
var changedDuringPreparation = offer with
{
    ExecutionPlan = plan with { StoreProductId = "Fixture.ChangedDuringDownload" }
};
var changedDuringPreparationBackend = new ScriptedBackend(
    [offer, changedDuringPreparation],
    new(0, true, null));
var changedAfterPrepare = await new UpdateTransactionCoordinator(changedDuringPreparationBackend)
    .ApplyAsync(offer, Path.GetTempPath());
Assert(changedAfterPrepare.Stage == UpdateTransactionStage.NoLongerApplicable &&
       changedDuringPreparationBackend.Calls.SequenceEqual(["Revalidate", "Prepare", "Revalidate", "Discard"]),
    "An offer that changed during preparation was not discarded before quiescence.");
var installedStateDrift = offer with
{
    InstalledVersion = "1.5.0",
    ExecutionPlan = plan with { InstalledVersionPrecondition = "1.5.0" }
};
var installedStateDriftBackend = new ScriptedBackend([offer, installedStateDrift], new(0, true, null));
var drifted = await new UpdateTransactionCoordinator(installedStateDriftBackend)
    .ApplyAsync(offer, Path.GetTempPath());
Assert(drifted.Stage == UpdateTransactionStage.NoLongerApplicable &&
       installedStateDriftBackend.Calls.SequenceEqual(["Revalidate", "Prepare", "Revalidate", "Discard"]),
    "Installed-version drift during preparation did not invalidate the user's approval.");

var recoveredBackend = new ScriptedBackend([offer, offer, installed], new(1603, false, "launcher failed"));
var recovered = await new UpdateTransactionCoordinator(recoveredBackend).ApplyAsync(offer, Path.GetTempPath());
Assert(recovered.Stage == UpdateTransactionStage.Succeeded,
    "Independent verification did not recover an update that completed despite a launcher error.");

var unchangedBackend = new ScriptedBackend([offer, offer, offer], new(0, true, null));
var unchanged = await new UpdateTransactionCoordinator(unchangedBackend, verificationAttempts: 1)
    .ApplyAsync(offer, Path.GetTempPath());
Assert(unchanged.Stage == UpdateTransactionStage.Indeterminate,
    "Exit code zero was incorrectly accepted without the installed target version.");
var majorUpgradeBackend = new ScriptedBackend(
    [offer, offer, installed with { ApplicationIdentity = "other-app" }],
    new(0, true, null));
var majorUpgrade = await new UpdateTransactionCoordinator(majorUpgradeBackend, verificationAttempts: 1)
    .ApplyAsync(offer, Path.GetTempPath());
Assert(majorUpgrade.Stage == UpdateTransactionStage.Succeeded,
    "An MSI major upgrade with a new ProductCode identity did not satisfy its stable correlation postcondition.");
var wrongIdentityBackend = new ScriptedBackend(
    [offer, offer, installed with { ApplicationIdentity = "other-app", CorrelationKey = "other-correlation" }],
    new(0, true, null));
var wrongIdentity = await new UpdateTransactionCoordinator(wrongIdentityBackend, verificationAttempts: 1)
    .ApplyAsync(offer, Path.GetTempPath());
Assert(wrongIdentity.Stage == UpdateTransactionStage.Indeterminate,
    "An unrelated application's higher installed version satisfied the update postcondition.");
var providerAliasBackend = new ScriptedBackend(
    [offer, offer, installed with { ProviderId = "fixture-provider-alias" }],
    new(0, true, null));
var providerAlias = await new UpdateTransactionCoordinator(providerAliasBackend, verificationAttempts: 1)
    .ApplyAsync(offer, Path.GetTempPath());
Assert(providerAlias.Stage == UpdateTransactionStage.Succeeded,
    "A legitimate provider alias transition invalidated an identity-bound installed-version postcondition.");
var nvidiaOneBackend = new ScriptedBackend([offer, offer, offer], new(1, false, "driver launcher returned 1"));
var nvidiaOne = await new UpdateTransactionCoordinator(nvidiaOneBackend, verificationAttempts: 1)
    .ApplyAsync(offer, Path.GetTempPath());
Assert(nvidiaOne.Stage == UpdateTransactionStage.FailedNeedsAttention && !nvidiaOne.IsSuccess,
    "Driver exit code 1 was accepted as success without an installed-version change.");
var rebootBackend = new ScriptedBackend([offer, offer, offer], new(3010, true, null));
var reboot = await new UpdateTransactionCoordinator(rebootBackend, verificationAttempts: 1)
    .ApplyAsync(offer, Path.GetTempPath());
Assert(reboot.Stage == UpdateTransactionStage.PendingReboot &&
       reboot.RequiresRestart &&
       !reboot.IsSuccess,
    "A reboot-pending update was incorrectly counted as completed before post-restart verification.");
var uacCanceledBackend = new ScriptedBackend([offer, offer, offer], new(1223, false, "UAC canceled"));
var uacCanceled = await new UpdateTransactionCoordinator(uacCanceledBackend, verificationAttempts: 1)
    .ApplyAsync(offer, Path.GetTempPath());
Assert(uacCanceled.Stage == UpdateTransactionStage.CanceledBeforeChange && !uacCanceled.IsSuccess,
    "A canceled UAC prompt was reported as a failed or completed installation.");

using var manifestClient = new HttpClient(new StubHandler(_ => Json("""
{
  "schemaVersion": 1,
  "buildVersion": "2.0.0.0",
  "storeProductId": "9PLM9XGG6VKS",
  "packageIdentity": "OpenAI.Codex"
}
""")));
var chatGpt = CreateApplication() with
{
    Identity = "msix:OpenAI.Codex_2p2nqsd0c76g0",
    DisplayName = "ChatGPT",
    Publisher = "OpenAI",
    InstalledVersion = "1.0.0.0",
    NormalizedVersion = "1.0.0.0",
    ManagementMode = ManagementMode.Msix,
    PrimaryInstallPath = @"C:\Fixture\ChatGPT.exe"
};
var releaseOnly = await new MsixStoreUpdateProvider(
        manifestClient,
        new StubStore(new(true, false, "9PLM9XGG6VKS", null, null)))
    .CheckAsync(chatGpt, CancellationToken.None);
Assert(releaseOnly is
{
    Status: UpdateStatus.Current,
    AvailableVersion: null,
    Applicability: UpdateApplicability.NotRequired,
    ExecutionPlan: null
}, "Release evidence was promoted to an installable ChatGPT update without applicability.");
var releaseOnlyThroughSelection = await new UpdateCheckService([
        new MsixStoreUpdateProvider(
            manifestClient,
            new StubStore(new(true, false, "9PLM9XGG6VKS", null, null)))
    ])
    .CheckAsync(new(DateTimeOffset.UtcNow, [chatGpt], [], []));
Assert(releaseOnlyThroughSelection.Results.Single() is
{
    Status: UpdateStatus.Current,
    AvailableVersion: null,
    Applicability: UpdateApplicability.NotRequired,
    ExecutionPlan: null
}, "Provider selection erased ChatGPT's explicit not-applicable evidence.");
var storeApplicable = await new MsixStoreUpdateProvider(
        manifestClient,
        new StubStore(new(true, true, "9PLM9XGG6VKS", "2.0.0.0", null)))
    .CheckAsync(chatGpt, CancellationToken.None);
Assert(storeApplicable is
{
    Status: UpdateStatus.Available,
    Applicability: UpdateApplicability.Applicable,
    ExecutionPlan.Kind: UpdateExecutionKind.StorePackage
}, "Store applicability did not produce an exact Store transaction.");
var differingTargets = await new MsixStoreUpdateProvider(
        manifestClient,
        new StubStore(new(true, true, "9PLM9XGG6VKS", "1.5.0.0", null)))
    .CheckAsync(chatGpt, CancellationToken.None);
Assert(differingTargets is
{
    Status: UpdateStatus.Available,
    AvailableVersion: "1.5.0.0",
    ExecutionPlan.Kind: UpdateExecutionKind.StorePackage
}, "The Store route did not retain its own exact target when manifest and Store targets differed.");
var productMismatch = await new MsixStoreUpdateProvider(
        manifestClient,
        new StubStore(new(true, true, "Different.Product", "2.0.0.0", null)))
    .CheckAsync(chatGpt, CancellationToken.None);
Assert(productMismatch is { Status: UpdateStatus.Error, ExecutionPlan: null },
    "A Store product that conflicted with OpenAI's manifest identity was accepted.");

using var currentManifestClient = new HttpClient(new StubHandler(_ => Json("""
{
  "schemaVersion": 1,
  "buildVersion": "1.0.0.0",
  "storeProductId": "9PLM9XGG6VKS",
  "packageIdentity": "OpenAI.Codex"
}
""")));
var storeNewerThanManifest = await new MsixStoreUpdateProvider(
        currentManifestClient,
        new StubStore(new(true, true, "9PLM9XGG6VKS", "2.0.0.0", null)))
    .CheckAsync(chatGpt, CancellationToken.None);
Assert(storeNewerThanManifest is
{
    Status: UpdateStatus.Available,
    AvailableVersion: "2.0.0.0",
    ExecutionPlan.Kind: UpdateExecutionKind.StorePackage
}, "A current OpenAI manifest suppressed a genuine exact Store update.");

var genericStoreApp = chatGpt with
{
    Identity = "msix:Fixture.StorePackage_123",
    DisplayName = "Fixture Store App"
};
var targetlessStore = await new UpdateCheckService([
        new MsixStoreUpdateProvider(
            manifestClient,
            new StubStore(new(true, true, "Fixture.Product", null, null)))
    ])
    .CheckAsync(new(DateTimeOffset.UtcNow, [genericStoreApp], [], []));
Assert(targetlessStore.Results.Single() is
{
    Status: UpdateStatus.NewerReleaseKnown,
    ExecutionPlan: null,
    IsInstallable: false
}, "A Store offer without an exact target version exposed an enabled install action.");

Console.WriteLine("NaxUpdater deterministic transaction tests passed.");

static InstalledApplication CreateApplication() => new(
    "fixture-app", "Fixture App", "Fixture Publisher", "1.0.0", "1.0.0", "fixture",
    @"C:\Fixture\Fixture.exe", "fixture", null, null, InstallScope.CurrentUser,
    ManagementMode.WindowsInstaller, ConfidenceLevel.High, false, [], null, []);

static UpdateCheckResult Result(
    InstalledApplication app,
    string provider,
    UpdateStatus status,
    string? available = null,
    UpdateExecutionPlan? plan = null) => new(
    app.Identity, app.DisplayName, app.NormalizedVersion, available, status, provider, provider,
    "neutral", "fixture", "x64", "stable", null, null, plan,
    Applicability: status == UpdateStatus.Available && plan is not null
        ? UpdateApplicability.Applicable
        : UpdateApplicability.Unknown);

static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
{
    Content = new StringContent(json, Encoding.UTF8, "application/json")
};

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class StubProvider(
    string id,
    UpdateProviderDescriptor descriptor,
    UpdateCheckResult result) : IUpdateProvider
{
    public string Id { get; } = id;
    public UpdateProviderDescriptor Descriptor { get; } = descriptor;
    public bool CanHandle(InstalledApplication application) => true;
    public Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken cancellationToken) =>
        Task.FromResult(result with
        {
            ApplicationIdentity = application.Identity,
            DisplayName = application.DisplayName,
            InstalledVersion = application.NormalizedVersion
        });
}

sealed class RawStubProvider(
    string id,
    UpdateProviderDescriptor descriptor,
    UpdateCheckResult result) : IUpdateProvider
{
    public string Id { get; } = id;
    public UpdateProviderDescriptor Descriptor { get; } = descriptor;
    public bool CanHandle(InstalledApplication application) => true;
    public Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken cancellationToken) =>
        Task.FromResult(result);
}

sealed class ScriptedBackend(
    IEnumerable<UpdateCheckResult?> assessments,
    UpdateExecutionResult execution,
    Exception? preparationError = null) : IUpdateTransactionBackend
{
    private readonly Queue<UpdateCheckResult?> _assessments = new(assessments);
    public List<string> Calls { get; } = [];
    public Task<UpdateCheckResult?> RevalidateAsync(UpdateCheckResult previous, CancellationToken cancellationToken)
    {
        Calls.Add("Revalidate");
        return Task.FromResult(_assessments.Count == 0 ? null : _assessments.Dequeue());
    }
    public Task<PreparedUpdateExecution> PrepareAsync(
        UpdateCheckResult update, string cacheRoot, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Calls.Add("Prepare");
        if (preparationError is not null)
        {
            throw preparationError;
        }
        return Task.FromResult(new PreparedUpdateExecution(null, null, null, null, null));
    }
    public Task<ApplicationCloseResult> QuiesceAsync(UpdateCheckResult update, CancellationToken cancellationToken)
    {
        Calls.Add("Quiesce");
        return Task.FromResult(new ApplicationCloseResult(true, false, []));
    }
    public Task DiscardPreparedAsync(PreparedUpdateExecution prepared)
    {
        Calls.Add("Discard");
        return Task.CompletedTask;
    }
    public Task<UpdateExecutionResult> ApplyAsync(
        UpdateCheckResult update, PreparedUpdateExecution prepared, CancellationToken cancellationToken)
    {
        Calls.Add("Apply");
        return Task.FromResult(execution);
    }
}

sealed class StubStore(StoreUpdateAvailability availability) : IStorePackageDeploymentService
{
    public string? LastError => availability.Error;
    public Task<StoreUpdateAvailability> CheckForUpdateAsync(
        string packageFamilyName, string? installedDisplayName, string? installedPublisher,
        string? installedVersion, string? installedArchitecture, CancellationToken cancellationToken) =>
        Task.FromResult(availability);
    public Task<StoreCatalogIdentity?> ResolveAsync(
        string packageFamilyName, string? installedDisplayName, string? installedPublisher,
        CancellationToken cancellationToken) => Task.FromResult<StoreCatalogIdentity?>(null);
    public Task<UpdateExecutionResult> InstallOrUpdateAsync(
        string productId, string packageFamilyName, string? installedDisplayName,
        string? installedPublisher, string expectedVersion, CancellationToken cancellationToken) =>
        Task.FromResult(new UpdateExecutionResult(0, true, null));
}

sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(responder(request));
}
