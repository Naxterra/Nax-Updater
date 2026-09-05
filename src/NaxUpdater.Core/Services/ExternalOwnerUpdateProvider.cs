using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

internal sealed class ExternalOwnerUpdateProvider : IUpdateProvider
{
    public string Id => "native-updater";
    public UpdateProviderDescriptor Descriptor { get; } = new(UpdateProviderAuthority.Unverified, 0,
        "Update owner identified; this alone is not a version check");
    public bool CanHandle(InstalledApplication app) => app.ManagementMode == ManagementMode.NativeSelfUpdater ||
        app.Evidence.Any(e => e.Label == ExternalManagementClassifier.OwnerEvidenceLabel);
    public Task<UpdateCheckResult> CheckAsync(InstalledApplication app, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var owner = app.Evidence.FirstOrDefault(e => e.Label == ExternalManagementClassifier.OwnerEvidenceLabel)?.Value ??
            app.Evidence.FirstOrDefault(e => e.Label == "Preferred update provider")?.Value ?? "Application updater";
        var source = app.Evidence.FirstOrDefault(e => e.Label == ExternalManagementClassifier.SourceEvidenceLabel)?.Value;
        return Task.FromResult(new UpdateCheckResult(app.Identity, app.DisplayName, app.NormalizedVersion, null,
            UpdateStatus.ManagedExternally, Id, owner, "application-managed", "Preserved by installed updater",
            "application-managed", "native", source,
            $"The installed update owner is {owner}. No compatible version-check protocol is implemented for this owner; current status is unknown.",
            null, Applicability: UpdateApplicability.Unknown));
    }
}
