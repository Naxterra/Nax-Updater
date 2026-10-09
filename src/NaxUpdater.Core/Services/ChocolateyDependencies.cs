using System.Xml;
using System.Xml.Linq;
using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

public sealed record ChocolateyDependency(string Id, string Range);

// Dependencies is null when the source cannot report them (choco.exe search
// output carries none); an empty list means the package has none.
public sealed record ChocolateyPackageDetails(string Id, string Version, IReadOnlyList<ChocolateyDependency>? Dependencies);

// How a Chocolatey package can actually reach a newer version. Wrapper packages
// pin their payload exactly (nodejs -> nodejs.install [26.11.0], python ->
// python3 -> python314), so upgrading the payload alone conflicts with the pin:
// choco then spends minutes resolving, reinstalls the old version and still
// exits 0. The update has to come through the top installed package instead.
public abstract record ChocolateyUpgradeRoute
{
    public sealed record Direct(string PackageId, string Version) : ChocolateyUpgradeRoute;
    public sealed record ThroughDependent(string RootPackageId, string RootVersion, string TargetVersion) : ChocolateyUpgradeRoute;
    public sealed record Blocked(string Reason) : ChocolateyUpgradeRoute;
}

internal static class ChocolateyVersionRange
{
    // NuGet ranges: "1.0" (minimum, inclusive), "[1.0]" (exact),
    // "[1.0,2.0)", "(,2.0]", "(1.0,)".
    public static bool Allows(string range, string version)
    {
        range = range.Trim();
        if (range.Length == 0) return true;
        if (range[0] is not ('[' or '(')) return VersionOrder.Compare(version, range) >= 0;
        if (range.Length < 2 || range[^1] is not (']' or ')')) return false;
        var body = range[1..^1];
        var comma = body.IndexOf(',');
        if (comma < 0) return range[0] == '[' && range[^1] == ']' && VersionOrder.Compare(version, body.Trim()) == 0;
        var lower = body[..comma].Trim();
        var upper = body[(comma + 1)..].Trim();
        if (lower.Length > 0)
        {
            var compared = VersionOrder.Compare(version, lower);
            if (range[0] == '[' ? compared < 0 : compared <= 0) return false;
        }
        if (upper.Length > 0)
        {
            var compared = VersionOrder.Compare(version, upper);
            if (range[^1] == ']' ? compared > 0 : compared >= 0) return false;
        }
        return true;
    }

    public static string? Exact(string range)
    {
        range = range.Trim();
        return range.Length > 2 && range[0] == '[' && range[^1] == ']' && !range.Contains(',') ? range[1..^1].Trim() : null;
    }
}

