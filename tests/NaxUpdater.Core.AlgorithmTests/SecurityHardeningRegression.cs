using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Xml.Linq;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

internal static class SecurityHardeningRegression
{
    public static async Task RunAsync(Action<bool, string> assert, string fixture)
    {
        programdata_protected_subfolder_is_accepted_despite_ancestor_write_attributes(assert, fixture);
        stale_protected_workspaces_older_than_boot_are_cleaned_up(assert, fixture);
        pe_architecture_reader_handles_valid_and_corrupt_executables_safely(assert, fixture);
        await github_private_asset_resolver_rejects_non_default_port_and_userinfo(assert);
        chocolatey_upgrade_arguments_require_installed_only_for_installed_packages(assert);
        user_agent_version_matches_app_project_version(assert);
        desktop_shell_job_is_allowed_only_without_kill_on_close_and_with_desktop_shell_member(assert);
        winget_com_interop_version_supports_local_rpc_elevation(assert);
        await electron_builder_falls_back_to_arch_win_manifest_and_verified_cdn(assert, fixture);
        await stale_fallback_catalog_does_not_mask_failed_higher_authority_feed(assert);
        await unresolved_non_store_msix_packages_are_cached_without_repeating_store_search(assert, fixture);
        await antigravity_official_recipe_matches_live_manifest_format(assert);
        await explicit_official_recipe_outranks_failing_generic_electron_metadata(assert, fixture);
    }

    private static void programdata_protected_subfolder_is_accepted_despite_ancestor_write_attributes(
        Action<bool, string> assert, string fixture)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        assert(PrivilegedUpdateHost.IsProtectedPath(programFiles, folder: true),
            "Program Files must be recognized as a protected folder.");
        assert(!PrivilegedUpdateHost.IsProtectedPath(programData, folder: true),
            "ProgramData itself allows standard-user writes and must not be accepted as a protected target folder.");
        assert(!PrivilegedUpdateHost.IsProtectedPath(fixture, folder: true),
            "A user-writable temporary folder must be rejected as a protected folder.");

