using NaxUpdater.Core.Internal;
using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

public sealed class ApplicationInventoryService
{
    private readonly string _policyPath;
    private readonly PolicyService _policyService = new();

    public ApplicationInventoryService(string policyPath)
    {
        _policyPath = policyPath;
    }

    public async Task<InventorySnapshot> ScanAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ApplicationPolicy> policies;
        var loadIssues = new List<InventoryIssue>();
        try
        {
            policies = await _policyService.LoadAsync(_policyPath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            policies = [];
            loadIssues.Add(new InventoryIssue("Application policies", exception.Message, exception.GetType().Name));
        }

        return await Task.Run(
            () => ScanCoreAsync(policies, loadIssues, cancellationToken),
            cancellationToken);
    }

    private static async Task<InventorySnapshot> ScanCoreAsync(
        IReadOnlyList<ApplicationPolicy> policies,
        IReadOnlyList<InventoryIssue> loadIssues,
        CancellationToken cancellationToken)
    {
        var issues = new List<InventoryIssue>(loadIssues);
        var candidates = new RegistryInventoryScanner().Scan(issues).ToList();
        cancellationToken.ThrowIfCancellationRequested();

        var shortcuts = new ShortcutScanner().Scan(issues);
        foreach (var candidate in candidates)
        {
            ShortcutScanner.ApplyMatches(candidate, shortcuts);
        }

        candidates.AddRange(new MsixInventoryScanner().Scan(issues));
        MsixIntegrationCorrelator.AttachToWin32Applications(candidates);
        var matchedPolicyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var zeroInstall = new ZeroInstallEnricher(new ProcessQueryRunner());
        var applications = new List<InstalledApplication>(candidates.Count);

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await zeroInstall.EnrichAsync(candidate, issues, cancellationToken);
            ApplyPolicy(candidate, policies, matchedPolicyIds);
            applications.Add(ExternalManagementClassifier.Classify(ExecutableMetadataEnricher.Finalize(candidate)));
        }