internal static class ChocolateyDependencyPlanner
{
    // Installed packages and their dependencies, from lib\<id>\<id>.nuspec.
    public static IReadOnlyDictionary<string, ChocolateyPackageDetails> ReadInstalled(string? root)
    {
        var installed = new Dictionary<string, ChocolateyPackageDetails>(StringComparer.OrdinalIgnoreCase);
        var lib = root is null ? null : Path.Combine(root, "lib");
        if (lib is null || !Directory.Exists(lib)) return installed;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(lib))
            {
                var id = Path.GetFileName(directory);
                var nuspec = Path.Combine(directory, id + ".nuspec");
                if (!File.Exists(nuspec)) continue;
                try
                {
                    using var reader = XmlReader.Create(nuspec, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                    var document = XDocument.Load(reader);
                    var version = document.Descendants().FirstOrDefault(static e => e.Name.LocalName == "version")?.Value.Trim();
                    if (string.IsNullOrEmpty(version)) continue;
                    var dependencies = document.Descendants().Where(static e => e.Name.LocalName == "dependency")
                        .Select(static e => new ChocolateyDependency(((string?)e.Attribute("id"))?.Trim() ?? "", ((string?)e.Attribute("version"))?.Trim() ?? ""))
                        .Where(static d => d.Id.Length > 0)
                        .ToArray();
                    installed[id] = new(id, version, dependencies);
                }
                catch (XmlException)
                {
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        return installed;
    }

    // Feed form of dependencies: "id:range:framework|id:range:framework".
    public static IReadOnlyList<ChocolateyDependency> ParseFeedDependencies(string? value) =>
        (value ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static entry => entry.Split(':'))
            .Where(static parts => parts[0].Trim().Length > 0)
            .Select(static parts => new ChocolateyDependency(parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : ""))
            .ToArray();

    public static async Task<ChocolateyUpgradeRoute> PlanAsync(
        string packageId,
        string latestVersion,
        IReadOnlyDictionary<string, ChocolateyPackageDetails> installed,
        Func<string, string?, Task<ChocolateyPackageDetails?>> details)
    {
        static IReadOnlyList<(ChocolateyPackageDetails Package, string Range)> Dependents(
            string id, IReadOnlyDictionary<string, ChocolateyPackageDetails> installed) =>
            installed.Values
                .SelectMany(package => (package.Dependencies ?? [])
                    .Where(dependency => dependency.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    .Select(dependency => (package, dependency.Range)))
                .ToArray();

        var pins = Dependents(packageId, installed).Where(d => !ChocolateyVersionRange.Allows(d.Range, latestVersion)).ToArray();
        if (pins.Length == 0) return new ChocolateyUpgradeRoute.Direct(packageId, latestVersion);
        var pin = pins[0];
        var pinText = $"the installed {pin.Package.Id} {pin.Package.Version} requires {packageId} {pin.Range}";

        // The package that carries the update is the top of the installed chain.
        var chain = new List<string> { packageId };
        var current = packageId;
        while (Dependents(current, installed) is { Count: > 0 } parents)
        {
            var parentIds = parents.Select(static p => p.Package.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (parentIds.Length > 1)
                return new ChocolateyUpgradeRoute.Blocked($"Chocolatey published {packageId} {latestVersion}, but {pinText}, and several installed packages ({string.Join(", ", parentIds)}) depend on {current}.");
            current = parentIds[0];
            if (chain.Contains(current, StringComparer.OrdinalIgnoreCase))
                return new ChocolateyUpgradeRoute.Blocked($"Chocolatey published {packageId} {latestVersion}, but {pinText}, and the installed packages depend on each other.");
            chain.Add(current);
        }
        chain.Reverse();
        var root = chain[0];
        var rootLatest = await details(root, null);
        if (rootLatest is null)
            return new ChocolateyUpgradeRoute.Blocked($"Chocolatey published {packageId} {latestVersion}, but {pinText}, and the configured source does not list {root}.");
        if (VersionOrder.Compare(rootLatest.Version, installed[root].Version) <= 0)
            return new ChocolateyUpgradeRoute.Blocked($"Chocolatey published {packageId} {latestVersion}, but {pinText}. The update arrives with the next {root} package, which Chocolatey has not published yet.");

        // Follow the newest root package's exact pins down to the package.
        var parent = rootLatest;
        for (var i = 1; i < chain.Count; i++)
        {
            if (parent.Dependencies is null)
                return new ChocolateyUpgradeRoute.Blocked($"Chocolatey published {packageId} {latestVersion}, but {pinText}, and the configured source does not report the dependencies of {parent.Id} {parent.Version}.");
            var range = parent.Dependencies.FirstOrDefault(d => d.Id.Equals(chain[i], StringComparison.OrdinalIgnoreCase))?.Range;
            var exact = range is null ? null : ChocolateyVersionRange.Exact(range);
            if (exact is null)
                return new ChocolateyUpgradeRoute.Blocked($"Chocolatey published {packageId} {latestVersion}, but {pinText}, and {parent.Id} {parent.Version} does not pin one version of {chain[i]}.");
            if (i == chain.Count - 1) return new ChocolateyUpgradeRoute.ThroughDependent(root, rootLatest.Version, exact);
            parent = await details(chain[i], exact) is { } next
                ? next
                : new ChocolateyPackageDetails(chain[i], exact, null);
        }
        return new ChocolateyUpgradeRoute.Blocked($"Chocolatey published {packageId} {latestVersion}, but {pinText}.");
    }
}
