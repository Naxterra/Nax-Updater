using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

public static class UpdateRecoveryVerifier
{
    public static bool HasReachedTarget(UpdateOperationRecord operation, string? observedVersion, UpdateCheckResult? assessment = null,
        Func<IReadOnlyDictionary<string, ChocolateyPackageDetails>>? chocolateyRecords = null)
    {
        if (string.IsNullOrWhiteSpace(observedVersion)) return false;
        if (!string.IsNullOrWhiteSpace(operation.TargetVersion))
        {
            if (VersionOrder.Compare(observedVersion, operation.TargetVersion) < 0) return false;
            // A same-version repair (Chocolatey reinstalling a version installed outside
            // it) already matched its target before it ran, so the application version
            // proves nothing. Only Chocolatey's own records can, for the package it ran
            // and, on a wrapper route, the payload package.
            var repairOnly = !string.IsNullOrWhiteSpace(operation.InstalledVersion) &&
                             VersionOrder.Compare(operation.TargetVersion, operation.InstalledVersion) <= 0;
            if (!repairOnly) return true;
            if (operation.ChocolateyTarget is not { } chocolatey) return false;
            var records = (chocolateyRecords ?? (static () => ChocolateyDependencyPlanner.ReadInstalled(ChocolateyPackageService.InstallRoot())))();
            return ChocolateyPackageService.RecordMismatch(records, chocolatey.PackageId, chocolatey.Version) is null &&
                   (chocolatey.PayloadPackageId is null || chocolatey.PayloadVersion is null ||
                    ChocolateyPackageService.RecordMismatch(records, chocolatey.PayloadPackageId, chocolatey.PayloadVersion) is null);
        }

        // 0.17.0 journals had no execution kind for versionless Store queue
        // operations. Support only that identifiable legacy case, not arbitrary
        // unversioned commands or a same-version servicing assumption.
        var queueOperation = operation.ExecutionKind == UpdateExecutionKind.NativeStoreQueue ||
            operation.ExecutionKind is null && (operation.ProviderId is "msix-store");
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