        var unmatchedPolicies = policies
            .Where(policy => policy.AppliesWhenAbsent && !matchedPolicyIds.Contains(policy.Id))
            .OrderBy(static policy => policy.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var mergedApplications = MergeMsiUpgradeFamilies(MergeExactDuplicates(applications));
        return new InventorySnapshot(
            DateTimeOffset.Now,
            mergedApplications
                .OrderBy(static application => application.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(static application => application.Publisher, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            unmatchedPolicies,
            issues.ToArray());
    }

    private static IReadOnlyList<InstalledApplication> MergeExactDuplicates(IReadOnlyList<InstalledApplication> applications)
    {
        var results = new List<InstalledApplication>(applications.Count);
        foreach (var group in applications.GroupBy(DeduplicationKey, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key.StartsWith("identity:", StringComparison.Ordinal) || group.Count() == 1)
            {
                results.AddRange(group);
                continue;
            }

            var items = group.ToArray();
            var first = items[0];
            var evidence = items
                .SelectMany(static application => application.Evidence)
                .Distinct()
                .ToArray();
            var blockedProviders = items
                .SelectMany(static application => application.BlockedProviders)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var confidence = items.Max(static application => application.Confidence);
            var scope = items.Any(static application => application.Scope == InstallScope.Machine)
                ? InstallScope.Machine
                : first.Scope;
            var datedItem = items
                .Where(static application => application.InstalledOn.HasValue)
                .OrderByDescending(static application => application.InstalledOn)
                .FirstOrDefault();
            var removalPlan = items.Select(static application => application.RemovalPlan).FirstOrDefault(static plan => plan is not null);
            results.Add(first with
            {
                Scope = scope,
                Confidence = confidence,
                InstalledOn = datedItem?.InstalledOn,
                InstallDateSource = datedItem?.InstallDateSource,
                BlockedProviders = blockedProviders,
                RemovalPlan = removalPlan,
                Evidence = evidence
            });
        }
        return results;
    }

    private static string DeduplicationKey(InstalledApplication application)
    {
        if (string.IsNullOrWhiteSpace(application.PrimaryInstallPath))
        {
            return $"identity:{application.Identity}";
        }

        var name = NativePathParser.NormalizeName(application.DisplayName);
        var publisher = NativePathParser.NormalizeName(application.Publisher ?? string.Empty);
        var version = application.InstalledVersion?.Trim() ?? string.Empty;
        var path = application.PrimaryInstallPath.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return $"facts:{name}|{publisher}|{version}|{path}";
    }

    private static IReadOnlyList<InstalledApplication> MergeMsiUpgradeFamilies(IReadOnlyList<InstalledApplication> applications)
    {
        var results = new List<InstalledApplication>(applications.Count);
        foreach (var group in applications.GroupBy(MsiUpgradeFamilyKey, StringComparer.OrdinalIgnoreCase))
        {
            var items = group.ToArray();
            if (group.Key.StartsWith("identity:", StringComparison.Ordinal) || items.Length == 1)
            {
                results.AddRange(items);
                continue;
            }

            var primary = items.Aggregate((current, candidate) =>
                VersionOrder.Compare(candidate.NormalizedVersion, current.NormalizedVersion) > 0
                    ? candidate
                    : current);
            var evidence = items
                .SelectMany(static application => application.Evidence)
                .Distinct()
                .ToArray();
            var blockedProviders = items
                .SelectMany(static application => application.BlockedProviders)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var datedItem = items
                .Where(static application => application.InstalledOn.HasValue)
                .OrderByDescending(static application => application.InstalledOn)
                .FirstOrDefault();
            results.Add(primary with
            {
                InstalledOn = primary.InstalledOn ?? datedItem?.InstalledOn,
                InstallDateSource = primary.InstallDateSource ?? datedItem?.InstallDateSource,
                Confidence = items.Max(static application => application.Confidence),
                BlockedProviders = blockedProviders,
                Evidence = evidence
            });
        }
        return results;
    }

    private static string MsiUpgradeFamilyKey(InstalledApplication application)
    {
        var family = application.Evidence.FirstOrDefault(static evidence =>
            evidence.Label == RegistryInventoryScanner.InstallerUpgradeFamilyEvidenceLabel)?.Value;
        return string.IsNullOrWhiteSpace(family)
            ? $"identity:{application.Identity}"
            : $"msi-upgrade:{family}";
    }

    private static void ApplyPolicy(
        ApplicationCandidate candidate,
        IReadOnlyList<ApplicationPolicy> policies,
        ISet<string> matchedPolicyIds)
    {
        var policy = policies.FirstOrDefault(item => PolicyService.IsMatch(item, candidate.DisplayName, candidate.Publisher));
        if (policy is null)
        {
            return;
        }

        candidate.Policy = policy;
        matchedPolicyIds.Add(policy.Id);
        foreach (var provider in policy.BlockedProviders.Where(static provider => !string.IsNullOrWhiteSpace(provider)))
        {
            candidate.BlockedProviders.Add(provider.Trim());
        }
        if (policy.ManagementMode.HasValue)
        {
            candidate.ManagementMode = policy.ManagementMode.Value;
        }

        var policyDescription = string.IsNullOrWhiteSpace(policy.Reason)
            ? policy.Id
            : $"{policy.Id} · {policy.Reason}";
        candidate.Evidence.Add(new ApplicationEvidence(
            EvidenceKind.Policy,
            "Management policy",
            policyDescription,
            true));
        if (candidate.BlockedProviders.Count > 0)
        {
            candidate.Evidence.Add(new ApplicationEvidence(
                EvidenceKind.Policy,
                "Blocked providers",
                string.Join(", ", candidate.BlockedProviders.Order(StringComparer.OrdinalIgnoreCase)),
                true));
        }
        if (!string.IsNullOrWhiteSpace(policy.PreferredProvider))
        {
            candidate.Evidence.Add(new ApplicationEvidence(
                EvidenceKind.Policy,
                "Preferred update provider",
                policy.PreferredProvider,
                true));
        }
    }
}
