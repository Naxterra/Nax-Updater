using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

public sealed class UpdateCheckService
{
    private readonly IReadOnlyList<IUpdateProvider> _providers;

    public UpdateCheckService(HttpClient httpClient, UpdateProviderCatalog catalog, FirefoxMetadataDetector? firefoxMetadataDetector = null)
    {
        var providers = new List<IUpdateProvider>
        {
            new FirefoxUpdateProvider(httpClient, firefoxMetadataDetector ?? new FirefoxMetadataDetector()),
            new ZeroInstallUpdateProvider(new ProcessQueryRunner()),
            new ElectronBuilderUpdateProvider(httpClient)
        };
        providers.AddRange(catalog.GitHub.Select(recipe => new GitHubReleaseUpdateProvider(httpClient, recipe)));
        providers.Add(new MsixStoreUpdateProvider(httpClient));
        providers.Add(new FederatedCatalogUpdateProvider(httpClient));
        _providers = providers;
    }

    public async Task<UpdateCheckSnapshot> CheckAsync(
        InventorySnapshot inventory,
        CancellationToken cancellationToken = default)
    {
        var checks = new List<Task<UpdateCheckResult>>();
        var supportedIdentities = new HashSet<string>(StringComparer.Ordinal);
        var externalResults = new List<UpdateCheckResult>();

        foreach (var application in inventory.Applications.Where(static application => !application.IsSystemComponent))
        {
            var provider = _providers.FirstOrDefault(candidate => candidate.CanHandle(application));
            if (provider is not null)
            {
                supportedIdentities.Add(application.Identity);
                checks.Add(SafeCheckAsync(provider, application, cancellationToken));
                continue;
            }
            if (application.ManagementMode == ManagementMode.NativeSelfUpdater)
            {
                supportedIdentities.Add(application.Identity);
                externalResults.Add(new UpdateCheckResult(
                    application.Identity,
                    application.DisplayName,
                    application.NormalizedVersion,
                    null,
                    UpdateStatus.ManagedExternally,
                    "native-updater",
                    "Application native updater",
                    "application-managed",
                    "Preserved by the application's updater",
                    "application-managed",
                    "native",
                    null,
                    "NaxUpdater will not replace this application's own update mechanism.",
                    null));
                continue;
            }
            externalResults.Add(new UpdateCheckResult(
                application.Identity,
                application.DisplayName,
                application.NormalizedVersion,
                null,
                UpdateStatus.Unsupported,
                "unverified",
                "No verifiable update source",
                "unknown",
                "No verified update source discovered",
                "unknown",
                "unknown",
                null,
                "The application was inventoried, but no unambiguous catalog identity or installed updater protocol could be verified.",
                null));
        }

        var checkedResults = await Task.WhenAll(checks);
        var results = checkedResults
            .Concat(externalResults)
            .OrderBy(static result => result.Status == UpdateStatus.Available ? 0 : result.Status == UpdateStatus.Error ? 1 : 2)
            .ThenBy(static result => result.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var unsupportedCount = results.Count(static result => result.Status == UpdateStatus.Unsupported);
        return new UpdateCheckSnapshot(
            DateTimeOffset.Now,
            results,
            unsupportedCount);
    }

    private static async Task<UpdateCheckResult> SafeCheckAsync(
        IUpdateProvider provider,
        InstalledApplication application,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.CheckAsync(application, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new UpdateCheckResult(
                application.Identity,
                application.DisplayName,
                application.NormalizedVersion,
                null,
                UpdateStatus.Error,
                provider.Id,
                provider.Id,
                "unknown",
                "Check failed before language could be verified",
                "unknown",
                "unknown",
                null,
                exception.Message,
                null);
        }
    }
}
