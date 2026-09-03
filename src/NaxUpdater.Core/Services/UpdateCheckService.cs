using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

public sealed class UpdateCheckService
{
    private readonly IReadOnlyList<IUpdateProvider> _providers;

    public UpdateCheckService(IReadOnlyList<IUpdateProvider> providers)
    {
        _providers = providers;
    }

    public UpdateCheckService(HttpClient httpClient, UpdateProviderCatalog catalog, FirefoxMetadataDetector? firefoxMetadataDetector = null)
    {
        var providers = new List<IUpdateProvider>
        {
            new FirefoxUpdateProvider(httpClient, firefoxMetadataDetector ?? new FirefoxMetadataDetector()),
            new ZeroInstallUpdateProvider(new ProcessQueryRunner()),
            new ElectronBuilderUpdateProvider(httpClient),
            new GogGalaxyUpdateProvider(),
            new WinRarUpdateProvider(httpClient)
        };
        providers.AddRange(catalog.GitHub.Select(recipe => new GitHubReleaseUpdateProvider(httpClient, recipe)));
        providers.Add(new MsixStoreUpdateProvider(httpClient));
        // WinGet is deliberately last: it supplies coverage only when no installed or
        // producer-owned provider claims the application.
        providers.Add(new WingetFallbackUpdateProvider(httpClient));
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
            // Native-updater ownership is an explicit application policy. The application
            // remains the update authority instead of being replaced by a third-party source.
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

            var provider = _providers.FirstOrDefault(candidate => candidate.CanHandle(application));
            if (provider is not null)
            {
                supportedIdentities.Add(application.Identity);
                checks.Add(SafeCheckAsync(provider, application, cancellationToken));
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
