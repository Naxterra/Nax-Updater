using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

public sealed class UpdateCheckService
{
    private readonly IReadOnlyList<IUpdateProvider> _providers;
    private readonly SemaphoreSlim _checkSlots = new(16, 16);

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
            new IvpnUpdateProvider(httpClient),
            new NodeJsUpdateProvider(httpClient),
            new WinRarUpdateProvider(httpClient)
        };
        providers.AddRange(catalog.GitHub.Select(recipe => new GitHubReleaseUpdateProvider(httpClient, recipe)));
        providers.Add(new MsixStoreUpdateProvider(httpClient));
        // Registration order is not authoritative. Explicit descriptors below arbitrate
        // installed protocols, producer sources, Store, and fallback catalogs.
        providers.Add(new WingetFallbackUpdateProvider());
        _providers = providers;
    }

    public async Task<UpdateCheckSnapshot> CheckAsync(
        InventorySnapshot inventory,
        CancellationToken cancellationToken = default)
    {
        var generationId = Guid.NewGuid();
        var checkedAt = DateTimeOffset.UtcNow;
        var checks = new List<Task<UpdateCheckResult>>();
        var externalResults = new List<UpdateCheckResult>();

        foreach (var refresher in _providers.OfType<IUpdateProviderSourceRefresher>())
        {
            await refresher.RefreshSourceAsync(cancellationToken);
        }

        foreach (var application in inventory.Applications.Where(static application => !application.IsSystemComponent))
        {
            // Native-updater ownership is an explicit application policy. The application
            // remains the update authority instead of being replaced by a third-party source.
            if (application.ManagementMode == ManagementMode.NativeSelfUpdater)
            {
                var updateOwner = application.Evidence.FirstOrDefault(static evidence =>
                    evidence.Label == ExternalManagementClassifier.OwnerEvidenceLabel)?.Value ??
                    application.Evidence.FirstOrDefault(static evidence =>
                        evidence.Label == "Preferred update provider")?.Value ??
                    "Application native updater";
                var updateSource = application.Evidence.FirstOrDefault(static evidence =>
                    evidence.Label == ExternalManagementClassifier.SourceEvidenceLabel)?.Value;
                externalResults.Add(new UpdateCheckResult(
                    application.Identity,
                    application.DisplayName,
                    application.NormalizedVersion,
                    null,
                    UpdateStatus.ManagedExternally,
                    "native-updater",
                    updateOwner,
                    "application-managed",
                    "Preserved by the application's updater",
                    "application-managed",
                    "native",
                    Uri.TryCreate(updateSource, UriKind.Absolute, out var sourceUri) ? sourceUri.AbsoluteUri : null,
                    $"Update ownership belongs to {updateOwner}; NaxUpdater will not replace that mechanism with a fallback catalog.",
                    null,
                    UpdateProviderAuthority.ExplicitApplicationPolicy,
                    "The installed application policy assigns update ownership to its native updater",
                    ["native-updater"],
                    UpdateApplicability.Unknown));
                continue;
            }

            var preferredProviderId = PreferredProviderId(application);
            IUpdateProvider? preferredProvider = null;
            if (preferredProviderId is not null)
            {
                preferredProvider = _providers.FirstOrDefault(provider =>
                    provider.Id.Equals(preferredProviderId, StringComparison.OrdinalIgnoreCase));
                if (preferredProvider is null ||
                    !SupportsManagementMode(preferredProvider, application.ManagementMode) ||
                    IsBlocked(application, preferredProvider.Id) ||
                    !preferredProvider.CanHandle(application))
                {
                    externalResults.Add(PreferredProviderUnavailableResult(application, preferredProviderId));
                    continue;
                }
            }
            var candidates = preferredProvider is null
                ? ResolveHighestAuthorityCandidates(application)
                : [preferredProvider];
            var provider = preferredProvider ?? candidates.FirstOrDefault();
            if (provider is not null)
            {
                var equallyRanked = candidates
                    .Where(candidate => candidate.Descriptor.Authority == provider.Descriptor.Authority &&
                                        candidate.Descriptor.Specificity == provider.Descriptor.Specificity)
                    .ToArray();
                if (preferredProvider is null && equallyRanked.Length > 1)
                {
                    externalResults.Add(AmbiguousProviderResult(application, equallyRanked));
                    continue;
                }
                checks.Add(SafeCheckAsync(
                    provider,
                    application,
                    candidates.Select(static candidate => candidate.Id).ToArray(),
                    preferredProvider is not null,
                    generationId,
                    checkedAt,
                    cancellationToken));
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
            checkedAt,
            results,
            unsupportedCount,
            generationId);
    }

    private async Task<UpdateCheckResult> SafeCheckAsync(
        IUpdateProvider provider,
        InstalledApplication application,
        IReadOnlyList<string> candidateProviderIds,
        bool selectedByPolicy,
        Guid generationId,
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken)
    {
        await _checkSlots.WaitAsync(cancellationToken);
        try
        {
            return await SafeCheckCoreAsync(
                provider,
                application,
                candidateProviderIds,
                selectedByPolicy,
                generationId,
                checkedAt,
                cancellationToken);
        }
        finally
        {
            _checkSlots.Release();
        }
    }

    private static async Task<UpdateCheckResult> SafeCheckCoreAsync(
        IUpdateProvider provider,
        InstalledApplication application,
        IReadOnlyList<string> candidateProviderIds,
        bool selectedByPolicy,
        Guid generationId,
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await provider.CheckAsync(application, cancellationToken);
            if (!result.ApplicationIdentity.Equals(application.Identity, StringComparison.Ordinal) ||
                !provider.OwnsResultProviderId(result.ProviderId))
            {
                return ProviderContractError(
                    provider,
                    application,
                    candidateProviderIds,
                    "The provider returned an application or provider identity outside its registered claim.");
            }
            if (result.ExecutionPlan is not null &&
                (result.Status != UpdateStatus.Available || result.Applicability == UpdateApplicability.NotApplicable))
            {
                return ProviderContractError(
                    provider,
                    application,
                    candidateProviderIds,
                    "The provider attached an execution plan to a status or applicability state that is not installable.");
            }
            var hasApplicablePlan = result.ExecutionPlan is not null &&
                                    !string.IsNullOrWhiteSpace(result.AvailableVersion);
            var normalizedStatus = result.Status == UpdateStatus.Available && !hasApplicablePlan
                ? UpdateStatus.NewerReleaseKnown
                : result.Status;
            var applicability = normalizedStatus switch
            {
                UpdateStatus.Available when result.ExecutionPlan is not null => UpdateApplicability.Applicable,
                UpdateStatus.Current => UpdateApplicability.NotRequired,
                UpdateStatus.NewerReleaseKnown => result.Applicability == UpdateApplicability.Applicable
                    ? UpdateApplicability.Unknown
                    : result.Applicability,
                _ => result.Applicability
            };
            var plan = normalizedStatus != UpdateStatus.Available || result.ExecutionPlan is null
                ? null
                : result.ExecutionPlan with
                {
                    CreatedAt = checkedAt,
                    ExpiresAt = checkedAt + TimeSpan.FromMinutes(15),
                    InstalledVersionPrecondition = application.NormalizedVersion,
                    CheckGenerationId = generationId,
                    RunningExecutablePaths = BindRunningExecutablePaths(application, result.ExecutionPlan)
                };
            var boundResult = result with
            {
                DisplayName = application.DisplayName,
                InstalledVersion = application.NormalizedVersion,
                Status = normalizedStatus,
                ExecutionPlan = plan,
                ProviderAuthority = selectedByPolicy
                    ? UpdateProviderAuthority.ExplicitApplicationPolicy
                    : provider.Descriptor.Authority,
                ProviderSelectionReason = selectedByPolicy
                    ? $"Explicit application policy selected {provider.Id}"
                    : provider.Descriptor.SelectionReason,
                CandidateProviderIds = candidateProviderIds,
                Applicability = applicability,
                CorrelationKey = UpdateCorrelation.ForApplication(application)
            };
            if (boundResult.Status == UpdateStatus.Current && boundResult.AvailableVersion is not null &&
                VersionOrder.Compare(boundResult.AvailableVersion, boundResult.InstalledVersion) < 0)
                boundResult = boundResult with { AvailableVersion = null };
            if (boundResult.ExecutionPlan is not null && boundResult.Status != UpdateStatus.Available)
            {
                return ProviderContractError(
                    provider,
                    application,
                    candidateProviderIds,
                    $"The provider attached an execution plan to non-applicable status {boundResult.Status}.");
            }
            if (boundResult.ExecutionPlan is not null)
            {
                var validationError = UpdatePlanValidator.Validate(boundResult, checkedAt);
                if (validationError is not null)
                {
                    return ProviderContractError(provider, application, candidateProviderIds, validationError);
                }
            }
            return boundResult;
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
                null,
                selectedByPolicy
                    ? UpdateProviderAuthority.ExplicitApplicationPolicy
                    : provider.Descriptor.Authority,
                selectedByPolicy
                    ? $"Explicit application policy selected {provider.Id}"
                    : provider.Descriptor.SelectionReason,
                candidateProviderIds);
        }
    }

    private static UpdateCheckResult AmbiguousProviderResult(
        InstalledApplication application,
        IReadOnlyList<IUpdateProvider> providers)
    {
        var providerIds = providers.Select(static provider => provider.Id).Order(StringComparer.Ordinal).ToArray();
        return new UpdateCheckResult(
            application.Identity,
            application.DisplayName,
            application.NormalizedVersion,
            null,
            UpdateStatus.Error,
            "provider-arbitration",
            "Ambiguous update authority",
            "unknown",
            "No provider selected",
            "unknown",
            "unknown",
            null,
            $"Multiple equally authoritative providers claimed this application: {string.Join(", ", providerIds)}.",
            null,
            providers[0].Descriptor.Authority,
            "The provider claims were equally authoritative and specific; installation is blocked until policy resolves them",
            providerIds);
    }

    private static UpdateCheckResult PreferredProviderUnavailableResult(
        InstalledApplication application,
        string preferredProviderId) => new(
        application.Identity,
        application.DisplayName,
        application.NormalizedVersion,
        null,
        UpdateStatus.Error,
        "provider-policy",
        "Preferred update provider unavailable",
        "unknown",
        "Application policy",
        "unknown",
        "unknown",
        null,
        $"Application policy requires {preferredProviderId}, but that provider did not claim the installed application. Fallback is blocked.",
        null,
        UpdateProviderAuthority.ExplicitApplicationPolicy,
        $"Explicit application policy requires {preferredProviderId}",
        [preferredProviderId],
        UpdateApplicability.Unknown);

    private static UpdateCheckResult ProviderContractError(
        IUpdateProvider provider,
        InstalledApplication application,
        IReadOnlyList<string> candidateProviderIds,
        string message) => new(
        application.Identity,
        application.DisplayName,
        application.NormalizedVersion,
        null,
        UpdateStatus.Error,
        provider.Id,
        provider.Id,
        "unknown",
        "Provider contract validation",
        "unknown",
        "unknown",
        null,
        message,
        null,
        provider.Descriptor.Authority,
        provider.Descriptor.SelectionReason,
        candidateProviderIds,
        UpdateApplicability.Unknown);

    private static bool IsBlocked(InstalledApplication application, string providerId) =>
        application.BlockedProviders.Any(blocked =>
            blocked.Equals(providerId, StringComparison.OrdinalIgnoreCase) ||
            providerId.Equals("winget-fallback", StringComparison.OrdinalIgnoreCase) &&
            blocked.Equals("WinGet fallback", StringComparison.OrdinalIgnoreCase));

    private IUpdateProvider[] ResolveHighestAuthorityCandidates(InstalledApplication application)
    {
        foreach (var authorityGroup in _providers
                     .Where(provider => SupportsManagementMode(provider, application.ManagementMode) &&
                                        !IsBlocked(application, provider.Id))
                     .GroupBy(static provider => provider.Descriptor.Authority)
                     .OrderByDescending(static group => group.Key))
        {
            var candidates = authorityGroup
                .Where(provider => provider.CanHandle(application))
                .OrderByDescending(static provider => provider.Descriptor.Specificity)
                .ThenBy(static provider => provider.Id, StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length > 0)
            {
                return candidates;
            }
        }
        return [];
    }

    private static bool SupportsManagementMode(IUpdateProvider provider, ManagementMode managementMode) =>
        provider.Descriptor.SupportedManagementModes is not { Count: > 0 } supported ||
        supported.Contains(managementMode);

    private static string? PreferredProviderId(InstalledApplication application)
    {
        var preferred = application.Evidence.FirstOrDefault(static evidence =>
            evidence.Label == "Preferred update provider")?.Value;
        return preferred switch
        {
            "Zero Install native feed" => "zero-install",
            "Official Nextcloud GitHub release and signed MSI" => "github:nextcloud-releases/desktop",
            "Blizzard native updater" => "native-updater",
            "Brave native update channel" => "native-updater",
            _ => preferred
        };
    }

    private static IReadOnlyList<string> BindRunningExecutablePaths(
        InstalledApplication application,
        UpdateExecutionPlan plan)
    {
        if (plan.RunningExecutablePaths is { Count: > 0 })
        {
            return plan.RunningExecutablePaths;
        }
        if (plan.RunningProcessNames.Count == 0 ||
            string.IsNullOrWhiteSpace(application.PrimaryInstallPath) ||
            !Path.GetExtension(application.PrimaryInstallPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }
        try
        {
            return [Path.GetFullPath(application.PrimaryInstallPath)];
        }
        catch
        {
            return [];
        }
    }
}

public static class UpdateCorrelation
{
    public static string ForApplication(InstalledApplication application)
    {
        var upgradeFamily = application.Evidence.FirstOrDefault(static evidence =>
            evidence.Label == "Windows Installer upgrade family" && evidence.Verified)?.Value;
        if (!string.IsNullOrWhiteSpace(upgradeFamily))
        {
            return $"msi-upgrade:{upgradeFamily.Trim().ToUpperInvariant()}";
        }
        if (application.Identity.StartsWith("msix:", StringComparison.OrdinalIgnoreCase))
        {
            return application.Identity.ToLowerInvariant();
        }
        if (!string.IsNullOrWhiteSpace(application.PrimaryInstallPath))
        {
            try
            {
                return $"path:{Path.GetFullPath(application.PrimaryInstallPath).ToUpperInvariant()}";
            }
            catch
            {
                // Fall back to the inventory identity when path normalization fails.
            }
        }
        return $"identity:{application.Identity}";
    }
}
