using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

// The update history keeps every finished outcome next to the recovery journal, which holds
// only the latest transaction. Each scenario is one method.
internal static class HistoryRegression
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        await every_finished_update_is_kept_newest_first(assert);
        history_keeps_only_the_newest_entries(assert);
        damaged_history_file_is_kept_aside(assert);
    }

    private static async Task every_finished_update_is_kept_newest_first(Action<bool, string> assert)
    {
        var folder = Folder();
        try
        {
            var history = new FileUpdateHistory(Path.Combine(folder, "update-history.json"));
            var journal = new FileUpdateOperationJournal(Path.Combine(folder, "update-operation.json"));
            var offer = await Offer();
            var updated = await new UpdateTransactionCoordinator(new Backend(offer, offer with { InstalledVersion = "2.0.0" }, new(3010, true, null)),
                journal, verificationAttempts: 1, history: history).ApplyAsync(offer, "unused");
            var failed = await new UpdateTransactionCoordinator(new Backend(offer, offer, new(1603, false, "The installer failed.")),
                journal, verificationAttempts: 1, history: history).ApplyAsync(offer, "unused");
            var entries = history.ReadNewestFirst();
            assert(updated.IsSuccess && !failed.IsSuccess && entries.Count == 2 &&
                   entries[0] is { Stage: UpdateTransactionStage.FailedNeedsAttention, ExitCode: 1603, TargetVersion: "2.0.0" } &&
                   entries[1] is { Stage: UpdateTransactionStage.Succeeded, ExitCode: 3010, RestartRecommended: true, InstalledVersion: "1.0.0", ObservedVersion: "2.0.0" } &&
                   journal.ReadLatest()?.Stage == UpdateTransactionStage.FailedNeedsAttention,
                $"The history did not keep both outcomes with their versions and exit codes: {string.Join(" | ", entries.Select(static entry => $"{entry.Stage} {entry.ExitCode} {entry.ObservedVersion}"))}");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void history_keeps_only_the_newest_entries(Action<bool, string> assert)
    {
        var folder = Folder();
        try
        {
            var history = new FileUpdateHistory(Path.Combine(folder, "update-history.json"), capacity: 3);
            for (var index = 1; index <= 5; index++) history.Append(Entry($"App {index}"));
            var names = history.ReadNewestFirst().Select(static entry => entry.DisplayName).ToArray();
            assert(names.SequenceEqual(["App 5", "App 4", "App 3"]), $"The capped history did not keep the newest entries: {string.Join(", ", names)}");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void damaged_history_file_is_kept_aside(Action<bool, string> assert)
    {
        var folder = Folder();
        try
        {
            var path = Path.Combine(folder, "update-history.json");
            File.WriteAllText(path, "{ not json");
            var history = new FileUpdateHistory(path);
            var unreadableBefore = history.ReadNewestFirst();
            history.Append(Entry("After the damage"));
            var keptAside = Directory.GetFiles(folder, "update-history.json.unreadable-*");
            assert(unreadableBefore.Count == 0 && keptAside.Length == 1 && File.ReadAllText(keptAside[0]) == "{ not json" &&
                   history.ReadNewestFirst().Single().DisplayName == "After the damage",
                "A damaged history file was overwritten instead of being kept aside, or the new entry was lost.");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static string Folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "nax-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static UpdateHistoryEntry Entry(string name) => new(Guid.NewGuid(), name, "registry:fixture", "official:Vendor.App", "1.0.0", "2.0.0",
        "2.0.0", UpdateTransactionStage.Succeeded, 0, false, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    // A bound offer produced by the real check pipeline, so the coordinator's validation applies.
    private static async Task<UpdateCheckResult> Offer()
    {
        var app = new InstalledApplication("registry:fixture", "Fixture App", "Vendor", "1.0.0", "1.0.0", "fixture",
            null, "fixture", null, null, InstallScope.Machine, ManagementMode.Registry, ConfidenceLevel.High, false, [], null, []);
        return (await new UpdateCheckService([new Provider()]).CheckAsync(new(DateTimeOffset.UtcNow, [app], [], []))).Results.Single();
    }

    private sealed class Provider : IUpdateProvider
    {
        public string Id => "native-fixture";
        public UpdateProviderDescriptor Descriptor { get; } = new(UpdateProviderAuthority.ProducerRelease, 50, "fixture");
        public bool CanHandle(InstalledApplication application) => true;
        public Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken token) => Task.FromResult(new UpdateCheckResult(
            application.Identity, application.DisplayName, application.NormalizedVersion, "2.0.0", UpdateStatus.Available, Id, Id, "neutral", "fixture",
            "x64", "stable", null, "fixture",
            new UpdateExecutionPlan(UpdateExecutionKind.NativeCommand, null, null, null, "Fixture", @"C:\Fixture\updater.exe", [], false, [], [])));
    }

    private sealed class Backend(UpdateCheckResult before, UpdateCheckResult after, UpdateExecutionResult execution) : IUpdateTransactionBackend
    {
        private bool _applied;
        public Task<UpdateCheckResult?> RevalidateAsync(UpdateCheckResult previous, CancellationToken token) =>
            Task.FromResult<UpdateCheckResult?>(_applied ? after : before);
        public Task<PreparedUpdateExecution> PrepareAsync(UpdateCheckResult update, string cacheRoot, IProgress<double>? progress, CancellationToken token) =>
            Task.FromResult(new PreparedUpdateExecution(null, null, null, null, null));
        public Task<ApplicationCloseResult> QuiesceAsync(UpdateCheckResult update, CancellationToken token) =>
            Task.FromResult(new ApplicationCloseResult(true, false, []));
        public Task DiscardPreparedAsync(PreparedUpdateExecution prepared) => Task.CompletedTask;
        public Task<UpdateExecutionResult> ApplyAsync(UpdateCheckResult update, PreparedUpdateExecution prepared, CancellationToken token)
        {
            _applied = true;
            return Task.FromResult(execution);
        }
    }
}
