using NaxUpdater.Core.Internal;
using NaxUpdater.Core.Models;
using System.Text.RegularExpressions;

namespace NaxUpdater.Core.Services;

internal static class MsixIntegrationCorrelator
{
    private static readonly HashSet<string> IgnoredPublisherWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "co", "company", "corp", "corporation", "gmbh", "inc", "incorporated", "limited", "llc", "ltd", "team", "usa"
    };

    public static int AttachToWin32Applications(IList<ApplicationCandidate> candidates)
    {
        var integrations = candidates.Where(static candidate => candidate.IsMsixIntegrationPackage).ToArray();
        var win32Candidates = candidates
            .Where(static candidate => !candidate.IsMsixIntegrationPackage && candidate.Identity.StartsWith("registry:", StringComparison.Ordinal))
            .ToArray();
        var attachedCount = 0;

        foreach (var integration in integrations)
        {
            var matches = win32Candidates
                .Select(candidate => new CorrelationMatch(candidate, Score(integration, candidate)))
                .Where(static match => match.Score >= 120)
                .OrderByDescending(static match => match.Score)
                .ToArray();
            if (matches.Length == 0)
            {
                continue;
            }

            var best = matches[0];
            if (matches.Length > 1 &&
                best.Score - matches[1].Score < 25 &&
                !AreEquivalentTargets(best.Candidate, matches[1].Candidate))
            {
                continue;
            }

            AttachEvidence(best.Candidate, integration);
            candidates.Remove(integration);
            attachedCount++;
        }

        return attachedCount;
    }

    private static int Score(ApplicationCandidate integration, ApplicationCandidate win32)
    {
        var integrationName = CanonicalProductName(integration.DisplayName);
        var win32Name = CanonicalProductName(win32.DisplayName);
        var nameScore = integrationName == win32Name
            ? 100
            : integrationName.Length >= 4 && win32Name.Length >= 4 &&
              (integrationName.StartsWith(win32Name, StringComparison.Ordinal) ||
               win32Name.StartsWith(integrationName, StringComparison.Ordinal))
                ? 70
                : 0;

        var executableScore = DeclaredExecutableMatchesPath(integration, win32) ? 80 : 0;
        if (nameScore == 0 && executableScore == 0)
        {
            return 0;
        }

        var publisherScore = PublishersOverlap(integration.Publisher, win32.Publisher) ? 25 : 0;
        return nameScore + executableScore + publisherScore;
    }

    private static bool DeclaredExecutableMatchesPath(ApplicationCandidate integration, ApplicationCandidate win32)
    {
        var declaredNames = integration.MsixManifest?.DeclaredExecutables
            .Select(static path => NativePathParser.NormalizeName(Path.GetFileNameWithoutExtension(path)))
            .Where(static name => name.Length > 0)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        return win32.Paths.Any(path =>
            declaredNames.Contains(NativePathParser.NormalizeName(Path.GetFileNameWithoutExtension(path.Path))));
    }

    private static bool PublishersOverlap(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        var firstTokens = PublisherTokens(first);
        var secondTokens = PublisherTokens(second);
        return firstTokens.Overlaps(secondTokens);
    }

    private static HashSet<string> PublisherTokens(string publisher)
    {
        return Regex.Split(publisher.ToLowerInvariant(), @"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant)
            .Where(static token => token.Length >= 3 && !IgnoredPublisherWords.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string CanonicalProductName(string displayName)
    {
        var normalized = NativePathParser.NormalizeName(displayName);
        string[] architectureSuffixes = ["64bitx64", "32bitx86", "64bit", "32bit", "arm64", "x8664", "x64", "x86"];
        foreach (var suffix in architectureSuffixes)
        {
            if (normalized.EndsWith(suffix, StringComparison.Ordinal) && normalized.Length > suffix.Length + 2)
            {
                return normalized[..^suffix.Length];
            }
        }
        return normalized;
    }

    private static bool AreEquivalentTargets(ApplicationCandidate first, ApplicationCandidate second)
    {
        return CanonicalProductName(first.DisplayName) == CanonicalProductName(second.DisplayName) &&
               string.Equals(first.Publisher, second.Publisher, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(first.RegistryVersion, second.RegistryVersion, StringComparison.OrdinalIgnoreCase) &&
               first.Paths.Select(static path => path.Path).Intersect(
                   second.Paths.Select(static path => path.Path),
                   StringComparer.OrdinalIgnoreCase).Any();
    }

    private static void AttachEvidence(ApplicationCandidate target, ApplicationCandidate integration)
    {
        var familyName = integration.MsixPackageFamilyName ?? integration.Identity;
        var version = integration.ProviderVersion ?? "version not reported";
        target.Evidence.Add(new ApplicationEvidence(
            EvidenceKind.MsixPackage,
            "Attached MSIX integration package",
            $"{integration.DisplayName} · {familyName} · package version {version}",
            true));
        foreach (var evidence in integration.Evidence)
        {
            target.Evidence.Add(evidence with { Label = $"MSIX integration · {evidence.Label}" });
        }
    }

    private sealed record CorrelationMatch(ApplicationCandidate Candidate, int Score);
}
