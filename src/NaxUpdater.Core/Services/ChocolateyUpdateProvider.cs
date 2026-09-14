using System.Text.RegularExpressions;
using NaxUpdater.Core.Internal;
using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

// Last-resort fallback catalog, deliberately below WinGet: WinGet's structured
// COM API and local index give it a stronger identity match (product code /
// upgrade code) than Chocolatey's name-only CLI search can offer. Chocolatey is
// only consulted when nothing else, including WinGet, has claimed the app.
public sealed partial class ChocolateyUpdateProvider(IChocolateyPackageService? packages = null) : IUpdateProvider
{
    private readonly IChocolateyPackageService _packages = packages ?? new ChocolateyPackageService();

    public string Id => "chocolatey-fallback";
    public UpdateProviderDescriptor Descriptor { get; } = new(
        UpdateProviderAuthority.FallbackCatalog,
        25,
        "Last-resort catalog, consulted only when no installed, producer-owned, Store, or WinGet source claims the application",
        [ManagementMode.Unmanaged, ManagementMode.Registry, ManagementMode.WindowsInstaller, ManagementMode.DirectVendor],
        // A real network round-trip through choco.exe per app dominated scan
        // time. Once WinGet (the only higher-specificity sibling in this tier)
        // already reports Current, Chocolatey's own errors here are rarely
        // actionable - opt in to being skipped in that case.
        SkippableAfterHigherSiblingResolves: true);

    public bool CanHandle(InstalledApplication application) =>
        _packages.IsAvailable && NameCandidates(application).Any();

    public async Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        var candidates = NameCandidates(application).ToArray();
        if (candidates.Length == 0)
        {
            return Unsupported(application, "No usable application name was available to search Chocolatey.");
        }
        var offer = await _packages.FindLatestAsync(candidates, application.NormalizedVersion, cancellationToken);
        if (offer.Error is not null)
        {
            return Error(application, offer.Error);
        }
        if (offer.Target is null)
        {
            return offer.Found
                ? new(
                    application.Identity, application.DisplayName, application.NormalizedVersion, null,
                    UpdateStatus.Current, Id, "Chocolatey community feed",
                    "application-managed", "Selected by choco.exe", "provider-selected", "stable",
                    null, "Chocolatey reports no newer package for this installation.", null)
                : Unsupported(application, "No exact Chocolatey community package matched this application.");
        }
        var executable = InstalledApplicationMetadata.Executable(application);
        var plan = new UpdateExecutionPlan(
            UpdateExecutionKind.ChocolateyPackage, null, null, null, null, null, [], true, [],
            executable is null ? [] : [Path.GetFileNameWithoutExtension(executable)],
            RunningExecutablePaths: executable is null ? [] : [executable],
            ChocolateyTarget: offer.Target);
        return new(
            application.Identity, application.DisplayName, application.NormalizedVersion, offer.Target.Version,
            UpdateStatus.Available, Id, "Chocolatey community feed",
            "application-managed", "Selected by choco.exe", "provider-selected", "stable",
            $"https://community.chocolatey.org/packages/{offer.Target.PackageId}",
            $"Chocolatey identifies {offer.Target.PackageId} {offer.Target.Version} as an exact-name match for this installation.",
            plan);
    }

    public bool OwnsResultProviderId(string resultProviderId) => resultProviderId.Equals(Id, StringComparison.Ordinal);

    private static IEnumerable<string> NameCandidates(InstalledApplication application)
    {
        // At most 2 candidates: each one costs a real network round-trip through
        // choco.exe (no local index like WinGet's SQLite cache), so candidates are
        // ordered most-likely-first and FindLatestAsync stops at the first hit.
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        Add(application.DisplayName);
        Add(ArchitectureSuffixRegex().Replace(VersionSuffixRegex().Replace(application.DisplayName, string.Empty), string.Empty));
        return candidates;

        void Add(string? value)
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : NativePathParser.NormalizeName(value);
            if (normalized.Length >= 3)
            {
                candidates.Add(normalized);
            }
        }
    }

    private UpdateCheckResult Error(InstalledApplication application, string message) => new(
        application.Identity, application.DisplayName, application.NormalizedVersion, null, UpdateStatus.Error,
        Id, "Chocolatey community feed", "unknown", "Selected by choco.exe", "unknown", "unknown", null, message, null);

    // Not a failure: Chocolatey simply has nothing to say about this application.
    // Unsupported (rather than Error) keeps this non-definitive, matching how the
    // rest of the pipeline already treats "no source found" (UpdateCheckService.cs).
    private UpdateCheckResult Unsupported(InstalledApplication application, string message) => new(
        application.Identity, application.DisplayName, application.NormalizedVersion, null, UpdateStatus.Unsupported,
        Id, "Chocolatey community feed", "unknown", "Selected by choco.exe", "unknown", "unknown", null, message, null);

    [GeneratedRegex(@"(?:\s+|\s*[-(]\s*)v?\d+(?:\.\d+)+(?:[^)]*)?\)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionSuffixRegex();

    [GeneratedRegex(@"\s*\((?:x64|x86|arm64|64-bit|32-bit)[^)]*\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArchitectureSuffixRegex();
}
