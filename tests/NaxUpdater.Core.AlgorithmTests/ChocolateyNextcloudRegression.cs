using System.Net;
using System.Text;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

internal static class ChocolateyNextcloudRegression
{
    public static async Task RunAsync(Action<bool, string> assert, string fixture)
    {
        // Chocolatey's own records: lib\<id>\<id>.nuspec and .chocolatey\<id>.<version>\.registry.
        var root = Path.Combine(fixture, "choco");
        void Package(string id, string libVersion, string recordVersion, params (string Name, string Path, string Publisher)[] keys)
        {
            Directory.CreateDirectory(Path.Combine(root, "lib", id));
            File.WriteAllText(Path.Combine(root, "lib", id, id + ".nuspec"),
                $"<?xml version=\"1.0\"?><package xmlns=\"http://schemas.microsoft.com/packaging/2015/06/nuspec.xsd\"><metadata><id>{id}</id><version>{libVersion}</version></metadata></package>");
            var record = Path.Combine(root, ".chocolatey", $"{id}.{recordVersion}");
            Directory.CreateDirectory(record);
            File.WriteAllText(Path.Combine(record, ".registry"), "<?xml version=\"1.0\" encoding=\"utf-8\"?><registrySnapshot><keys>" +
                string.Concat(keys.Select(k => $"<key installerType=\"Msi\" displayName=\"{k.Name}\" displayVersion=\"1\"><RegistryView>Registry64</RegistryView>" +
                    $"<KeyPath>{k.Path}</KeyPath><Publisher><![CDATA[{k.Publisher}]]></Publisher></key>")) + "</keys></registrySnapshot>");
        }
        const string machine = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";
        const string user = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";
        Package("nodejs.install", "26.8.2", "26.8.2", ("Node.js", machine + "{OLD-NODE}", "Node.js Foundation"));
        Package("nodejs", "26.8.2", "26.8.2");
        Package("python314", "3.14.8", "3.14.8",
            ("Python 3.14.8 (64-bit)", user + "{PY-BUNDLE}", "Python Software Foundation"),
            ("Python Launcher", machine + "{PY-LAUNCHER}", "Python Software Foundation"));
        var packages = ChocolateyOwnership.ReadPackages(root);
        assert(packages.Count == 2 && packages.Single(p => p.PackageId == "nodejs.install").Version == "26.8.2" &&
               packages.Single(p => p.PackageId == "python314").Keys.Count == 2,
            "Chocolatey package records or their registry snapshots were not read.");

        InstalledApplication App(string name, string publisher, string version, InstallScope scope, string location) => new(
            "registry:" + location, name, publisher, version, version, "fixture", Environment.ProcessPath, "fixture", null, null, scope,
            ManagementMode.WindowsInstaller, ConfidenceLevel.High, false, [], null,
            [new(EvidenceKind.Registry, "Uninstall registry", location, true)]);
        var node = App("Node.js", "Node.js Foundation", "26.10.0", InstallScope.Machine, "LocalMachine Registry64 · {NEW-NODE}");
        var bundle = App("Python 3.14.8 (64-bit)", "Python Software Foundation", "3.14.8", InstallScope.CurrentUser, "CurrentUser Registry64 · {PY-BUNDLE}");
        var launcher = App("Python Launcher", "Python Software Foundation", "3.14.8150.0", InstallScope.Machine, "LocalMachine Registry64 · {PY-LAUNCHER}");
        var python315 = App("Python 3.15.0 (64-bit)", "Python Software Foundation", "3.15.0", InstallScope.CurrentUser, "CurrentUser Registry64 · {PY315}");
        var policyOwned = App("Node.js", "Node.js Foundation", "26.10.0", InstallScope.CurrentUser, "CurrentUser Registry64 · {OTHER}");
        var assigned = ChocolateyOwnership.Assign([node, bundle, launcher, python315], packages);
        assert(ChocolateyOwnership.Package(assigned[0]) == ("nodejs.install", "26.8.2", true),
            "An MSI updated outside Chocolatey (new product code) lost its Chocolatey package ownership.");
        assert(ChocolateyOwnership.Package(assigned[1]) == ("python314", "3.14.8", true) &&
               ChocolateyOwnership.Package(assigned[2]) == ("python314", "3.14.8", false),
            "A package's main application and its companion entry were not told apart.");
        assert(ChocolateyOwnership.Package(assigned[3]) is null,
            "A side-by-side Python release line was claimed by another line's Chocolatey package.");
        assert(assigned[0].Evidence.Any(e => e.Label == "Preferred update provider" && e.Value == ChocolateyInstalledUpdateProvider.ProviderId),
            "A Chocolatey-installed application could still be updated outside Chocolatey.");
        var explicitPolicy = policyOwned with { Evidence = [.. policyOwned.Evidence, new(EvidenceKind.Policy, "Preferred update provider", "zero-install", true)] };
        assert(ChocolateyOwnership.Package(ChocolateyOwnership.Assign([explicitPolicy], packages)[0]) is null,
            "Chocolatey ownership overrode an explicit application policy.");

        var feed = new FakeChocolatey { Latest = "26.10.0" };
        var provider = new ChocolateyInstalledUpdateProvider(feed);
        var sync = await provider.CheckAsync(assigned[0], CancellationToken.None);
        assert(sync.Status == UpdateStatus.Available && sync.AvailableVersion == "26.10.0" &&
               sync.ExecutionPlan?.ChocolateyTarget == new ChocolateyUpdateTarget("nodejs.install", "26.10.0"),
            "Chocolatey's stale record was not offered the reinstall that choco upgrade would perform.");
        feed.Latest = "26.11.0";
        var upgrade = await provider.CheckAsync(assigned[0], CancellationToken.None);
        assert(upgrade.Status == UpdateStatus.Available && upgrade.ExecutionPlan?.ChocolateyTarget?.Version == "26.11.0",
            "A newer Chocolatey package was not applied through Chocolatey.");
        feed.Latest = "3.14.8";
        assert((await provider.CheckAsync(assigned[1], CancellationToken.None)).Status == UpdateStatus.Current,
            "An installed and recorded Chocolatey package was reported as an update.");
        feed.Latest = "3.14.9";
        var companion = await provider.CheckAsync(assigned[2], CancellationToken.None);
        assert(companion.Status == UpdateStatus.Current && companion.ExecutionPlan is null,
            "A companion entry offered a second Chocolatey upgrade of the same package.");
        feed.Latest = "26.9.0";
        var ahead = await provider.CheckAsync(assigned[0], CancellationToken.None);
        assert(ahead.Status == UpdateStatus.Current && ahead.ExecutionPlan is null,
            "Chocolatey offered to reinstall an older package over a newer installed version.");

        // Nextcloud: the channel comes from the client's own settings and the version
        // from Nextcloud's update server, in the numbering the MSI registers.
        assert(NextcloudUpdateProvider.ParseChannel(["[General]", "autoUpdateCheck=true", "updateChannel=beta"]) == "beta" &&
               NextcloudUpdateProvider.ParseChannel(["[Accounts]", "updateChannel=beta"]) is null &&
               NextcloudUpdateProvider.ParseChannel(["[General]", "updateChannel=../x"]) is null,
            "The Nextcloud update channel was not read from the [General] section.");
        var hash = new string('c', 64);
        string? serverQuery = null;
        var serverVersion = "34.0.94";
        using var http = new HttpClient(new Handler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.StartsWith("https://updates.nextcloud.org/client/", StringComparison.Ordinal))
            {
                serverQuery = url;
                return Text(serverVersion is null ? "<?xml version=\"1.0\"?><owncloudclient/>" :
                    $"<?xml version=\"1.0\"?><owncloudclient><version>{serverVersion}</version><versionstring>Nextcloud Client 35.0.0-rc4</versionstring>" +
                    "<downloadurl>https://github.com/nextcloud-releases/desktop/releases/download/v35.0.0-rc4/Nextcloud-35.0.0-rc4-x64.msi</downloadurl></owncloudclient>");
            }
            if (url == "https://api.github.com/repos/nextcloud-releases/desktop/releases/tags/v35.0.0-rc4")
                return Text($"{{\"assets\":[{{\"name\":\"Nextcloud-35.0.0-rc4-x64.msi\",\"digest\":\"sha256:{hash}\"}}]}}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var nextcloud = App("Nextcloud", "Nextcloud GmbH", "34.0.93", InstallScope.Machine, "LocalMachine Registry64 · {NC}");
        var nextcloudProvider = new NextcloudUpdateProvider(http, () => "beta");
        var beta = await nextcloudProvider.CheckAsync(nextcloud, CancellationToken.None);
        assert(serverQuery?.Contains("channel=beta") == true && serverQuery.Contains("osRelease=windows"),
            "Nextcloud's update server was not asked for the client's own channel.");
        assert(beta.Status == UpdateStatus.Available && beta.AvailableVersion == "34.0.94" && beta.ExecutionPlan is
               { Kind: UpdateExecutionKind.DownloadedMsi, Sha256: var sha, ExpectedSigner: "Nextcloud GmbH", FileName: "Nextcloud-35.0.0-rc4-x64.msi" } &&
               sha == hash,
            "A beta-channel Nextcloud release was not offered with its MSI-comparable version, GitHub digest and signer.");
        serverVersion = "34.0.93";
        assert((await nextcloudProvider.CheckAsync(nextcloud, CancellationToken.None)).Status == UpdateStatus.Current,
            "An installed release candidate was offered again because its tag and MSI version differ.");
        serverVersion = null;
        assert((await nextcloudProvider.CheckAsync(nextcloud, CancellationToken.None)).Status == UpdateStatus.Current,
            "An empty update-server answer was not treated as current.");
        hash = "short";
        serverVersion = "34.0.94";
        var unverified = await nextcloudProvider.CheckAsync(nextcloud, CancellationToken.None);
        assert(unverified.Status == UpdateStatus.NewerReleaseKnown && unverified.ExecutionPlan is null,
            "A Nextcloud release without a GitHub SHA-256 digest was made installable.");
    }

    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };

    private sealed class FakeChocolatey : IChocolateyPackageService
    {
        public string? Latest { get; set; }
        public bool IsAvailable => true;
        public Task<ChocolateyPackageOffer> FindLatestAsync(IEnumerable<string> nameCandidates, string? installedVersion, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<string?> FindPackageVersionAsync(string packageId, CancellationToken token) => Task.FromResult(Latest);
        public Task<PreparedChocolateyUpdate> PrepareAsync(UpdateCheckResult update, CancellationToken token) => throw new NotSupportedException();
    }
}