        var packageCache = Path.Combine(programData, "Package Cache");
        if (Directory.Exists(packageCache))
        {
            assert(PrivilegedUpdateHost.IsProtectedPath(packageCache, folder: true),
                "An administrator-owned subfolder under ProgramData must be accepted despite ProgramData granting WriteAttributes to Users.");
        }
    }

    private static void stale_protected_workspaces_older_than_boot_are_cleaned_up(
        Action<bool, string> assert, string fixture)
    {
        var root = Directory.CreateDirectory(Path.Combine(fixture, "workspace-sweep")).FullName;
        var bootTime = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        var stale = Directory.CreateDirectory(Path.Combine(root, "NaxUpdater.Update." + Guid.NewGuid().ToString("N"))).FullName;
        var stagedFile = Path.Combine(stale, "staged.exe");
        File.WriteAllText(stagedFile, "old");
        File.SetAttributes(stagedFile, FileAttributes.ReadOnly);
        Directory.SetCreationTimeUtc(stale, bootTime.AddMinutes(-15));

        var current = Directory.CreateDirectory(Path.Combine(root, "NaxUpdater.Update." + Guid.NewGuid().ToString("N"))).FullName;
        Directory.SetCreationTimeUtc(current, bootTime.AddMinutes(5));

        var unrelated = Directory.CreateDirectory(Path.Combine(root, "KeepMe")).FullName;
        Directory.SetCreationTimeUtc(unrelated, bootTime.AddDays(-2));

        var malformed = Directory.CreateDirectory(Path.Combine(root, "NaxUpdater.Update.not-a-guid")).FullName;
        Directory.SetCreationTimeUtc(malformed, bootTime.AddDays(-2));

        PrivilegedUpdateHost.CleanupStaleWorkspaces(root, bootTime);

        assert(!Directory.Exists(stale), "A stale protected staging folder created before boot was not removed.");
        assert(Directory.Exists(current), "A protected staging folder created after boot was removed prematurely.");
        assert(Directory.Exists(unrelated), "An unrelated folder was removed during workspace cleanup.");
        assert(Directory.Exists(malformed), "A non-GUID folder matching only the prefix was removed during workspace cleanup.");
    }

    private static void pe_architecture_reader_handles_valid_and_corrupt_executables_safely(
        Action<bool, string> assert, string fixture)
    {
        var dir = Directory.CreateDirectory(Path.Combine(fixture, "pe-arch")).FullName;
        string WriteValidPe(string name, ushort machine)
        {
            var path = Path.Combine(dir, name);
            var bytes = new byte[0x90];
            bytes[0] = (byte)'M';
            bytes[1] = (byte)'Z';
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3c, 4), 0x80);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x80, 4), 0x00004550);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x84, 2), machine);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        assert(PeArchitectureReader.Detect(WriteValidPe("x64.exe", 0x8664)) == "x64", "Valid x64 PE executable was not detected.");
        assert(PeArchitectureReader.Detect(WriteValidPe("x86.exe", 0x014c)) == "x86", "Valid x86 PE executable was not detected.");
        assert(PeArchitectureReader.Detect(WriteValidPe("arm64.exe", 0xAA64)) == "arm64", "Valid ARM64 PE executable was not detected.");

        var truncated = Path.Combine(dir, "truncated.exe");
        File.WriteAllBytes(truncated, [(byte)'M', (byte)'Z', 0, 0]);
        assert(PeArchitectureReader.Detect(truncated) is null, "A truncated executable must return null without throwing.");

        var negativeOffset = Path.Combine(dir, "negative.exe");
        var negBytes = new byte[0x90];
        negBytes[0] = (byte)'M';
        negBytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(negBytes.AsSpan(0x3c, 4), -100);
        File.WriteAllBytes(negativeOffset, negBytes);
        assert(PeArchitectureReader.Detect(negativeOffset) is null, "A PE header with a negative offset must return null without throwing.");

        var hugeOffset = Path.Combine(dir, "huge.exe");
        var hugeBytes = new byte[0x90];
        hugeBytes[0] = (byte)'M';
        hugeBytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(hugeBytes.AsSpan(0x3c, 4), 0x10000);
        File.WriteAllBytes(hugeOffset, hugeBytes);
        assert(PeArchitectureReader.Detect(hugeOffset) is null, "A PE header pointing past EOF must return null without throwing.");

        var badSignature = Path.Combine(dir, "badsig.exe");
        var sigBytes = new byte[0x90];
        sigBytes[0] = (byte)'M';
        sigBytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(sigBytes.AsSpan(0x3c, 4), 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(sigBytes.AsSpan(0x80, 4), 0xDEADBEEF);
        BinaryPrimitives.WriteUInt16LittleEndian(sigBytes.AsSpan(0x84, 2), 0x8664);
        File.WriteAllBytes(badSignature, sigBytes);
        assert(PeArchitectureReader.Detect(badSignature) is null, "A file without the PE signature must not be treated as a valid executable.");
    }

    private static async Task github_private_asset_resolver_rejects_non_default_port_and_userinfo(Action<bool, string> assert)
    {
        var tokenRead = false;
        Task<string?> NeverReadToken(CancellationToken _)
        {
            tokenRead = true;
            return Task.FromResult<string?>("ghp_should_never_be_read");
        }

        foreach (var badUrl in new[]
        {
            "https://api.github.com:8443/repos/Naxterra/Nax-Copy/releases/assets/123",
            "https://attacker@api.github.com/repos/Naxterra/Nax-Copy/releases/assets/123"
        })
        {
            var rejected = false;
            try
            {
                await UpdatePackageDownloader.ResolveGitHubAssetAsync(new Uri(badUrl), CancellationToken.None, NeverReadToken);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            assert(rejected && !tokenRead, $"ResolveGitHubAssetAsync accepted unsafe URL {badUrl} or read the GitHub token.");
        }
    }

    private static void chocolatey_upgrade_arguments_require_installed_only_for_installed_packages(Action<bool, string> assert)
    {
        var installedArgs = ChocolateyPackageService.UpgradeArguments(new("git.install", "2.47.0"));
        var fallbackArgs = ChocolateyPackageService.UpgradeArguments(new("git.install", "2.47.0", AllowInstall: true));
        assert(installedArgs.Contains("--fail-on-not-installed", StringComparer.Ordinal),
            "Chocolatey upgrades for installed packages must pass --fail-on-not-installed.");
        assert(!fallbackArgs.Contains("--fail-on-not-installed", StringComparer.Ordinal),
            "Chocolatey fallback upgrades must omit --fail-on-not-installed so non-Chocolatey installations can update.");
    }

    private static void user_agent_version_matches_app_project_version(Action<bool, string> assert)
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "NaxUpdater.slnx")))
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("Repository root not found.");
        var csproj = XDocument.Load(Path.Combine(root, "src", "NaxUpdater", "NaxUpdater.csproj"));
        var projectVersion = csproj.Descendants("Version").FirstOrDefault()?.Value.Trim();
        assert(AppVersion.Current == projectVersion && AppVersion.UserAgent == "NaxUpdater/" + projectVersion,
            $"AppVersion ({AppVersion.Current}) does not match NaxUpdater.csproj Version ({projectVersion}).");
    }

    private static void desktop_shell_job_is_allowed_only_without_kill_on_close_and_with_desktop_shell_member(
        Action<bool, string> assert)
    {
        const uint breakawayOk = 0x00000800;
        const uint killOnClose = 0x00002000;
        assert(!UpdateHostLifetime.IsJobBoundForQuery(inJob: false, querySucceeded: false, limitFlags: 0, containsDesktopShell: false),
            "A process outside any job must not be reported as job-bound.");
        assert(!UpdateHostLifetime.IsJobBoundForQuery(inJob: true, querySucceeded: true, limitFlags: breakawayOk, containsDesktopShell: true),
            "A non-terminating desktop shell job containing explorer.exe must not be treated as a launcher job.");
        assert(UpdateHostLifetime.IsJobBoundForQuery(inJob: true, querySucceeded: true, limitFlags: killOnClose, containsDesktopShell: true),
            "A kill-on-close job must remain job-bound even if the desktop shell is present.");
        assert(UpdateHostLifetime.IsJobBoundForQuery(inJob: true, querySucceeded: true, limitFlags: 0, containsDesktopShell: false),
            "A non-kill nested job without the desktop shell must fail closed as job-bound.");
        assert(UpdateHostLifetime.IsJobBoundForQuery(inJob: true, querySucceeded: false, limitFlags: 0, containsDesktopShell: true),
            "An unreadable job object must fail closed as job-bound.");
    }

    private static void winget_com_interop_version_supports_local_rpc_elevation(Action<bool, string> assert)
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "NaxUpdater.slnx")))
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("Repository root not found.");
        var minimum = new Version(1, 29, 380);
        var versions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            var doc = XDocument.Load(project);
            foreach (var reference in doc.Descendants("PackageReference"))
            {
                if (!string.Equals(reference.Attribute("Include")?.Value, "Microsoft.WindowsPackageManager.ComInterop", StringComparison.OrdinalIgnoreCase))
                    continue;
                var raw = reference.Attribute("Version")?.Value?.Trim() ?? "";
                versions.Add(raw);
                var parsed = Version.TryParse(raw.Split('-')[0], out var version) ? version : new Version(0, 0, 0);
                assert(parsed >= minimum,
                    $"{Path.GetFileName(project)} references Microsoft.WindowsPackageManager.ComInterop {raw}, which predates the 1.29.380 local RPC elevation fix.");
            }
        }
        assert(versions.Count == 1, $"Projects reference inconsistent Microsoft.WindowsPackageManager.ComInterop versions: {string.Join(", ", versions)}.");
    }

    private static async Task electron_builder_falls_back_to_arch_win_manifest_and_verified_cdn(
        Action<bool, string> assert, string fixture)
    {
        var dir = Directory.CreateDirectory(Path.Combine(fixture, "electron-arch-channel")).FullName;
        var resources = Directory.CreateDirectory(Path.Combine(dir, "resources")).FullName;
        var exe = Path.Combine(dir, "FixtureApp.exe");
        var pe = new byte[0x90];
        pe[0] = (byte)'M';
        pe[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(0x3c, 4), 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x80, 4), 0x00004550);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0x84, 2), 0x8664);
        File.WriteAllBytes(exe, pe);
        File.WriteAllText(Path.Combine(resources, "app-update.yml"),
            "provider: generic\nurl: https://updater.example/manifest/\npublisherName:\n  - Example Publisher LLC\n");

        var sha512Bytes = Enumerable.Repeat((byte)0x5a, 64).ToArray();
        var sha512Base64 = Convert.ToBase64String(sha512Bytes);
        var sha512Hex = Convert.ToHexString(sha512Bytes);
        var manifest =
            "version: 2.35.1\nfiles:\n  - url: https://cdn.example/releases/FixtureApp-x64.exe\n" +
            $"    sha512: {sha512Base64}\npath: FixtureApp-x64.exe\nsha512: {sha512Base64}\n";
        using var client = new HttpClient(new StubHandler(req =>
            req.RequestUri!.AbsoluteUri == "https://updater.example/manifest/latest-x64-win.yml"
                ? new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = req, Content = new StringContent(manifest, Encoding.UTF8, "text/yaml") }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = req }));
        var app = new InstalledApplication("fixture:electron-arch", "FixtureApp 2.35.0", "Example Publisher", "2.35.0", "2.35.0",
            "fixture", exe, "fixture", null, null, InstallScope.CurrentUser, ManagementMode.Registry, ConfidenceLevel.High, false, [], null, []);
        var result = await new ElectronBuilderUpdateProvider(client).CheckAsync(app, CancellationToken.None);
        assert(result.Status == UpdateStatus.Available && result.AvailableVersion == "2.35.1" &&
               result.ExecutionPlan is { DownloadUri.AbsoluteUri: "https://cdn.example/releases/FixtureApp-x64.exe", ExpectedSigner: "Example Publisher LLC" } &&
               result.ExecutionPlan.Sha512 == sha512Hex &&
               result.ExecutionPlan.AllowedDownloadHosts.Contains("cdn.example", StringComparer.OrdinalIgnoreCase),
            "ElectronBuilderUpdateProvider did not fall back to latest-x64-win.yml or pin the verified CDN download host.");

        // A transient HTTP 503 on latest-x64-win.yml must fail as an HTTP error rather than
        // being swallowed as HTTP 404 Unsupported from latest.yml.
        using var outageClient = new HttpClient(new StubHandler(req =>
            req.RequestUri!.AbsoluteUri == "https://updater.example/manifest/latest-x64-win.yml"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = req }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = req }));
        var outageThrew = false;
        try { await new ElectronBuilderUpdateProvider(outageClient).CheckAsync(app, CancellationToken.None); }
        catch (HttpRequestException) { outageThrew = true; }
        assert(outageThrew, "A transient HTTP 503 on latest-x64-win.yml was swallowed as 404 Unsupported.");
    }

    private static async Task stale_fallback_catalog_does_not_mask_failed_higher_authority_feed(Action<bool, string> assert)
    {
        var app = new InstalledApplication("fixture:stale-mask", "SampleApp 2.35.0", "Vendor", "2.35.0", "2.35.0",
            "fixture", Environment.ProcessPath, "fixture", null, null, InstallScope.CurrentUser, ManagementMode.Registry, ConfidenceLevel.High, false, [], null, []);
        var deadFeed = new StubProvider("installed-updater-metadata",
            new(UpdateProviderAuthority.InstalledUpdateProtocol, 80, "fixture"),
            new UpdateCheckResult(app.Identity, app.DisplayName, app.NormalizedVersion, null, UpdateStatus.Unsupported,
                "installed-updater-metadata", "Installed metadata", "neutral", "fixture", "x64", "stable", null, "Feed returned 404.", null));
        var staleCatalog = new StubProvider("winget-fallback",
            new(UpdateProviderAuthority.FallbackCatalog, 50, "fixture"),
            new UpdateCheckResult(app.Identity, app.DisplayName, app.NormalizedVersion, null, UpdateStatus.Current,
                "winget-fallback", "WinGet fallback", "neutral", "fixture", "x64", "stable", null, "Catalog is older.", null,
                AvailabilityReason: UpdateAvailabilityReason.CatalogBehindInstalled));

        var combined = await new UpdateCheckService([deadFeed, staleCatalog]).CheckAsync(new(DateTimeOffset.UtcNow, [app], [], []));
        var selected = combined.Results.Single();
        assert(selected.ProviderId == "installed-updater-metadata" && selected.Status == UpdateStatus.Unsupported,
            "A stale fallback catalog masked a failed higher-authority update feed as Current.");

        var standalone = await new UpdateCheckService([staleCatalog]).CheckAsync(new(DateTimeOffset.UtcNow, [app], [], []));
        assert(standalone.Results.Single().Status == UpdateStatus.Current,
            "A standalone fallback catalog older than the installed version should still remain Current.");
    }

    private static async Task unresolved_non_store_msix_packages_are_cached_without_repeating_store_search(
        Action<bool, string> assert, string fixture)
    {
        var cachePath = Path.Combine(fixture, "store-unresolved-cache.json");
        var cache = new StoreSearchCache(cachePath);
        var store = new CountingStoreService(cacheable: true);
        var provider = new MsixStoreUpdateProvider(null, store, nativeStore: null, searchCache: cache);
        var app = new InstalledApplication("msix:NonStore.App_12345", "NonStore App", "Vendor", "1.0.0.0", "1.0.0.0",
            "fixture", null, "fixture", null, null, InstallScope.CurrentUser, ManagementMode.Msix, ConfidenceLevel.High, false, [], null, []);

        var first = await provider.CheckAsync(app, CancellationToken.None);
        var second = await provider.CheckAsync(app, CancellationToken.None);
        assert(first.Status == UpdateStatus.ManagedExternally && second.Status == UpdateStatus.ManagedExternally && store.Calls == 1,
            $"Cacheable unresolved MSIX lookup ran {store.Calls} times instead of 1.");

        var transientStore = new CountingStoreService(cacheable: false);
        var transientProvider = new MsixStoreUpdateProvider(null, transientStore, nativeStore: null, searchCache: cache);
        var app2 = app with { Identity = "msix:Transient.App_12345", DisplayName = "Transient App" };
        await transientProvider.CheckAsync(app2, CancellationToken.None);
        await transientProvider.CheckAsync(app2, CancellationToken.None);
        assert(transientStore.Calls == 2,
            $"Transient Store lookup failure was incorrectly cached (calls={transientStore.Calls}).");
    }

    private static async Task antigravity_official_recipe_matches_live_manifest_format(Action<bool, string> assert)
    {
        var catalogPath = Path.Combine(AppContext.BaseDirectory, "Configuration", "update-providers.json");
        var catalog = await UpdateProviderCatalogLoader.LoadAsync(catalogPath);
        var recipe = catalog.Official.Single(r => r.Id == "Google.Antigravity");
        var shaBytes = Enumerable.Repeat((byte)0x3c, 64).ToArray();
        var shaBase64 = Convert.ToBase64String(shaBytes);
        var shaHex = Convert.ToHexString(shaBytes).ToLowerInvariant();
        var body =
            "version: 2.35.1\r\nfiles:\r\n  - url: https://storage.googleapis.com/antigravity-public/antigravity-hub/2.35.1-123/windows-x64/Antigravity-x64.exe\r\n" +
            $"    sha512: {shaBase64}\r\n    size: 170272672\r\npath: Antigravity-x64.exe\r\nsha512: {shaBase64}\r\n";
        using var http = new HttpClient(new StubHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = req, Content = new StringContent(body, Encoding.UTF8, "text/yaml") }));
        var app = new InstalledApplication("fixture:ag", "Antigravity 2.35.0", "Google", "2.35.0", "2.35.0",
            "fixture", Environment.ProcessPath, "fixture", null, null, InstallScope.CurrentUser, ManagementMode.Registry, ConfidenceLevel.High, false, [], null, []);
        var provider = new OfficialSiteUpdateProvider(http, recipe);
        assert(provider.CanHandle(app), "Google.Antigravity recipe did not match Antigravity 2.35.0.");
        var result = await provider.CheckAsync(app, CancellationToken.None);
        assert(result.Status == UpdateStatus.Available && result.AvailableVersion == "2.35.1" &&
               result.ExecutionPlan is { ExpectedSigner: "Google LLC", RequiresElevation: false } &&
               result.ExecutionPlan.Sha512 == shaHex &&
               result.ExecutionPlan.DownloadUri?.Host == "storage.googleapis.com",
            "Google.Antigravity recipe did not produce a SHA-512 and Google LLC verified update plan.");
    }

    private static async Task explicit_official_recipe_outranks_failing_generic_electron_metadata(
        Action<bool, string> assert, string fixture)
    {
        // Reproduces the Antigravity case: installed app has an app-update.yml without
        // publisherName and an unsigned main binary, so generic ElectronBuilderUpdateProvider
        // blocks installation with UpdateStatus.Error. An explicit reviewed official recipe
        // (ProducerRelease, priority 95) must outrank generic app-update.yml discovery
        // (ProducerRelease, priority 80) and offer the verified update cleanly.
        var dir = Directory.CreateDirectory(Path.Combine(fixture, "electron-vs-official")).FullName;
        var resources = Directory.CreateDirectory(Path.Combine(dir, "resources")).FullName;
        var exe = Path.Combine(dir, "Antigravity.exe");
        var pe = new byte[0x90];
        pe[0] = (byte)'M';
        pe[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(0x3c, 4), 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x80, 4), 0x00004550);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0x84, 2), 0x8664);
        File.WriteAllBytes(exe, pe);
        File.WriteAllText(Path.Combine(resources, "app-update.yml"),
            "provider: generic\nurl: https://antigravity-hub-auto-updater-974169037036.us-central1.run.app/manifest/\n");

        var catalogPath = Path.Combine(AppContext.BaseDirectory, "Configuration", "update-providers.json");
        var catalog = await UpdateProviderCatalogLoader.LoadAsync(catalogPath);
        var recipe = catalog.Official.Single(r => r.Id == "Google.Antigravity");

        var shaBytes = Enumerable.Repeat((byte)0x4d, 64).ToArray();
        var shaBase64 = Convert.ToBase64String(shaBytes);
        var body =
            "version: 2.35.1\r\nfiles:\r\n  - url: https://storage.googleapis.com/antigravity-public/antigravity-hub/2.35.1-123/windows-x64/Antigravity-x64.exe\r\n" +
            $"    sha512: {shaBase64}\r\n    size: 170272672\r\npath: Antigravity-x64.exe\r\nsha512: {shaBase64}\r\n";
        using var http = new HttpClient(new StubHandler(req =>
            req.RequestUri!.AbsoluteUri.EndsWith("latest-x64-win.yml", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = req, Content = new StringContent(body, Encoding.UTF8, "text/yaml") }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = req }));

        var app = new InstalledApplication("fixture:ag-priority", "Antigravity 2.35.0", "Google", "2.35.0", "2.35.0",
            "fixture", exe, "fixture", null, null, InstallScope.CurrentUser, ManagementMode.Registry, ConfidenceLevel.High, false, [], null, []);
        var electronProvider = new ElectronBuilderUpdateProvider(http);
        var officialProvider = new OfficialSiteUpdateProvider(http, recipe);

        assert(officialProvider.Descriptor.Authority == UpdateProviderAuthority.ProducerRelease &&
               electronProvider.Descriptor.Authority == UpdateProviderAuthority.ProducerRelease &&
               officialProvider.Descriptor.Specificity > electronProvider.Descriptor.Specificity,
            "Explicit OfficialSiteUpdateProvider must outrank generic ElectronBuilderUpdateProvider.");

        var snapshot = await new UpdateCheckService([electronProvider, officialProvider])
            .CheckAsync(new(DateTimeOffset.UtcNow, [app], [], []));
        var selected = snapshot.Results.Single();
        assert(selected.Status == UpdateStatus.Available &&
               selected.ProviderId == "official:Google.Antigravity" &&
               selected.AvailableVersion == "2.35.1" &&
               selected.IsInstallable &&
               selected.ExecutionPlan is { ExpectedSigner: "Google LLC" },
            $"Expected official:Google.Antigravity Available update, got {selected.ProviderId} ({selected.Status}): {selected.Message}");

        // Once already at 2.35.1, official:Google.Antigravity confirms Current and skips
        // generic app-update.yml probing cleanly.
        var currentApp = app with { DisplayName = "Antigravity 2.35.1", InstalledVersion = "2.35.1", NormalizedVersion = "2.35.1" };
        var currentSnapshot = await new UpdateCheckService([electronProvider, officialProvider])
            .CheckAsync(new(DateTimeOffset.UtcNow, [currentApp], [], []));
        var currentResult = currentSnapshot.Results.Single();
        assert(currentResult.Status == UpdateStatus.Current &&
               currentResult.ProviderId == "official:Google.Antigravity" &&
               (currentResult.SourceChecks?.All(s => s.ProviderId != electronProvider.Id) ?? true),
            $"Expected official:Google.Antigravity Current without generic Electron fallback, got {currentResult.ProviderId} ({currentResult.Status}).");

        // When a higher-specificity official provider reports NewerReleaseKnown (for example
        // manual install or license acceptance), a failing skippable generic Electron feed
        // must not override it with UpdateStatus.Error.
        var knownOfficial = new StubProvider("official:Google.Antigravity",
            new(UpdateProviderAuthority.ProducerRelease, 95, "fixture"),
            new UpdateCheckResult(app.Identity, app.DisplayName, app.NormalizedVersion, "2.35.1",
                UpdateStatus.NewerReleaseKnown, "official:Google.Antigravity", "Official site",
                "neutral", "fixture", "x64", "stable", null, "Manual release.", null,
                AvailabilityReason: UpdateAvailabilityReason.ManualReleaseInstall));
        var knownSnapshot = await new UpdateCheckService([electronProvider, knownOfficial])
            .CheckAsync(new(DateTimeOffset.UtcNow, [app], [], []));
        var knownResult = knownSnapshot.Results.Single();
        assert(knownResult.Status == UpdateStatus.NewerReleaseKnown &&
               knownResult.ProviderId == "official:Google.Antigravity" &&
               (knownResult.SourceChecks?.All(s => s.ProviderId != electronProvider.Id) ?? true),
            $"Expected official:Google.Antigravity NewerReleaseKnown without generic Electron override, got {knownResult.ProviderId} ({knownResult.Status}).");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class StubProvider(string id, UpdateProviderDescriptor descriptor, UpdateCheckResult result) : IUpdateProvider
    {
        public string Id => id;
        public UpdateProviderDescriptor Descriptor => descriptor;
        public bool CanHandle(InstalledApplication application) => true;
        public Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class CountingStoreService(bool cacheable) : IStorePackageDeploymentService
    {
        public int Calls { get; private set; }
        public string? LastError => "No exact Store catalog product was correlated with the installed package family.";
        public Task<StoreUpdateAvailability> CheckForUpdateAsync(
            string packageFamilyName, string? installedDisplayName, string? installedPublisher,
            string? installedVersion, string? installedArchitecture, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new StoreUpdateAvailability(false, false, null, null, LastError, IsCacheableUnresolved: cacheable));
        }
        public Task<StoreCatalogIdentity?> ResolveAsync(string packageFamilyName, string? installedDisplayName, string? installedPublisher, CancellationToken cancellationToken) =>
            Task.FromResult<StoreCatalogIdentity?>(null);
        public Task<UpdateExecutionResult> InstallOrUpdateAsync(string productId, string packageFamilyName, string? installedDisplayName, string? installedPublisher, string expectedVersion, CancellationToken cancellationToken) =>
            Task.FromResult(new UpdateExecutionResult(0, true, null));
    }
}
