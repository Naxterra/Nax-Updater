using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

public sealed class UpdateCheckService
{
    private readonly IReadOnlyList<IUpdateProvider> _providers;
    private readonly SemaphoreSlim _checkSlots = new(16, 16);
    private readonly TimeSpan _providerTimeout = TimeSpan.FromSeconds(45);
    private readonly TimeSpan _sourceTimeout = TimeSpan.FromSeconds(20);
    private readonly TimeSpan _sourceCheckTimeout = TimeSpan.FromSeconds(30);

    public UpdateCheckService(IReadOnlyList<IUpdateProvider> providers,
        TimeSpan? providerTimeout = null, TimeSpan? sourceTimeout = null, TimeSpan? sourceCheckTimeout = null)
    {
        _providers = providers;
        if (sourceCheckTimeout is not null) _sourceCheckTimeout = sourceCheckTimeout.Value > TimeSpan.Zero
            ? sourceCheckTimeout.Value : throw new ArgumentOutOfRangeException(nameof(sourceCheckTimeout));
        if (providerTimeout is not null) _providerTimeout = providerTimeout.Value > TimeSpan.Zero
            ? providerTimeout.Value : throw new ArgumentOutOfRangeException(nameof(providerTimeout));
        if (sourceTimeout is not null) _sourceTimeout = sourceTimeout.Value > TimeSpan.Zero
            ? sourceTimeout.Value : throw new ArgumentOutOfRangeException(nameof(sourceTimeout));
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
            new WslUpdateProvider(httpClient),
            new MacriumUpdateProvider(httpClient),
            new DriverComponentUpdateProvider(httpClient),
            new ExternalOwnerUpdateProvider(),
            new WinRarUpdateProvider(httpClient)
        };
        providers.AddRange(catalog.GitHub.Select(recipe => new GitHubReleaseUpdateProvider(httpClient, recipe)));
        providers.Add(new MsixStoreUpdateProvider(httpClient));
        // Registration order is not authoritative. Explicit descriptors below arbitrate
        // installed protocols, producer sources, Store, and fallback catalogs.
        providers.Add(new WingetFallbackUpdateProvider());
        _providers = providers;
    }

    public Task<UpdateCheckSnapshot> CheckAsync(
        InventorySnapshot inventory,
        CancellationToken cancellationToken = default,
        IProgress<UpdateCheckProgress>? progress = null) =>
        Task.Run(() => CheckCoreAsync(inventory, cancellationToken, progress), cancellationToken)
            .WaitAsync(cancellationToken);

    private async Task<UpdateCheckSnapshot> CheckCoreAsync(
        InventorySnapshot inventory, CancellationToken token, IProgress<UpdateCheckProgress>? progress)
    {
        var applications = inventory.Applications.Where(static app => !app.IsSystemComponent).ToArray();
        var generation = Guid.NewGuid();
        var refreshFailures = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var provider in _providers)
        {
            if (provider is not IUpdateProviderSourceRefresher refresher) continue;
            progress?.Report(new(0, applications.Length, "sources", provider.Id));
            using var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var refresh = Task.Run(() => refresher.RefreshSourceAsync(refreshCancellation.Token), refreshCancellation.Token);
            try
            {
                await refresh.WaitAsync(_sourceTimeout, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                refreshCancellation.Cancel();
                ObserveFailure(refresh);
                refreshFailures[provider.Id] = exception is TimeoutException
                    ? "Refreshing the provider catalog timed out. Retry the check."
                    : exception.Message;
            }
        }

        var checkedAt = DateTimeOffset.UtcNow;
        var completed = 0;
        progress?.Report(new(0, applications.Length, "checks", null));
        async Task<UpdateCheckResult> CheckOneAsync(InstalledApplication application)
        {
            await _checkSlots.WaitAsync(token);
            using var checkCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task<UpdateCheckResult>? task = null;
            try
            {
                task = Task.Run(() => AssessApplicationAsync(application, generation, checkedAt, refreshFailures,
                    checkCancellation.Token), checkCancellation.Token);
                return await task.WaitAsync(_providerTimeout, token);
            }
            catch (TimeoutException)
            {
                checkCancellation.Cancel();
                if (task is not null) ObserveFailure(task);
                return FailedCheck(application, $"The update check timed out after {_providerTimeout.TotalSeconds:0} seconds. Other applications were still checked.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                checkCancellation.Cancel();
                if (task is not null) ObserveFailure(task);
                throw;
            }
            catch (Exception exception)
            {
                return FailedCheck(application, exception.Message);
            }
            finally
            {
                _checkSlots.Release();
                progress?.Report(new(Interlocked.Increment(ref completed), applications.Length, "checks", application.DisplayName));
            }
        }

        var results = await Task.WhenAll(applications.Select(CheckOneAsync));
        ReconcileCompanionChecks(results);
        return new UpdateCheckSnapshot(
            checkedAt,
            results.OrderBy(static result => result.Status == UpdateStatus.Available ? 0 : result.Status == UpdateStatus.Error ? 1 : 2)
                .ThenBy(static result => result.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            results.Count(static result => result.Status == UpdateStatus.Unsupported),
            generation);
    }

    private async Task<UpdateCheckResult> AssessApplicationAsync(
        InstalledApplication application, Guid generation, DateTimeOffset checkedAt,
        IReadOnlyDictionary<string, string> refreshFailures, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var preferredId = PreferredProviderId(application);
        IUpdateProvider? preferred = null;
        if (preferredId is not null)
        {
            preferred = _providers.FirstOrDefault(provider => provider.Id.Equals(preferredId, StringComparison.OrdinalIgnoreCase));
            if (preferred is null || !SupportsManagementMode(preferred, application.ManagementMode) ||
                IsBlocked(application, preferred.Id) || !preferred.CanHandle(application))
                return PreferredProviderUnavailableResult(application, preferredId);
        }
        var candidates = preferred is null ? ResolveHighestAuthorityCandidates(application) : [preferred];
        var selected = preferred ?? candidates.FirstOrDefault();
        if (selected is null)
            return new(application.Identity, application.DisplayName, application.NormalizedVersion, null, UpdateStatus.Unsupported,
                "unverified", "No verifiable update source", "unknown", "No verified update source discovered",
                "unknown", "unknown", null,
                "The application was inventoried, but no unambiguous catalog identity or installed updater protocol could be verified.", null);

        var tied = candidates.Where(provider => provider.Descriptor.Authority == selected.Descriptor.Authority &&
            provider.Descriptor.Specificity == selected.Descriptor.Specificity).ToArray();
        if (preferred is null && tied.Length > 1) return AmbiguousProviderResult(application, tied);
        var ids = candidates.Select(static provider => provider.Id).ToArray();
        async Task<UpdateCheckResult> CheckSourceAsync(IUpdateProvider provider)
        {
            if (refreshFailures.TryGetValue(provider.Id, out var failure)) return ProviderContractError(provider, application, ids, failure);
            using var sourceCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var check = Task.Run(() => SafeCheckCoreAsync(provider, application, ids, preferred is not null, generation, checkedAt, sourceCancellation.Token), sourceCancellation.Token);
            try { return await check.WaitAsync(_sourceCheckTimeout, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (TimeoutException)
            {
                sourceCancellation.Cancel(); ObserveFailure(check);
                return ProviderContractError(provider, application, ids, "This source timed out; other compatible sources were still checked.");
            }
        }
        var checks = await Task.WhenAll(candidates.Select(CheckSourceAsync));
        // Do not let an unimplemented owner adapter suppress a working source.
        // Conversely, never bypass a higher-authority verification failure with
        // a lower-authority installer. Explicit provider policies stay exclusive.
        var firstDefinitive = Array.FindIndex(checks, c => c.Status is not (UpdateStatus.ManagedExternally or UpdateStatus.Unsupported));
        var available = Array.FindIndex(checks, c => c.IsInstallable);
        var selectedIndex = available >= 0 && !checks.Take(available).Any(c => c.Status == UpdateStatus.Error)
            ? available : firstDefinitive >= 0 ? firstDefinitive : 0;
        return checks[selectedIndex] with
        {
            SourceChecks = checks.Select(c =>
            new UpdateSourceCheck(c.ProviderId, c.ProviderDisplayName, c.Status, c.AvailableVersion, c.Message)).ToArray()
        };
    }

    private static UpdateCheckResult FailedCheck(InstalledApplication application, string error) => new(
        application.Identity, application.DisplayName, application.NormalizedVersion, null, UpdateStatus.Error,
        "provider-check", "Update provider check", "unknown", "Check did not complete",
        "unknown", "unknown", null, error, null, Applicability: UpdateApplicability.Unknown);

    private static void ReconcileCompanionChecks(UpdateCheckResult[] results)
    {
        var firefox = results.Where(r => r.ProviderId == "mozilla-firefox").ToArray();
        if (firefox.Length == 0 || firefox.Any(r => r.Status != UpdateStatus.Current)) return;
        for (var i = 0; i < results.Length; i++)
        {
            var service = results[i];
            if (service.DisplayName != "Mozilla Maintenance Service" || service.ProviderId != "native-updater" ||
                string.IsNullOrWhiteSpace(service.InstalledVersion) ||
                firefox.Any(f => string.IsNullOrWhiteSpace(f.InstalledVersion) || VersionOrder.Compare(service.InstalledVersion, f.InstalledVersion) < 0)) continue;
            results[i] = service with
            {
                Status = UpdateStatus.Current,
                ProviderId = "mozilla-maintenance",
                ProviderDisplayName = "Mozilla Firefox maintenance component",
                Applicability = UpdateApplicability.NotRequired,
                ReleaseNotesUrl = firefox[0].ReleaseNotesUrl,
                Message = "The installed Firefox release was checked against Mozilla. Its shared maintenance service is at least as recent and is updated together with Firefox.",
                SourceChecks = firefox.SelectMany(f => f.SourceChecks ?? []).ToArray()
            };
        }
    }

    private static void ObserveFailure(Task task) =>
        _ = task.ContinueWith(static failed => { _ = failed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

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
        var allCandidates = new List<IUpdateProvider>();
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
                allCandidates.AddRange(candidates);
            }
        }
        return allCandidates.ToArray();
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
