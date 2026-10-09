using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

// Applications that Chocolatey installed are updated only through Chocolatey.
// Updating them through any other source leaves Chocolatey's package record
// behind, and `choco upgrade all` then reinstalls a version that is already
// installed. When the record is already behind, this offers exactly that
// reinstall, which is what Chocolatey itself would do next.
public sealed class ChocolateyInstalledUpdateProvider(IChocolateyPackageService? packages = null) : IUpdateProvider
{
    internal const string ProviderId = "chocolatey-installed";
    private readonly IChocolateyPackageService _packages = packages ?? new ChocolateyPackageService();

    public string Id => ProviderId;
    public UpdateProviderDescriptor Descriptor { get; } = new(
        UpdateProviderAuthority.InstalledUpdateProtocol,
        100,
        "Chocolatey package that installed the application, identified by Chocolatey's own registry snapshot",
        [ManagementMode.Unmanaged, ManagementMode.Registry, ManagementMode.WindowsInstaller, ManagementMode.DirectVendor]);

    public bool CanHandle(InstalledApplication application) =>
        ChocolateyOwnership.Package(application) is not null && _packages.IsAvailable;

    public async Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        var (packageId, recorded, primary) = ChocolateyOwnership.Package(application)!.Value;
        var page = $"https://community.chocolatey.org/packages/{Uri.EscapeDataString(packageId)}";
        if (!primary)
            return Result(application, null, UpdateStatus.Current, page,
                $"Installed by Chocolatey package {packageId} together with its main application, which carries the package's updates.", null);
        var latest = await _packages.FindPackageVersionAsync(packageId, cancellationToken);
        if (latest is null)
            return Result(application, null, UpdateStatus.Error, page,
                $"Chocolatey installed this application as {packageId}, but its configured source no longer lists that package.", null);
        if (string.IsNullOrWhiteSpace(application.NormalizedVersion))
            return Result(application, null, UpdateStatus.Error, page, "The installed version could not be read.", null);

        var installed = application.NormalizedVersion;
        string message;
        if (VersionOrder.Compare(latest, installed) > 0)
            message = $"Chocolatey installed this application as {packageId}; it is updated through Chocolatey so Chocolatey's package record stays in sync.";
        else if (VersionOrder.Compare(latest, installed) == 0 && VersionOrder.Compare(recorded, latest) < 0)
            message = $"{installed} was installed outside Chocolatey, whose record still says {packageId} {recorded}. " +
                      $"Updating reinstalls {packageId} {latest} through Chocolatey, as `choco upgrade` would, so its record matches.";
        else
            return Result(application, latest, UpdateStatus.Current, page,
                VersionOrder.Compare(installed, latest) > 0
                    ? $"The installed version is newer than Chocolatey's {packageId} {latest}; Chocolatey's record catches up when its package does."
                    : $"Chocolatey's {packageId} {latest} is installed and recorded.", null);

        // Another installed package may pin this one (nodejs -> nodejs.install [x]);
        // then only that package can carry the update.
        var route = await ChocolateyDependencyPlanner.PlanAsync(packageId, latest, _packages.InstalledPackages(),
            (id, version) => _packages.FindPackageDetailsAsync(id, version, cancellationToken));
        ChocolateyUpdateTarget target;
        var available = latest;
        switch (route)
        {
            case ChocolateyUpgradeRoute.Blocked blocked:
                return Result(application, latest, UpdateStatus.NewerReleaseKnown, page, blocked.Reason, null) with
                    { Applicability = UpdateApplicability.NotApplicable };
            case ChocolateyUpgradeRoute.ThroughDependent through:
                if (VersionOrder.Compare(through.TargetVersion, installed) <= 0)
                    return Result(application, latest, UpdateStatus.NewerReleaseKnown, page,
                        $"Chocolatey published {packageId} {latest}, but the installed packages pin it; the newest {through.RootPackageId} {through.RootVersion} still requires {packageId} {through.TargetVersion}.", null) with
                        { Applicability = UpdateApplicability.NotApplicable };
                target = new ChocolateyUpdateTarget(through.RootPackageId, through.RootVersion);
                available = through.TargetVersion;
                message = $"Chocolatey installed this application as {packageId}, which the installed {through.RootPackageId} package pins; " +
                          $"updating {through.RootPackageId} to {through.RootVersion} brings {packageId} {through.TargetVersion}.";
                break;
            default:
                target = new ChocolateyUpdateTarget(packageId, latest);
                break;
        }

