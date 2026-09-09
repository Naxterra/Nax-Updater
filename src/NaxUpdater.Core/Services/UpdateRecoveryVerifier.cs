using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

public static class UpdateRecoveryVerifier
{
    public static bool HasReachedTarget(UpdateOperationRecord operation, string? observedVersion, UpdateCheckResult? assessment = null)
    {
        if (string.IsNullOrWhiteSpace(observedVersion)) return false;
        if (!string.IsNullOrWhiteSpace(operation.TargetVersion))
            return VersionOrder.Compare(observedVersion, operation.TargetVersion) >= 0;

        // 0.17.0 journals had no execution kind for versionless Store queue
        // operations. Support only that identifiable legacy case, not arbitrary
        // unversioned commands or a same-version servicing assumption.
        var queueOperation = operation.ExecutionKind == UpdateExecutionKind.NativeStoreQueue ||
            operation.ExecutionKind is null && (operation.ProviderId is "msix-store" or "openai-codex-store");
        if (operation.ExecutionKind == UpdateExecutionKind.NativeStoreQueue && operation.StoreQueueTarget is null) return false;
        if (!queueOperation || !operation.ApplicationIdentity.StartsWith("msix:", StringComparison.OrdinalIgnoreCase) ||
            !Version.TryParse(operation.InstalledVersion, out var baseline) || !Version.TryParse(observedVersion, out var installed) ||
            installed <= baseline || assessment is null ||
            assessment.Status is not (UpdateStatus.Current or UpdateStatus.Available) ||
            assessment.SourceChecks?.Any(s => s.Status is UpdateStatus.Error or UpdateStatus.StoreQueued) == true ||
            !assessment.ApplicationIdentity.Equals(operation.ApplicationIdentity, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(operation.CorrelationKey) ||
            !operation.CorrelationKey.Equals(assessment.CorrelationKey, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(assessment.InstalledVersion, observedVersion, StringComparison.OrdinalIgnoreCase)) return false;
        if (operation.StoreQueueTarget is { } target &&
            (!operation.ApplicationIdentity.Equals("msix:" + target.PackageFamilyName, StringComparison.OrdinalIgnoreCase) ||
             target.InstalledVersion != operation.InstalledVersion || string.IsNullOrWhiteSpace(target.ProductId))) return false;
        return true;
    }
}
