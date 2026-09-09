using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

internal static class RecoveryRegression
{
    public static void Run(Action<bool, string> assert)
    {
        const string family = "Fixture.Store_publisher";
        var time = DateTimeOffset.UtcNow;
        var legacy = new UpdateOperationRecord(Guid.NewGuid(), "msix:" + family, "msix:fixture.store_publisher", "Fixture", "msix-store",
            "26.901.5280.0", null, "fixture", UpdateTransactionStage.FailedNeedsAttention, time, time, "0x80073D02");
        var current = new UpdateCheckResult(legacy.ApplicationIdentity, "Fixture", "26.901.6511.0", null, UpdateStatus.Current,
            "openai-codex-store", "Microsoft Store", "neutral", "fixture", "x64", "stable", null, null, null,
            CorrelationKey: legacy.CorrelationKey);
        assert(UpdateRecoveryVerifier.HasReachedTarget(legacy, current.InstalledVersion, current),
            "An independently verified version increase did not recover the legacy null-target Store operation.");
        var typed = legacy with { ExecutionKind = UpdateExecutionKind.NativeStoreQueue, StoreQueueTarget = new("Product", family, legacy.InstalledVersion!) };
        assert(UpdateRecoveryVerifier.HasReachedTarget(typed, current.InstalledVersion, current), "Typed Store queue recovery failed.");
        assert(!UpdateRecoveryVerifier.HasReachedTarget(typed with { StoreQueueTarget = null }, current.InstalledVersion, current), "Incomplete queue metadata was accepted.");
        assert(!UpdateRecoveryVerifier.HasReachedTarget(legacy, legacy.InstalledVersion, current with { InstalledVersion = legacy.InstalledVersion }),
            "An unchanged version was falsely treated as completed servicing without completion evidence.");
        assert(!UpdateRecoveryVerifier.HasReachedTarget(legacy, "26.901.4000.0", current with { InstalledVersion = "26.901.4000.0" }), "A downgrade recovered a queue operation.");
        assert(!UpdateRecoveryVerifier.HasReachedTarget(legacy, current.InstalledVersion, current with { ApplicationIdentity = "msix:Wrong_family" }), "A different package recovered the operation.");
        assert(!UpdateRecoveryVerifier.HasReachedTarget(legacy, current.InstalledVersion, current with { CorrelationKey = null }), "Missing correlation evidence recovered the operation.");
        foreach (var status in new[] { UpdateStatus.StoreQueued, UpdateStatus.Error, UpdateStatus.ManagedExternally })
            assert(!UpdateRecoveryVerifier.HasReachedTarget(legacy, current.InstalledVersion, current with { Status = status }), "A pending or unchecked Store result recovered the operation.");
        assert(!UpdateRecoveryVerifier.HasReachedTarget(legacy, current.InstalledVersion, current with {
            SourceChecks = [new("msix-store", "Store", UpdateStatus.Error, null, "failed")] }), "A hidden source failure recovered the operation.");
        assert(!UpdateRecoveryVerifier.HasReachedTarget(legacy with { ProviderId = "unknown-native" }, current.InstalledVersion, current), "Arbitrary null-target commands used Store recovery.");
        assert(UpdateRecoveryVerifier.HasReachedTarget(legacy with { TargetVersion = "26.901.6000.0" }, current.InstalledVersion), "Normal version-bound recovery regressed.");
    }
}