        var executable = InstalledApplicationMetadata.Executable(application);
        var plan = new UpdateExecutionPlan(
            UpdateExecutionKind.ChocolateyPackage, null, null, null, null, null, [], true, [],
            executable is null ? [] : [Path.GetFileNameWithoutExtension(executable)],
            RunningExecutablePaths: executable is null ? [] : [executable],
            ChocolateyTarget: target);
        return Result(application, available, UpdateStatus.Available, page, message, plan);
    }

    private UpdateCheckResult Result(InstalledApplication application, string? available, UpdateStatus status,
        string page, string message, UpdateExecutionPlan? plan) => new(
        application.Identity, application.DisplayName, application.NormalizedVersion, available, status, Id,
        "Chocolatey package", "application-managed", "Selected by choco.exe", "provider-selected", "stable",
        page, message, plan);
}

// Chocolatey writes the uninstall keys a package created to
// <root>\.chocolatey\<id>.<version>\.registry. An application whose uninstall key
// is in that snapshot was installed by the package. An MSI updated outside
// Chocolatey gets a new product code, so when no inventoried application still
// has a snapshot key, exactly one application with the same version-free name,
// publisher and scope counts as the same installation.
internal static partial class ChocolateyOwnership
{
    internal const string PackageEvidenceLabel = "Chocolatey package";
    internal const string RoleEvidenceLabel = "Chocolatey package role";
    private const string PreferredProviderLabel = "Preferred update provider";

    internal sealed record SnapshotKey(string Location, InstallScope Scope, string DisplayName, string? Publisher);
    internal sealed record InstalledPackage(string PackageId, string Version, IReadOnlyList<SnapshotKey> Keys);

    public static (string PackageId, string RecordedVersion, bool Primary)? Package(InstalledApplication application)
    {
        var value = application.Evidence.FirstOrDefault(static e => e.Label == PackageEvidenceLabel)?.Value;
        var separator = value?.LastIndexOf(' ') ?? -1;
        if (separator <= 0) return null;
        var primary = application.Evidence.FirstOrDefault(static e => e.Label == RoleEvidenceLabel)?.Value != "companion";
        return (value![..separator], value[(separator + 1)..], primary);
    }

    public static IReadOnlyList<InstalledApplication> Apply(IReadOnlyList<InstalledApplication> applications, string? root)
    {
        if (root is null || !File.Exists(Path.Combine(root, "bin", "choco.exe"))) return applications;
        IReadOnlyList<InstalledPackage> packages;
        try { packages = ReadPackages(root); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return applications; }
        return Assign(applications, packages);
    }

    internal static IReadOnlyList<InstalledApplication> Assign(IReadOnlyList<InstalledApplication> applications, IReadOnlyList<InstalledPackage> packages)
    {
        // Applications with an explicit policy provider or external owner keep it.
        bool Eligible(InstalledApplication application) => !application.IsSystemComponent &&
            !application.Evidence.Any(static e => e.Label is PreferredProviderLabel or ExternalManagementClassifier.OwnerEvidenceLabel);
        var byLocation = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < applications.Count; i++)
            foreach (var evidence in applications[i].Evidence.Where(static e => e.Label == "Uninstall registry"))
                byLocation.TryAdd(evidence.Value.Trim(), i);

        var owners = new Dictionary<int, (InstalledPackage Package, bool Primary)>();
        foreach (var package in packages)
        {
            var matched = package.Keys.Select(key => byLocation.TryGetValue(key.Location, out var index) ? index : -1).ToArray();
            for (var k = 0; k < package.Keys.Count; k++)
            {
                if (matched[k] >= 0) continue;
                var key = package.Keys[k];
                var name = VersionFreeName(key.DisplayName);
                var candidates = Enumerable.Range(0, applications.Count).Where(i =>
                    !owners.ContainsKey(i) && !matched.Contains(i) &&
                    applications[i].Scope == key.Scope &&
                    string.Equals(applications[i].Publisher?.Trim(), key.Publisher?.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    VersionFreeName(applications[i].DisplayName) == name &&
                    SameVersionedLine(key.DisplayName, applications[i].DisplayName)).ToArray();
                if (candidates.Length == 1) matched[k] = candidates[0];
            }
            var primaryAssigned = false;
            foreach (var index in matched)
            {
                if (index < 0 || owners.ContainsKey(index) || !Eligible(applications[index])) continue;
                owners[index] = (package, !primaryAssigned);
                primaryAssigned = true;
            }
        }
        if (owners.Count == 0) return applications;
        return applications.Select((application, index) =>
        {
            if (!owners.TryGetValue(index, out var owner)) return application;
            var evidence = application.Evidence.ToList();
            evidence.Add(new(EvidenceKind.Policy, PackageEvidenceLabel, $"{owner.Package.PackageId} {owner.Package.Version}", true));
            evidence.Add(new(EvidenceKind.Policy, RoleEvidenceLabel, owner.Primary ? "primary" : "companion", true));
            evidence.Add(new(EvidenceKind.Policy, PreferredProviderLabel, ChocolateyInstalledUpdateProvider.ProviderId, true));
            return application with { Evidence = evidence };
        }).ToArray();
    }

