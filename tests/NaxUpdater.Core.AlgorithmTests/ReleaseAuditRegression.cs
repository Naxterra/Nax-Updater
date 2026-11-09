using System.Diagnostics;
using System.Net;
using System.Text;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

// Findings of an independent review of the whole project at 0.17.64, fixed on 0.17.71.
// Each scenario is one method.
internal static class ReleaseAuditRegression
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        version_suffix_is_a_prerelease_only_when_it_says_so(assert);
        signer_must_be_the_whole_certificate_name(assert);
        chocolatey_target_with_shell_characters_is_refused(assert);
        await driver_named_package_is_not_installable_from_a_fallback_catalog(assert);
        await uninstaller_that_hands_over_to_a_child_is_waited_for(assert);
        await kept_segments_of_a_replaced_server_file_are_not_reused(assert);
        cache_sweep_keeps_only_current_offers_and_every_driver_package(assert);
        await unavailable_github_api_does_not_report_current_git_as_outdated(assert);
        await server_answer_without_length_is_read_only_up_to_the_limit(assert);
        // Findings of the second review of the whole project, fixed after the Store timeout release.
        await removal_wait_ends_although_a_left_behind_process_keeps_running(assert);
        await relocated_uninstaller_is_waited_for_beyond_the_limit_for_other_processes(assert);
        prerelease_named_after_other_words_is_older_than_its_release(assert);
        await database_driver_and_driver_tool_are_not_device_drivers(assert);
        cache_folder_name_is_the_one_windows_stores(assert);
        cache_sweep_does_not_delete_through_a_link(assert);
        app_that_stopped_below_the_offered_version_is_not_reported_as_updated(assert);
    }

    private static async Task removal_wait_ends_although_a_left_behind_process_keeps_running(Action<bool, string> assert)
    {
        // The uninstaller was canceled: its entry stays, and a process that it started keeps running.
        using var uninstaller = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c start \"\" /b ping -n 9 127.0.0.1")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
        })!;
        var timer = Stopwatch.StartNew();
        await ApplicationRemovalService.WaitForUninstallerAsync(uninstaller, static () => true, CancellationToken.None,
            leftBehindLimit: TimeSpan.FromSeconds(1.5));
        assert(timer.Elapsed < TimeSpan.FromSeconds(6),
            $"The removal waited for a left-behind process beyond its limit: {timer.Elapsed.TotalSeconds:N1} s");
    }

    private static async Task relocated_uninstaller_is_waited_for_beyond_the_limit_for_other_processes(Action<bool, string> assert)
    {
        // Like an NSIS uninstaller whose copy the user leaves open: the left-behind process is the
        // uninstaller itself, so the limit for helpers and browsers does not end the wait.
        using var stub = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c start \"\" /b ping -n 5 127.0.0.1")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
        })!;
        var timer = Stopwatch.StartNew();
        await ApplicationRemovalService.WaitForUninstallerAsync(stub, static () => true, CancellationToken.None,
            leftBehindLimit: TimeSpan.FromSeconds(1),
            isUninstaller: static path => path is not null && Path.GetFileName(path).Equals("ping.exe", StringComparison.OrdinalIgnoreCase));
        assert(timer.Elapsed > TimeSpan.FromSeconds(2.5) && timer.Elapsed < TimeSpan.FromSeconds(30),
            $"The removal did not wait for the relocated uninstaller until it ended: {timer.Elapsed.TotalSeconds:N1} s");
        var registered = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Fixture", "uninstall.exe");
        assert(ApplicationRemovalService.IsUninstallerProgram(Path.Combine(Path.GetTempPath(), "~nsuA.tmp", "Un_A.exe"), registered) &&
               ApplicationRemovalService.IsUninstallerProgram(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), registered) &&
               ApplicationRemovalService.IsUninstallerProgram(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Fixture", "bin", "Uninstall.exe"), registered) &&
               !ApplicationRemovalService.IsUninstallerProgram(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Browser", "browser.exe"), registered) &&
               !ApplicationRemovalService.IsUninstallerProgram(null, registered),
            "A copy in the temp folder, Windows Installer or the registered uninstaller was not taken for the uninstaller, or a browser or an unreadable path was.");
        assert(string.Equals(ProcessDescendants.ImagePath(Environment.ProcessId), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase),
            "The program path of a running process could not be read.");
    }

    private static void prerelease_named_after_other_words_is_older_than_its_release(Action<bool, string> assert)
    {
        (string Installed, string Release)[] prereleases =
        [
            ("3.0 Release Candidate 1", "3.0"), ("3.0 Technical Preview", "3.0"), ("3.0 Public Beta 2", "3.0"), ("3.0-CTP1", "3.0"),
            ("3.0-prealpha", "3.0"), ("1.2.3-x64-beta1", "1.2.3")
        ];
        var notOlder = prereleases.Where(static pair => VersionOrder.Compare(pair.Installed, pair.Release) >= 0).Select(static pair => pair.Installed).ToArray();
        assert(notOlder.Length == 0, $"These prereleases counted as their finished release, which was then never offered: {string.Join(", ", notOlder)}");
        assert(VersionOrder.Compare("3.0 Release Candidate 1", "3.0 RC1") == 0 && VersionOrder.Compare("3.0 Release Candidate 2", "3.0-rc1") > 0 &&
               VersionOrder.Compare("3.0 Public Beta 2", "3.0 RC1") < 0,
            "The same prerelease in two spellings was ordered differently, or a beta was not older than a release candidate.");
        // "b456" is a build number: the release must not be offered as an update of itself.
        assert(VersionOrder.Compare("1.2.3", "1.2.3 b456") == 0 && VersionOrder.Compare("155.0b5", "155.0") < 0 && VersionOrder.Compare("3.13.0b12", "3.13.0") < 0,
            "A build number after a letter counted as a beta, or a numbered beta no longer did.");
    }

    private static async Task database_driver_and_driver_tool_are_not_device_drivers(Action<bool, string> assert)
    {
        string[] programs =
        [
            "Microsoft ODBC Driver 18 for SQL Server", "MongoDB ODBC Driver", "PostgreSQL JDBC Driver", "Display Driver Uninstaller", "Driver Booster 12",
            "Driver Easy 6.1", "Driver Genius 24", "Intel® Driver & Support Assistant", "Windows Driver Kit", "Screwdriver Tool"
        ];
        // A brand or a common word in a device driver's name must not make it a program.
        string[] drivers =
        [
            "Realtek Audio Driver", "Intel Graphics Drivers", "NVIDIA Grafiktreiber 621.14", "Samsung USB Driver for Mobile Phones",
            "Genius WebCam Driver", "Lenovo Easy Camera Driver", "USB Serial Driver Kit", "Printer Driver Updater Service"
        ];
        var blocked = programs.Where(ExternalManagementClassifier.IsDriverPackageName).ToArray();
        var allowed = drivers.Where(static name => !ExternalManagementClassifier.IsDriverPackageName(name)).ToArray();
        assert(blocked.Length == 0 && allowed.Length == 0,
            $"Programs taken for device drivers: {string.Join(", ", blocked)}. Device drivers taken for programs: {string.Join(", ", allowed)}.");
        InstalledApplication App(string name) => new("registry:" + name, name, "Vendor", "1.0.0", "1.0.0", "fixture",
            null, "fixture", null, null, InstallScope.Machine, ManagementMode.Registry, ConfidenceLevel.High, false, [], null, []);
        var snapshot = await new UpdateCheckService([new Catalog()]).CheckAsync(new(DateTimeOffset.UtcNow, [App(programs[0])], [], []));
        assert(snapshot.Results.Single().IsInstallable, "A database driver lost its update from a package catalog.");
    }

    private static void cache_folder_name_is_the_one_windows_stores(Action<bool, string> assert)
    {
        assert(UpdatePackageDownloader.SanitizePathSegment("1.2.") == "1.2" && UpdatePackageDownloader.SanitizePathSegment("1.2 ") == "1.2" &&
               UpdatePackageDownloader.SanitizePathSegment("..") == "unknown" && UpdatePackageDownloader.SanitizePathSegment(".") == "unknown" &&
               UpdatePackageDownloader.SanitizePathSegment("2.0.0") == "2.0.0",
            "A version that ends in a dot or a space, or that is only dots, did not become the folder name that Windows stores.");
        var cache = Path.Combine(Path.GetTempPath(), "nax-folder-" + Guid.NewGuid().ToString("N"));
        try
        {
            var installer = Path.Combine(Directory.CreateDirectory(Path.Combine(cache, "official_Vendor.App", "2.0.0")).FullName, "setup.exe");
            File.WriteAllText(installer, "payload");
            UpdatePayloadCache.Sweep(cache, [Result(Plan([1])) with { AvailableVersion = "2.0.0." }]);
            assert(File.Exists(installer), "The cache sweep removed the download of a current offer whose version ends in a dot.");
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }

    private static void cache_sweep_does_not_delete_through_a_link(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), "nax-link-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(root, "cache");
        var outside = Path.Combine(root, "outside");
        // One junction stands where a source folder would, one where a version folder would.
        string[] links = [Path.Combine(cache, "linked-source"), Path.Combine(cache, "official_Vendor.App", "1.0.0")];
        try
        {
            Directory.CreateDirectory(Path.Combine(cache, "official_Vendor.App"));
            var document = Path.Combine(Directory.CreateDirectory(Path.Combine(outside, "documents")).FullName, "letter.txt");
            File.WriteAllText(document, "text");
            foreach (var link in links)
            {
                // A junction needs no administrator rights.
                using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{outside}\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                })!;
                mklink.WaitForExit();
            }
            assert(links.All(static link => Directory.Exists(link) && File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint)),
                "The junctions of the link scenario could not be created.");
            UpdatePayloadCache.Sweep(cache, []);
            assert(File.Exists(document), "The cache sweep deleted a folder outside the cache through a link.");
        }
        finally
        {
            // Deleting a junction itself removes the link only.
            foreach (var link in links) if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void app_that_stopped_below_the_offered_version_is_not_reported_as_updated(Action<bool, string> assert)
    {
        // The offer is 1.0.0 to 2.0.0. Before the action runs, the app is found current at another version.
        var offer = Result(Plan([1]));
        UpdateTransactionResult FoundCurrentAt(string installed) => new(UpdateTransactionStage.NoLongerApplicable,
            offer with { InstalledVersion = installed, Status = UpdateStatus.Current, ExecutionPlan = null }, null, null);
        assert(UpdateTransactionCoordinator.CompletedElsewhere(FoundCurrentAt("2.0.0"), offer) &&
               UpdateTransactionCoordinator.CompletedElsewhere(FoundCurrentAt("2.1.0"), offer) &&
               !UpdateTransactionCoordinator.CompletedElsewhere(FoundCurrentAt("1.5.0"), offer) &&
               !UpdateTransactionCoordinator.CompletedElsewhere(FoundCurrentAt("1.0.0"), offer) &&
               UpdateTransactionCoordinator.CompletedElsewhere(FoundCurrentAt("1.5.0"), offer with { AvailableVersion = null }),
            "An app that had not reached the offered version was reported as updated, or one that had reached it was not.");
    }

    private static void version_suffix_is_a_prerelease_only_when_it_says_so(Action<bool, string> assert)
    {
        // An installed version with a patch letter, a build note or an edition word is not older
        // than the same numbers without it.
        (string Installed, string Listed)[] notOlder =
        [
            ("7.1a", "7.1"), ("1.0.2a", "1.0.2"), ("2.6.12-I001", "2.6.12"), ("8.10.1_3", "8.10.1"), ("1.2.3-stable", "1.2.3"),
            ("1.2.3-hotfix1", "1.2.3"), ("3.0 SP1", "3.0"), ("1.2.3 (64-bit)", "1.2.3"), ("6.2.11 (52896)", "6.2.11"), ("1.2.3.r2", "1.2.3"), ("9.0u1", "9.0")
        ];
        var wronglyOlder = notOlder.Where(static pair => VersionOrder.Compare(pair.Installed, pair.Listed) < 0).Select(static pair => pair.Installed).ToArray();
        assert(wronglyOlder.Length == 0, $"These installed versions counted as older than their own release: {string.Join(", ", wronglyOlder)}");
        // Real prereleases stay older, and patch levels order among themselves.
        assert(VersionOrder.Compare("155.0b5", "155.0") < 0 && VersionOrder.Compare("2.0.0-alpha.47", "2.0.0") < 0 &&
               VersionOrder.Compare("1.9.0-beta.1", "1.9.0") < 0 && VersionOrder.Compare("3.13.0a1", "3.13.0") < 0 &&
               VersionOrder.Compare("2.0.0-rc1", "2.0.0-beta2") > 0 && VersionOrder.Compare("1.2.3-canary", "1.2.3") < 0,
            "A prerelease was no longer older than its release.");
        assert(VersionOrder.Compare("1.0.2b", "1.0.2a") > 0 && VersionOrder.Compare("3.0 SP2", "3.0 SP1") > 0 && VersionOrder.Compare("7.1a", "7.2") < 0,
            "Patch levels were not ordered among themselves or against the next release.");
        // "24.1.2" and "24.01.02" are the same date, so both sort the same way against "231105".
        assert(VersionOrder.Compare("231105", "24.01.02") < 0 && VersionOrder.Compare("231105", "24.1.2") < 0 && VersionOrder.Compare("24.1.2", "24.01.02") == 0,
            "A dotted release date ordered differently with and without leading zeros.");
    }

    private static void signer_must_be_the_whole_certificate_name(Action<bool, string> assert)
    {
        string[] names = ["Microsoft Corporation", "Microsoft Corporation"];
        string[] rejected = ["o", "soft Corp", "Microsoft", "Redmond", "C=US", "Microsoft Corporation Ltd", ""];
        var accepted = rejected.Where(expected => NativeAuthenticodeVerifier.SignerMatches(names, expected)).ToArray();
        assert(accepted.Length == 0, $"A part of the certificate subject was accepted as the signer: {string.Join(", ", accepted.Select(static value => $"'{value}'"))}");
        assert(NativeAuthenticodeVerifier.SignerMatches(names, "microsoft corporation") &&
               NativeAuthenticodeVerifier.SignerMatches(["GOG  sp. z o.o."], "GOG sp. z o.o") &&
               NativeAuthenticodeVerifier.SignerMatches(["Example Software", "Example Holding GmbH"], "Example Holding GmbH"),
            "The whole subject name or organization was not accepted (letter case, repeated spaces and a final period do not count).");
    }

    private static void chocolatey_target_with_shell_characters_is_refused(Action<bool, string> assert)
    {
        assert(ChocolateyPackageService.IsSafeTarget(new("git.install", "2.47.0")) && ChocolateyPackageService.IsSafeTarget(new("7zip", "24.9.0-beta1+build.5")) &&
               !ChocolateyPackageService.IsSafeTarget(new("git.install", "1.0\" & calc & rem \"")) &&
               !ChocolateyPackageService.IsSafeTarget(new("git & calc", "1.0")) && !ChocolateyPackageService.IsSafeTarget(new("git", "1.0 > file")) &&
               !ChocolateyPackageService.IsSafeTarget(new("git\n", "1.0")) && !ChocolateyPackageService.IsSafeTarget(new("git", "1.0\n")),
            "A Chocolatey package ID or version with shell characters or a final line feed was accepted for the elevated command, or a normal one was refused.");
    }

    private static async Task driver_named_package_is_not_installable_from_a_fallback_catalog(Action<bool, string> assert)
    {
        InstalledApplication App(string name) => new("registry:" + name, name, "Vendor", "1.0.0", "1.0.0", "fixture",
            null, "fixture", null, null, InstallScope.Machine, ManagementMode.Registry, ConfidenceLevel.High, false, [], null, []);
        var snapshot = await new UpdateCheckService([new Catalog()]).CheckAsync(
            new(DateTimeOffset.UtcNow, [App("Realtek Audio Driver"), App("NVIDIA Grafiktreiber 621.14"), App("Fixture App")], [], []));
        var driver = snapshot.Results.Single(static result => result.DisplayName == "Realtek Audio Driver");
        var german = snapshot.Results.Single(static result => result.DisplayName.StartsWith("NVIDIA", StringComparison.Ordinal));
        var application = snapshot.Results.Single(static result => result.DisplayName == "Fixture App");
        assert(driver is { IsInstallable: false, ExecutionPlan: null, Status: UpdateStatus.NewerReleaseKnown } && !german.IsInstallable && application.IsInstallable,
            $"A package named as a driver became installable through a fallback catalog, or an application lost its offer: {driver.Status} · {german.Status} · {application.Status}");
    }

    private static async Task uninstaller_that_hands_over_to_a_child_is_waited_for(Action<bool, string> assert)
    {
        // Like an NSIS uninstaller: the first process starts a second one and exits at once.
        static Process HandOver() => Process.Start(new ProcessStartInfo("cmd.exe", "/d /c start \"\" /b ping -n 4 127.0.0.1")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
        })!;
        using var registered = HandOver();
        var timer = Stopwatch.StartNew();
        await ApplicationRemovalService.WaitForUninstallerAsync(registered, static () => true, CancellationToken.None);
        var waited = timer.Elapsed;
        // Once the uninstall entry is gone, a process the uninstaller left behind is not waited for.
        using var removed = HandOver();
        timer.Restart();
        await ApplicationRemovalService.WaitForUninstallerAsync(removed, static () => false, CancellationToken.None);
        var returned = timer.Elapsed;
        assert(waited > TimeSpan.FromSeconds(2) && waited < TimeSpan.FromSeconds(30) && returned < TimeSpan.FromSeconds(2),
            $"The removal did not wait for the process its uninstaller started, or waited although the entry was gone: {waited.TotalSeconds:N1} s · {returned.TotalSeconds:N1} s");
    }

    private static async Task kept_segments_of_a_replaced_server_file_are_not_reused(Action<bool, string> assert)
    {
        var cache = Path.Combine(Path.GetTempPath(), "nax-segments-" + Guid.NewGuid().ToString("N"));
        var first = Enumerable.Range(0, 6000).Select(static index => (byte)(index % 251)).ToArray();
        var second = Enumerable.Range(0, 6000).Select(static index => (byte)(index % 241)).ToArray();
        string? error = null;
        try
        {
            var server = new RangeServer { Body = first, ETag = "\"one\"", FailFrom = 5000 };
            using var http = new HttpClient(server);
            var downloader = new UpdatePackageDownloader(http, new Signer(), segmentedDownloadThresholdBytes: 1000);
            var interrupted = false;
            try { (await downloader.DownloadAndVerifyAsync(Result(Plan(first)), cache)).Dispose(); }
            catch (HttpRequestException) { interrupted = true; }
            var kept = Directory.GetFiles(cache, "*.segment?", SearchOption.AllDirectories).Count(static file => char.IsAsciiDigit(file[^1]));
            // The publisher replaces the file; five segments of the old one are still on disk.
            server.Body = second;
            server.ETag = "\"two\"";
            server.FailFrom = null;
            using var installer = await downloader.DownloadAndVerifyAsync(Result(Plan(second)), cache);
            if (!interrupted || kept != 5) error = $"the first download was not interrupted as planned (interrupted {interrupted}, kept {kept})";
            else if (!File.ReadAllBytes(installer.Path).AsSpan().SequenceEqual(second)) error = "the file differs from the new server file";
        }
        catch (Exception exception) { error = exception.Message; }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
        assert(error is null, $"Segments of a replaced server file were merged into the new download: {error}");
    }

    private static void cache_sweep_keeps_only_current_offers_and_every_driver_package(Action<bool, string> assert)
    {
        var cache = Path.Combine(Path.GetTempPath(), "nax-sweep-" + Guid.NewGuid().ToString("N"));
        try
        {
            string Add(string provider, string version)
            {
                var folder = Directory.CreateDirectory(Path.Combine(cache, provider, version)).FullName;
                File.WriteAllText(Path.Combine(folder, "setup.exe"), "payload");
                return folder;
            }
            var current = Add("official_Vendor.App", "2.0.0");
            var superseded = Add("official_Vendor.App", "1.9.0");
            var installed = Add("github_o_r", "3.1.0");
            var driver = Add("manufacturer-driver_nvidia", "617.42");
            UpdatePayloadCache.Sweep(cache, [Result(Plan([1]))]);
            assert(Directory.Exists(current) && !Directory.Exists(superseded) && !Directory.Exists(installed) &&
                   !Directory.Exists(Path.Combine(cache, "github_o_r")) && File.Exists(Path.Combine(driver, "setup.exe")),
                "The cache sweep removed a current offer or a driver package, or kept an installer that no offer needs.");
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }

    private static async Task unavailable_github_api_does_not_report_current_git_as_outdated(Action<bool, string> assert)
    {
        // The API answers 403 (rate limit); the release page redirects to the newest tag.
        using var http = new HttpClient(new Respond(static request => request.RequestUri!.Host == "api.github.com"
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://github.com/git-for-windows/git/releases/tag/v2.47.0.windows.1")
            }));
        var recipe = new GitHubUpdateRecipe
        {
            Id = "Git.Git", DisplayName = "Git", PublisherContains = "Git", Repository = "git-for-windows/git",
            AssetNamePattern = @"^Git-(?<version>[0-9]+(?:\.[0-9]+)+)-64-bit\.exe$", Architecture = "x64", ExpectedSigner = "Johannes Schindelin",
            InstallerKind = UpdateExecutionKind.DownloadedExe, InstallerArguments = ["/VERYSILENT"]
        };
        var provider = new GitHubReleaseUpdateProvider(http, recipe, static _ => Task.FromResult<string?>(null));
        var git = new InstalledApplication("fixture:git", "Git", "The Git Development Community", "2.47.0", "2.47.0", "fixture",
            Environment.ProcessPath, "fixture", null, null, InstallScope.Machine, ManagementMode.Registry, ConfidenceLevel.High, false, [], null, []);
        var result = await provider.CheckAsync(git, CancellationToken.None);
        assert(result.Status == UpdateStatus.Current, $"A current Git for Windows was not current while the GitHub API was unavailable: {result.Status} · {result.Message}");
    }

    private static async Task server_answer_without_length_is_read_only_up_to_the_limit(Action<bool, string> assert)
    {
        static HttpContent Body(int bytes) => new StreamContent(new MemoryStream(new byte[bytes]));
        var within = await HttpBodyLimit.ReadBytesAsync(Body(1000), CancellationToken.None, 1000);
        Exception? failure = null;
        try { await HttpBodyLimit.ReadBytesAsync(Body(200_000), CancellationToken.None, 1000); }
        catch (Exception exception) { failure = exception; }
        var text = await HttpBodyLimit.ReadStringAsync(new ByteArrayContent([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("Größe")]), CancellationToken.None);
        assert(within.Length == 1000 && failure is InvalidDataException && text == "Größe",
            $"A server answer was read beyond its limit, or a normal answer was not read correctly: {within.Length} · {failure?.GetType().Name ?? "no error"} · {text}");
    }

    private static UpdateExecutionPlan Plan(byte[] payload) => new(UpdateExecutionKind.DownloadedExe, new Uri("https://vendor.example/setup.exe"), "setup.exe",
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)), "Vendor Inc", null, ["/S"], true, ["vendor.example"], []);

    private static UpdateCheckResult Result(UpdateExecutionPlan plan) => new("registry:fixture", "Fixture App", "1.0.0", "2.0.0",
        UpdateStatus.Available, "official:Vendor.App", "Official", "neutral", "fixture", "x64", "stable", null, "fixture", plan,
        CorrelationKey: "fixture");

    // A fallback catalog that offers an installable update for every application.
    private sealed class Catalog : IUpdateProvider
    {
        public string Id => "catalog-fixture";
        public UpdateProviderDescriptor Descriptor { get; } = new(UpdateProviderAuthority.FallbackCatalog, 50, "fixture");
        public bool CanHandle(InstalledApplication application) => true;
        public Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken token) => Task.FromResult(new UpdateCheckResult(
            application.Identity, application.DisplayName, application.NormalizedVersion, "2.0.0", UpdateStatus.Available, Id, Id, "neutral", "fixture",
            "x64", "stable", null, "fixture",
            new UpdateExecutionPlan(UpdateExecutionKind.NativeCommand, null, null, null, "Fixture", @"C:\Fixture\updater.exe", [], false, [], [])));
    }

    private sealed class Signer : IAuthenticodeVerifier
    {
        public AuthenticodeVerificationResult Verify(string filePath, string expectedSigner) => new(true, expectedSigner, null);
    }

    private sealed class Respond(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = respond(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    // Serves one file with byte ranges and an ETag; a range that starts at FailFrom breaks off.
    private sealed class RangeServer : HttpMessageHandler
    {
        public byte[] Body { get; set; } = [];
        public string ETag { get; set; } = "";
        public long? FailFrom { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            HttpResponseMessage response;
            if (request.Headers.Range?.Ranges.SingleOrDefault() is { From: { } from, To: { } to })
            {
                if (from == FailFrom) throw new HttpRequestException("The connection was reset.");
                response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Body[(int)from..((int)to + 1)]) };
                response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, Body.Length);
            }
            else
            {
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Body) };
                response.Headers.AcceptRanges.Add("bytes");
            }
            response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(ETag);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
