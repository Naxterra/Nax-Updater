using System.Buffers.Binary;
using System.Xml.Linq;
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
}
