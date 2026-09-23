using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

public sealed class UpdateCheckService
{
    private readonly IReadOnlyList<IUpdateProvider> _providers;
    private readonly SemaphoreSlim _checkSlots = new(16, 16);
    private readonly SemaphoreSlim _storeCheckSlots = new(8, 8);
    // Longer than NativeStoreUpdateClient's 10-second unresponsive cooldown.
    private static readonly TimeSpan StoreRetryDelay = TimeSpan.FromSeconds(11);
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
        // Below WinGet in the same FallbackCatalog authority tier (lower Specificity):
        // consulted only when neither a producer-owned source nor WinGet claims the app.
        providers.Add(new ChocolateyUpdateProvider());
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
            // Admission time is not execution time. Limit Store families before
            // starting the per-app/per-source budgets or occupying generic slots.
            var store = application.ManagementMode == ManagementMode.Msix;
            if (store) await _storeCheckSlots.WaitAsync(token);
            try { await _checkSlots.WaitAsync(token); }
            catch { if (store) _storeCheckSlots.Release(); throw; }
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
                if (store) _storeCheckSlots.Release();
                progress?.Report(new(Interlocked.Increment(ref completed), applications.Length, "checks", application.DisplayName));
            }
        }

        var results = await Task.WhenAll(applications.Select(CheckOneAsync));
        // One stalled Microsoft Store request puts the shared Store service into a
        // short cooldown in which every other Store family fails fast. Recheck those
        // families once after the cooldown instead of reporting a whole batch of
        // failures for a single transient stall.
        var storeRetries = Enumerable.Range(0, applications.Length)
            .Where(i => applications[i].ManagementMode == ManagementMode.Msix && results[i].Status == UpdateStatus.Error)
            .ToArray();
        if (storeRetries.Length > 0)
        {
            await Task.Delay(StoreRetryDelay, token);
            completed = applications.Length - storeRetries.Length;
            var retried = await Task.WhenAll(storeRetries.Select(i => CheckOneAsync(applications[i])));
            for (var k = 0; k < storeRetries.Length; k++) results[storeRetries[k]] = retried[k];
        }
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
        var durations = new System.Collections.Concurrent.ConcurrentDictionary<string, double>();
        async Task<UpdateCheckResult> CheckSourceAsync(IUpdateProvider provider)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
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
            finally { durations[provider.Id] = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
        }
        // Authority tiers are independent update channels - e.g. the Store can
        // have its own offer even while a producer feed says Current - and are
        // always all checked, concurrently with each other. Within a tier,
        // candidates are checked in specificity order; an actionable offer
        // (IsInstallable) stops the tier immediately (provably safe: see the
        // selection logic below, which can only pick an installable result
        // outright). A mere Current only skips a remaining same-tier sibling
        // that has explicitly opted in via SkippableAfterHigherSiblingResolves -
        // anything that has not opted in is still always checked, so a genuine
        // Error from it can never be silently hidden behind a Current result.
        // Each result is paired with its own provider directly (not by array
        // position), so duration/SourceChecks attribution can never drift.
        var tiers = new List<IUpdateProvider[]>();
        for (var i = 0; i < candidates.Length;)
        {
            var tierAuthority = candidates[i].Descriptor.Authority;
            var j = i;
            while (j < candidates.Length && candidates[j].Descriptor.Authority == tierAuthority) j++;
            tiers.Add(candidates[i..j]);
            i = j;
        }
        async Task<List<(IUpdateProvider Provider, UpdateCheckResult Result)>> CheckTierAsync(IUpdateProvider[] tierCandidates)
        {
            var tierChecks = new List<(IUpdateProvider, UpdateCheckResult)>();
            for (var i = 0; i < tierCandidates.Length; i++)
            {
                var candidate = tierCandidates[i];
                var result = await CheckSourceAsync(candidate);
                tierChecks.Add((candidate, result));
                if (result.Status == UpdateStatus.StoreQueued || result.IsInstallable)
                {
                    break;
                }
                if (result.Status == UpdateStatus.Current)
                {
                    while (i + 1 < tierCandidates.Length && tierCandidates[i + 1].Descriptor.SkippableAfterHigherSiblingResolves)
                    {
                        i++;
                    }
                }
            }
            return tierChecks;
        }
        var tierResults = await Task.WhenAll(tiers.Select(CheckTierAsync));
        var checkResults = tierResults.SelectMany(t => t).ToArray();
        var checks = checkResults.Select(c => c.Result).ToArray();
        // Do not let an unimplemented owner adapter suppress a working source.
        // Conversely, never bypass a higher-authority verification failure with
        // a lower-authority installer. Explicit provider policies stay exclusive.
        var firstDefinitive = Array.FindIndex(checks, c => c.Status is not (UpdateStatus.ManagedExternally or UpdateStatus.Unsupported));
        var available = Array.FindIndex(checks, c => c.IsInstallable);
        var selectedIndex = available >= 0 && !checks.Take(available).Any(c => c.Status == UpdateStatus.Error)
            ? available : firstDefinitive >= 0 ? firstDefinitive : 0;
        // A platform-owned deployment blocks duplicate execution through any
        // other provider. A Current result must not hide another source's error.
        var queued = Array.FindIndex(checks, c => c.Status == UpdateStatus.StoreQueued);
        var error = Array.FindIndex(checks, c => c.Status == UpdateStatus.Error);
        if (queued >= 0) selectedIndex = queued;
        else if (!checks[selectedIndex].IsInstallable && error >= 0) selectedIndex = error;
        return checks[selectedIndex] with
        {
            SourceChecks = checkResults.Select(c =>
            new UpdateSourceCheck(c.Result.ProviderId, c.Result.ProviderDisplayName, c.Result.Status, c.Result.AvailableVersion, c.Result.Message,
                durations.GetValueOrDefault(c.Provider.Id))).ToArray()
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
                (result.Status != UpdateStatus.Available && !(result.Status == UpdateStatus.StoreQueued && result.ExecutionPlan.Kind == UpdateExecutionKind.NativeStoreQueue) || result.Applicability == UpdateApplicability.NotApplicable))
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
            var plan = normalizedStatus is not (UpdateStatus.Available or UpdateStatus.StoreQueued) || result.ExecutionPlan is null
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
            if (boundResult.ExecutionPlan is not null && boundResult.Status is not (UpdateStatus.Available or UpdateStatus.StoreQueued))
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
        // Bootstrapper-staged installers (WiX Burn "Package Cache\{bundleGuid}\..."
        // and similar) register a fresh uninstall key and cache path per version,
        // by design, on every successful update. A raw path/registry correlation
        // key is then guaranteed to change across the exact event it needs to
        // survive. When the installed version is literally baked into the display
        // name (e.g. ".NET Desktop Runtime - 9.0.3 (x86)"), key on the
        // version-stripped name instead, which stays stable across the update.
        var version = application.NormalizedVersion ?? application.InstalledVersion;
        if (!string.IsNullOrWhiteSpace(version) &&
            application.DisplayName.Contains(version, StringComparison.OrdinalIgnoreCase))
        {
            var strippedName = application.DisplayName
                .Replace(version, "", StringComparison.OrdinalIgnoreCase)
                .Trim();
            if (strippedName.Length > 0)
            {
                var publisher = (application.Publisher ?? "").Trim().ToUpperInvariant();
                // Keep the major version in the key: distinct major-version lines
                // of the same product can be installed side by side (e.g. .NET
                // Desktop Runtime 8.x and 9.x) and must not collapse onto one
                // key, while a patch/minor bump within the same major line - the
                // case this branch exists for - still keys identically across
                // the update that needs to survive.
                var major = Version.TryParse(version, out var parsedVersion)
                    ? parsedVersion.Major.ToString()
                    : version.ToUpperInvariant();
                return $"versioned-name:{publisher}|{strippedName.ToUpperInvariant()}|{major}";
            }
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

    // Resolves the single application that identifies as the one referenced by a
    // prior check: exact inventory identity first, else the one application whose
    // correlation key matches. If more than one installed application shares that
    // correlation key (e.g. two side-by-side versions of the same product), the
    // match is ambiguous and must not be guessed at.
    public static InstalledApplication? FindMatch(
        IEnumerable<InstalledApplication> candidates,
        string identity,
        string? correlationKey)
    {
        var applications = candidates as IReadOnlyList<InstalledApplication> ?? candidates.ToArray();
        var byIdentity = applications.FirstOrDefault(candidate =>
            candidate.Identity.Equals(identity, StringComparison.Ordinal));
        if (byIdentity is not null)
        {
            return byIdentity;
        }
        if (string.IsNullOrWhiteSpace(correlationKey))
        {
            return null;
        }
        var byCorrelation = applications
            .Where(candidate => ForApplication(candidate).Equals(correlationKey, StringComparison.Ordinal))
            .ToArray();
        return byCorrelation.Length == 1 ? byCorrelation[0] : null;
    }
}