    // The installed package version is lib\<id>\<id>.nuspec; the newest
    // .chocolatey\<id>.<version> folder that has a .registry snapshot names the
    // uninstall keys the package created.
    internal static IReadOnlyList<InstalledPackage> ReadPackages(string root)
    {
        var lib = Path.Combine(root, "lib");
        var records = Path.Combine(root, ".chocolatey");
        if (!Directory.Exists(lib) || !Directory.Exists(records)) return [];
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(lib))
        {
            var id = Path.GetFileName(directory);
            if (NuspecVersion(Path.Combine(directory, id + ".nuspec")) is { } version) versions[id] = version;
        }
        var snapshots = new Dictionary<string, (string Version, string Path)>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(records))
        {
            var name = Path.GetFileName(directory);
            var registry = Path.Combine(directory, ".registry");
            var id = versions.Keys.Where(candidate => name.Length > candidate.Length + 1 &&
                    name.StartsWith(candidate + ".", StringComparison.OrdinalIgnoreCase) &&
                    PackageVersionRegex().IsMatch(name[(candidate.Length + 1)..]))
                .MaxBy(static candidate => candidate.Length);
            if (id is null || !File.Exists(registry)) continue;
            var version = name[(id.Length + 1)..];
            if (!snapshots.TryGetValue(id, out var existing) || VersionOrder.Compare(version, existing.Version) > 0)
                snapshots[id] = (version, registry);
        }
        var packages = new List<InstalledPackage>();
        foreach (var (id, snapshot) in snapshots)
        {
            var keys = SnapshotKeys(snapshot.Path);
            if (keys.Count > 0) packages.Add(new(id, versions[id], keys));
        }
        return packages;
    }

    private static string? NuspecVersion(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader).Descendants().FirstOrDefault(static e => e.Name.LocalName == "version")?.Value.Trim() is { Length: > 0 } version
                ? version : null;
        }
        catch (XmlException) { return null; }
    }

    internal static IReadOnlyList<SnapshotKey> SnapshotKeys(string path)
    {
        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var keys = new List<SnapshotKey>();
            foreach (var key in XDocument.Load(reader).Descendants("key"))
            {
                var keyPath = key.Element("KeyPath")?.Value.Trim() ?? "";
                var marker = keyPath.IndexOf(@"\Uninstall\", StringComparison.OrdinalIgnoreCase);
                var (hive, scope) = keyPath.StartsWith(@"HKEY_LOCAL_MACHINE\", StringComparison.OrdinalIgnoreCase) ? ("LocalMachine", InstallScope.Machine)
                    : keyPath.StartsWith(@"HKEY_CURRENT_USER\", StringComparison.OrdinalIgnoreCase) ? ("CurrentUser", InstallScope.CurrentUser)
                    : (null, InstallScope.Machine);
                var view = key.Element("RegistryView")?.Value.Trim();
                var displayName = (string?)key.Attribute("displayName");
                if (hive is null || marker < 0 || view is not ("Registry64" or "Registry32") || string.IsNullOrWhiteSpace(displayName)) continue;
                var subKey = keyPath[(marker + @"\Uninstall\".Length)..];
                if (subKey.Length == 0 || subKey.Contains('\\')) continue;
                keys.Add(new($"{hive} {view} · {subKey}", scope, displayName.Trim(), key.Element("Publisher")?.Value.Trim()));
            }
            return keys;
        }
        catch (XmlException) { return []; }
    }

    private static string VersionFreeName(string name) =>
        Regex.Replace(VersionTokenRegex().Replace(name, " "), @"\s+", " ").Trim().ToLowerInvariant();

    // A version inside the display name marks a side-by-side release line
    // ("Python 3.14.8 (64-bit)"): only the same major.minor line is the same
    // installation. Names without a version ("Node.js") upgrade in place.
    private static bool SameVersionedLine(string recorded, string installed)
    {
        static string? Line(string name) =>
            VersionTokenRegex().Match(name) is { Success: true } match ? string.Join('.', match.Value.TrimStart('v', 'V').Split('.').Take(2)) : null;
        return Line(recorded) == Line(installed);
    }

    [GeneratedRegex(@"\bv?\d+(?:\.\d+)+\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionTokenRegex();

    [GeneratedRegex(@"^\d+(?:\.\d+)*(?:[-+][0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageVersionRegex();
}
