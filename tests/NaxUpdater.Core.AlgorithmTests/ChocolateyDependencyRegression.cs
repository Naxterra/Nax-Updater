using System.Runtime.InteropServices;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

// 2026-10-09: Node.js was offered nodejs.install 26.11.1 while the installed
// nodejs 26.11.0 pinned nodejs.install [26.11.0]; choco resolved for 9 minutes,
// reinstalled 26.11.0 and exited 0. LibreOffice failed because the elevated
// WinGet call quoted "C:\Program Files\LibreOffice\" and the trailing backslash
// escaped the quote.
internal static class ChocolateyDependencyRegression
{
    public static async Task RunAsync(Action<bool, string> assert, string fixture)
    {
        assert(ChocolateyVersionRange.Allows("[26.11.0]", "26.11.0") && !ChocolateyVersionRange.Allows("[26.11.0]", "26.11.1") &&
               ChocolateyVersionRange.Allows("1.0.2", "1.0.4") && !ChocolateyVersionRange.Allows("1.0.2", "1.0.1") &&
               ChocolateyVersionRange.Allows("[1.0,2.0)", "1.9") && !ChocolateyVersionRange.Allows("[1.0,2.0)", "2.0") &&
               ChocolateyVersionRange.Allows("(,2.0]", "2.0") && !ChocolateyVersionRange.Allows("(1.0,)", "1.0") &&
               ChocolateyVersionRange.Allows("", "9"),
            "NuGet dependency version ranges were evaluated wrongly.");
        assert(ChocolateyVersionRange.Exact("[26.11.0]") == "26.11.0" && ChocolateyVersionRange.Exact("26.11.0") is null &&
               ChocolateyVersionRange.Exact("[1.0,2.0]") is null,
            "An exact Chocolatey dependency pin was not recognized.");
        var parsed = ChocolateyDependencyPlanner.ParseFeedDependencies("nodejs.install:[26.11.0]:|chocolatey-core.extension:1.3.3:");
        assert(parsed.Count == 2 && parsed[0] == new ChocolateyDependency("nodejs.install", "[26.11.0]") &&
               parsed[1].Range == "1.3.3" && ChocolateyDependencyPlanner.ParseFeedDependencies("").Count == 0,
            "Feed dependencies were not parsed.");

        var root = Path.Combine(fixture, "choco-dependencies");
        void Nuspec(string id, string version, params (string Id, string Range)[] dependencies)
        {
            Directory.CreateDirectory(Path.Combine(root, "lib", id));
            var deps = dependencies.Length == 0 ? "" :
                "<dependencies>" + string.Concat(dependencies.Select(d => $"<dependency id=\"{d.Id}\" version=\"{d.Range}\" />")) + "</dependencies>";
            File.WriteAllText(Path.Combine(root, "lib", id, id + ".nuspec"),
                $"<?xml version=\"1.0\"?><package xmlns=\"http://schemas.microsoft.com/packaging/2015/06/nuspec.xsd\"><metadata><id>{id}</id><version>{version}</version>{deps}</metadata></package>");
        }
        Nuspec("nodejs.install", "26.11.0");
        Nuspec("nodejs", "26.11.0", ("nodejs.install", "[26.11.0]"));
        Nuspec("python314", "3.14.8");
        Nuspec("python3", "3.14.8", ("python314", "[3.14.8]"));
        Nuspec("python", "3.14.8", ("python3", "[3.14.8]"));
        Nuspec("git.install", "2.51.0");
        var installed = ChocolateyDependencyPlanner.ReadInstalled(root);
        assert(installed.Count == 6 && installed["nodejs"].Dependencies!.Single() == new ChocolateyDependency("nodejs.install", "[26.11.0]") &&
               installed["git.install"].Dependencies!.Count == 0 && ChocolateyDependencyPlanner.ReadInstalled(null).Count == 0,
            "Installed Chocolatey packages and their dependencies were not read.");

        var feed = new Dictionary<string, ChocolateyPackageDetails>(StringComparer.OrdinalIgnoreCase)
        {
            ["nodejs"] = new("nodejs", "26.11.0", [new("nodejs.install", "[26.11.0]")]),
            ["python"] = new("python", "3.14.9", [new("python3", "[3.14.9]")]),
            ["python3@3.14.9"] = new("python3", "3.14.9", [new("python314", "[3.14.9]")])
        };
        Task<ChocolateyPackageDetails?> Details(string id, string? version) =>
            Task.FromResult(feed.TryGetValue(version is null ? id : $"{id}@{version}", out var found) ? found : null);

        var blocked = await ChocolateyDependencyPlanner.PlanAsync("nodejs.install", "26.11.1", installed, Details);
        assert(blocked is ChocolateyUpgradeRoute.Blocked { Reason: var reason } &&
               reason.Contains("nodejs 26.11.0 requires nodejs.install [26.11.0]") && reason.Contains("next nodejs package"),
            "A payload pinned by its unchanged wrapper package was still offered as a direct upgrade.");

        feed["nodejs"] = new("nodejs", "26.11.1", [new("nodejs.install", "[26.11.1]")]);
        var through = await ChocolateyDependencyPlanner.PlanAsync("nodejs.install", "26.11.1", installed, Details);
        assert(through == new ChocolateyUpgradeRoute.ThroughDependent("nodejs", "26.11.1", "26.11.1"),
            "A published wrapper package was not used to carry its pinned payload's update.");

        var chain = await ChocolateyDependencyPlanner.PlanAsync("python314", "3.14.9", installed, Details);
        assert(chain == new ChocolateyUpgradeRoute.ThroughDependent("python", "3.14.9", "3.14.9"),
            "A two-level wrapper chain (python -> python3 -> python314) was not followed to its top package.");

        assert(await ChocolateyDependencyPlanner.PlanAsync("git.install", "2.52.0", installed, Details) ==
               new ChocolateyUpgradeRoute.Direct("git.install", "2.52.0"),
            "An unpinned package was not upgraded directly.");
        var unknownDependencies = await ChocolateyDependencyPlanner.PlanAsync("nodejs.install", "26.11.1", installed,
            (id, version) => Task.FromResult<ChocolateyPackageDetails?>(new(id, version ?? "26.11.1", null)));
        assert(unknownDependencies is ChocolateyUpgradeRoute.Blocked,
            "A wrapper whose dependencies the source cannot report was trusted to carry the update.");

        // The provider end to end: the pinned case is a known release, never an installable update.
        var service = new FakeService(installed, feed);
        var provider = new ChocolateyInstalledUpdateProvider(service);
        var node = new InstalledApplication("registry:node", "Node.js", "Node.js Foundation", "26.11.0", "26.11.0", "fixture",
            Environment.ProcessPath, "fixture", null, null, InstallScope.Machine, ManagementMode.WindowsInstaller, ConfidenceLevel.High,
            false, [], null,
            [new(EvidenceKind.Policy, "Chocolatey package", "nodejs.install 26.11.0", true),
             new(EvidenceKind.Policy, "Chocolatey package role", "primary", true)]);
        feed["nodejs"] = new("nodejs", "26.11.0", [new("nodejs.install", "[26.11.0]")]);
        service.Latest["nodejs.install"] = "26.11.1";
        var known = await provider.CheckAsync(node, CancellationToken.None);
        assert(known.Status == UpdateStatus.NewerReleaseKnown && known.Applicability == UpdateApplicability.NotApplicable &&
               !known.IsInstallable && known.ExecutionPlan is null && known.AvailableVersion == "26.11.1",
            "The pinned Node.js payload was offered as an installable Chocolatey update.");
        feed["nodejs"] = new("nodejs", "26.11.1", [new("nodejs.install", "[26.11.1]")]);
        var carried = await provider.CheckAsync(node, CancellationToken.None);
        assert(carried.IsInstallable && carried.AvailableVersion == "26.11.1" &&
               carried.ExecutionPlan?.ChocolateyTarget == new ChocolateyUpdateTarget("nodejs", "26.11.1"),
            "The Node.js update was not applied through the nodejs wrapper package once it was published.");

        // Elevated WinGet arguments: what winget.exe parses must be the exact folder.
        foreach (var location in new[] { @"C:\Program Files\LibreOffice\", @"C:\Program Files\LibreOffice", @"D:\", @"C:\A\\" })
        {
            var quoted = WingetPackageService.QuoteArgument(location);
            var argv = ParseCommandLine($"winget.exe --location {quoted} --next");
            assert(argv is [_, "--location", var value, "--next"] && value == location,
                $"The install location {location} did not survive Windows argument parsing ({quoted}).");
        }
    }

    private static string[] ParseCommandLine(string commandLine)
    {
        var pointer = CommandLineToArgvW(commandLine, out var count);
        try
        {
            return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!).ToArray();
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private sealed class FakeService(
        IReadOnlyDictionary<string, ChocolateyPackageDetails> installed,
        Dictionary<string, ChocolateyPackageDetails> feed) : IChocolateyPackageService
    {
        public Dictionary<string, string> Latest { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsAvailable => true;
        public Task<ChocolateyPackageOffer> FindLatestAsync(IEnumerable<string> nameCandidates, string? installedVersion, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<string?> FindPackageVersionAsync(string packageId, CancellationToken token) =>
            Task.FromResult(Latest.TryGetValue(packageId, out var version) ? version : null);
        public Task<PreparedChocolateyUpdate> PrepareAsync(UpdateCheckResult update, CancellationToken token) => throw new NotSupportedException();
        public IReadOnlyDictionary<string, ChocolateyPackageDetails> InstalledPackages() => installed;
        public Task<ChocolateyPackageDetails?> FindPackageDetailsAsync(string packageId, string? version, CancellationToken token) =>
            Task.FromResult(feed.TryGetValue(version is null ? packageId : $"{packageId}@{version}", out var found) ? found : null);
    }
}
