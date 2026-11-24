using System.ComponentModel;
using System.Diagnostics;
using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

// Found distinguishes "Chocolatey has no information about this application at
// all" (Found: false — the overwhelming common case: games, drivers, redistributables,
// enterprise tools with no community package) from "Chocolatey recognizes the
// package and confirms the installed version is current" (Found: true, Target:
// null). Only the former is non-definitive; conflating the two previously made
// "not on Chocolatey" surface as a scary per-app Error on nearly a third of a
// real inventory.
public sealed record ChocolateyPackageOffer(ChocolateyUpdateTarget? Target, bool Found, string? Error, bool CatalogBehindInstalled = false);

public interface IChocolateyPackageService
{
    bool IsAvailable { get; }
    Task<ChocolateyPackageOffer> FindLatestAsync(IEnumerable<string> nameCandidates, string? installedVersion, CancellationToken token);
    Task<string?> FindPackageVersionAsync(string packageId, CancellationToken token);
    Task<PreparedChocolateyUpdate> PrepareAsync(UpdateCheckResult update, CancellationToken token);

    // Installed packages with their dependencies (lib\<id>\<id>.nuspec).
    IReadOnlyDictionary<string, ChocolateyPackageDetails> InstalledPackages() =>
        new Dictionary<string, ChocolateyPackageDetails>(StringComparer.OrdinalIgnoreCase);

    // A package version (the newest when version is null) with its dependencies.
    async Task<ChocolateyPackageDetails?> FindPackageDetailsAsync(string packageId, string? version, CancellationToken token) =>
        version is not null ? new(packageId, version, [])
        : await FindPackageVersionAsync(packageId, token) is { } latest ? new(packageId, latest, []) : null;
}

// Chocolatey packages apply themselves through an embedded PowerShell install
// script (chocolateyinstall.ps1). Only Chocolatey's own engine is trusted to run
// that script; NaxUpdater shells out to the installed choco.exe for both
// detection and apply rather than reimplementing package execution itself.
// This mirrors the trust boundary WinGet's own COM engine provides for its path,
// except here there is no structured API, so choco.exe itself is required.
public sealed class ChocolateyPackageService : IChocolateyPackageService
{
    // NaxUpdater is a long-running desktop app; a user can install Chocolatey
    // (or PATH/ChocolateyInstall can become valid) after the app already
    // started. Cache a found path forever (it won't disappear mid-session),
    // but keep re-probing on every call while it's still missing, so the
    // fallback tier doesn't stay dark for the rest of the process's life.
    private static string? _cachedExecutablePath;
    private static string? ResolveExecutablePath() => _cachedExecutablePath ??= FindChocoExecutable();
    private static readonly ProcessQueryRunner ProcessRunner = new();
    // Concurrent choco.exe invocations contend for Chocolatey's own local
    // lock/cache directory and slow each other down rather than running in
    // parallel (observed: a full scan went from ~30s to ~80s once Chocolatey
    // was actually installed and every candidate app queried it at once).
    // Cap concurrency the same way the Store broker path already does.
    private static readonly SemaphoreSlim QuerySlots = new(8, 8);

    public bool IsAvailable => ResolveExecutablePath() is not null;

    // choco.exe lives in <install root>\bin.
    internal static string? InstallRoot() =>
        ResolveExecutablePath() is { } exe ? Path.GetDirectoryName(Path.GetDirectoryName(exe)) : null;

    public async Task<string?> FindPackageVersionAsync(string packageId, CancellationToken token) =>
        IsAvailable
            ? (await SearchExactAsync(packageId, token)).FirstOrDefault(match =>
                match.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase))?.Version
            : null;

    public IReadOnlyDictionary<string, ChocolateyPackageDetails> InstalledPackages() =>
        ChocolateyDependencyPlanner.ReadInstalled(InstallRoot());

    public async Task<ChocolateyPackageDetails?> FindPackageDetailsAsync(string packageId, string? version, CancellationToken token)
    {
        if (!IsAvailable) return null;
        if (UsesOnlyCommunityFeed())
        {
            var url = version is null
                ? $"{CommunityFeed}Packages()?$filter={Uri.EscapeDataString($"tolower(Id) eq '{packageId.ToLowerInvariant().Replace("'", "''")}' and IsLatestVersion")}"
                : $"{CommunityFeed}Packages(Id='{Uri.EscapeDataString(packageId.Replace("'", "''"))}',Version='{Uri.EscapeDataString(version.Replace("'", "''"))}')";
            return (await QueryCommunityFeedAsync(url, token))
                .FirstOrDefault(entry => entry.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase));
        }
        // choco.exe search output carries no dependencies.
        if (version is not null) return new(packageId, version, null);
        return await FindPackageVersionAsync(packageId, token) is { } latest ? new(packageId, latest, null) : null;
    }

    public async Task<ChocolateyPackageOffer> FindLatestAsync(
        IEnumerable<string> nameCandidates, string? installedVersion, CancellationToken token)
    {
        if (!IsAvailable)
        {
            return new(null, false, "Chocolatey (choco.exe) is not installed.");
        }

        // Each candidate costs a real network round-trip through choco.exe (no
        // local index like WinGet's). Most apps in a typical inventory have no
        // Chocolatey package at all, so trying every candidate unconditionally
        // was multiplying an already-expensive call by up to 3x for no benefit;
        // stop at the first candidate that resolves to exactly one package.
        ChocolateyUpdateTarget? found = null;
        foreach (var name in nameCandidates)
        {
            token.ThrowIfCancellationRequested();
            var matches = await SearchExactAsync(name, token);
            if (matches.Count == 1)
            {
                found = matches[0];
                break;
            }
        }
        if (found is null)
        {
            // Not an error: Chocolatey simply has no package for this application,
            // true for most of a typical inventory (games, drivers, redistributables).
            return new(null, false, null);
        }
        if (string.IsNullOrWhiteSpace(installedVersion))
        {
            // A name match without a comparable installed version is not evidence
            // of currency - claiming Current here would silently hide whatever
            // Chocolatey actually offers. Report as unresolved instead.
            return new(null, false, null);
        }
        var comparison = VersionOrder.Compare(found.Version, installedVersion);
        if (comparison <= 0)
        {
            return new(null, true, null, CatalogBehindInstalled: comparison < 0);
        }
        return new(found, true, null);
    }

    public async Task<PreparedChocolateyUpdate> PrepareAsync(UpdateCheckResult update, CancellationToken token)
    {
        var target = update.ExecutionPlan?.ChocolateyTarget
            ?? throw new InvalidOperationException("The Chocolatey update target is missing.");
        if (!IsSafeTarget(target))
            throw new InvalidOperationException("The Chocolatey package ID or version contains characters that are not allowed, so the update was not started.");
        if (!IsAvailable)
        {
            throw new InvalidOperationException("Chocolatey (choco.exe) is no longer available.");
        }
        // Re-verify the offer is still current before approving execution.
        var current = (await SearchExactAsync(target.PackageId, token))
            .FirstOrDefault(match => match.PackageId.Equals(target.PackageId, StringComparison.OrdinalIgnoreCase));
        if (current is null || current.Version != target.Version)
        {
            throw new InvalidOperationException("Chocolatey no longer offers the approved package version.");
        }
        return new PreparedChocolateyUpdate(target, (_, cancellationToken) => RunElevatedUpgradeAsync(target, cancellationToken));
    }

    private const string CommunityFeed = "https://community.chocolatey.org/api/v2/";
    // A stalled feed connection timed out whole scans' worth of lookups at 15 s.
    // Each attempt gets 8 s; the retry goes through a second handler, whose own
    // connection pool makes it open a fresh connection instead of reusing the stalled one.
    private static readonly HttpClient FeedClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly HttpClient FeedRetryClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly TimeSpan FeedAttemptTimeout = TimeSpan.FromSeconds(8);

    // choco.exe startup made every search cost ~2-4 s and made Chocolatey the
    // longest part of a scan. `search --exact --by-id-only` is one OData query
    // against the configured feed, so when the public community feed is the only
    // enabled source, ask it directly. Any other source setup (private,
    // authenticated, extra feeds) keeps using choco.exe. Applying always does.
    private static bool UsesOnlyCommunityFeed()
    {
        try
        {
            var exe = ResolveExecutablePath();
            var root = exe is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(exe));
            var config = root is null ? null : Path.Combine(root, "config", "chocolatey.config");
            if (config is null || !File.Exists(config)) return false;
            using var reader = System.Xml.XmlReader.Create(config, new System.Xml.XmlReaderSettings
                { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
            var enabled = System.Xml.Linq.XDocument.Load(reader).Descendants("source")
                .Where(static source => !string.Equals((string?)source.Attribute("disabled"), "true", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return enabled.Length == 1 &&
                   string.Equals(((string?)enabled[0].Attribute("value"))?.TrimEnd('/') + "/", CommunityFeed, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return false;
        }
    }

    private static async Task<List<ChocolateyUpdateTarget>> SearchCommunityFeedAsync(string name, CancellationToken token)
    {
        // The feed's Id comparison is case-sensitive; choco's --exact is not.
        var filter = Uri.EscapeDataString($"tolower(Id) eq '{name.ToLowerInvariant().Replace("'", "''")}' and IsLatestVersion");
        return (await QueryCommunityFeedAsync($"{CommunityFeed}Packages()?$filter={filter}", token))
            .Where(item => item.Id.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(static item => new ChocolateyUpdateTarget(item.Id, item.Version))
            .ToList();
    }

    // A feed query answers with an Atom feed of entries; a Packages(Id,Version) lookup with one entry.
    private static Task<List<ChocolateyPackageDetails>> QueryCommunityFeedAsync(string url, CancellationToken token) =>
        QueryCommunityFeedAsync(FeedClient, FeedRetryClient, url, FeedAttemptTimeout, token);

    internal static async Task<List<ChocolateyPackageDetails>> QueryCommunityFeedAsync(
        HttpClient client, HttpClient retryClient, string url, TimeSpan attemptTimeout, CancellationToken token)
    {
        try { return await QueryCommunityFeedOnceAsync(client, url, attemptTimeout, token); }
        catch (TimeoutException)
        {
            try { return await QueryCommunityFeedOnceAsync(retryClient, url, attemptTimeout, token); }
            catch (TimeoutException) { throw new TimeoutException(
                $"The Chocolatey community feed did not answer within {attemptTimeout.TotalSeconds:0} seconds, twice, on separate connections."); }
        }
    }

    private static async Task<List<ChocolateyPackageDetails>> QueryCommunityFeedOnceAsync(
        HttpClient client, string url, TimeSpan attemptTimeout, CancellationToken token)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
        attempt.CancelAfter(attemptTimeout);
        try { return await ParseCommunityFeedAsync(client, url, attempt.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException(); }
    }

    private static async Task<List<ChocolateyPackageDetails>> ParseCommunityFeedAsync(HttpClient client, string url, CancellationToken token)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return [];
        response.EnsureSuccessStatusCode();
        using var reader = System.Xml.XmlReader.Create(await HttpBodyLimit.ReadStreamAsync(response.Content, token), new System.Xml.XmlReaderSettings
            { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, Async = true });
        var document = await System.Xml.Linq.XDocument.LoadAsync(reader, System.Xml.Linq.LoadOptions.None, token);
        System.Xml.Linq.XNamespace atom = "http://www.w3.org/2005/Atom";
        System.Xml.Linq.XNamespace metadata = "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata";
        System.Xml.Linq.XNamespace data = "http://schemas.microsoft.com/ado/2007/08/dataservices";
        var entries = document.Root is null ? []
            : document.Root.Name == atom + "entry" ? [document.Root] : document.Root.Elements(atom + "entry").ToList();
        return entries
            .Select(entry => (Id: entry.Element(atom + "title")?.Value.Trim(),
                Properties: entry.Element(metadata + "properties")))
            .Select(item => (item.Id, Version: item.Properties?.Element(data + "Version")?.Value.Trim(),
                Dependencies: item.Properties?.Element(data + "Dependencies")?.Value))
            .Where(static item => item.Id is { Length: > 0 } && item.Version is { Length: > 0 })
            .Select(static item => new ChocolateyPackageDetails(item.Id!, item.Version!,
                ChocolateyDependencyPlanner.ParseFeedDependencies(item.Dependencies)))
            .ToList();
    }

    private static async Task<List<ChocolateyUpdateTarget>> SearchExactAsync(string name, CancellationToken token)
    {
        if (UsesOnlyCommunityFeed()) return await SearchCommunityFeedAsync(name, token);
        var results = new List<ChocolateyUpdateTarget>();
        var output = await RunAsync(["search", name, "--exact", "--by-id-only", "-r", "--limit-output"], token);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            var parts = trimmed.Split('|');
            if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
            {
                results.Add(new(parts[0].Trim(), parts[1].Trim()));
            }
        }
        return results;
    }

    private static async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        await QuerySlots.WaitAsync(token);
        try
        {
            var exe = ResolveExecutablePath() ?? throw new InvalidOperationException("Chocolatey (choco.exe) is not installed.");
            // choco.exe search hits Chocolatey's community feed over the network,
            // a known source of hangs when the feed is slow or unreachable; use
            // the shared runner so a stalled call is killed and reported instead
            // of hanging the scan slot until overall cancellation.
            var result = await ProcessRunner.RunAsync(exe, arguments, TimeSpan.FromSeconds(30), token);
            return result.StandardOutput;
            // StandardError is intentionally not surfaced for read-only search queries.
        }
        finally
        {
            QuerySlots.Release();
        }
    }

    // The package ID and version come from a feed and end up in an elevated cmd.exe command line,
    // so only the characters of NuGet IDs and versions are accepted. A value such as
    // 1.0" & calc & rem " would otherwise run a second command as administrator. The pattern
    // ends with \z, because $ also matches before a line feed at the end of the value.
    private static readonly System.Text.RegularExpressions.Regex SafeToken =
        new(@"^[A-Za-z0-9][A-Za-z0-9._+\-]*\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static bool IsSafeTarget(ChocolateyUpdateTarget target) =>
        !target.PackageId.Equals("all", StringComparison.OrdinalIgnoreCase) &&
        SafeToken.IsMatch(target.PackageId) && SafeToken.IsMatch(target.Version);

    private static Task<UpdateExecutionResult> RunElevatedUpgradeAsync(ChocolateyUpdateTarget target, CancellationToken token)
    {
        if (InstallRoot() is not { } detected ||
            !string.Equals(Path.TrimEndingDirectorySeparator(detected), Path.TrimEndingDirectorySeparator(MachineInstallRoot()), StringComparison.OrdinalIgnoreCase))
            throw new UpdateExecutionRefusedException("Install Chocolatey for all users in a protected folder before using it for elevated updates.");
        return Environment.IsPrivilegedProcess
            ? Task.Run(() => ApplyElevatedAsync(target), token)
            : PrivilegedUpdateHost.RunChocolateyAsync(target, token);
    }

    internal static IReadOnlyList<string> UpgradeArguments(ChocolateyUpdateTarget target) =>
        target.AllowInstall
            ? ["upgrade", target.PackageId, "--version", target.Version, "-y", "-r", "--no-progress"]
            : ["upgrade", target.PackageId, "--version", target.Version, "-y", "-r", "--no-progress", "--fail-on-not-installed"];

    private static string MachineInstallRoot() =>
        Environment.GetEnvironmentVariable("ChocolateyInstall", EnvironmentVariableTarget.Machine)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey");

    internal static async Task<UpdateExecutionResult> ApplyElevatedAsync(ChocolateyUpdateTarget target)
    {
        if (!Environment.IsPrivilegedProcess)
            throw new UpdateExecutionRefusedException("The Chocolatey update requires administrator rights.");
        if (!IsSafeTarget(target))
            throw new UpdateExecutionRefusedException("The Chocolatey package ID or version contains characters that are not allowed, so the update was not started.");
        string root = MachineInstallRoot();
        string exe = Path.Combine(root, "bin", "choco.exe");
        if (!Path.IsPathFullyQualified(root) || !PrivilegedUpdateHost.IsProtectedPath(root, folder: true) ||
            !PrivilegedUpdateHost.IsProtectedPath(exe))
            throw new UpdateExecutionRefusedException("Install Chocolatey for all users in a protected folder before using it for elevated updates.");
        foreach (string folder in new[] { "bin", "lib", "helpers", "config", "extensions" })
        {
            var path = Path.Combine(root, folder);
            if (Directory.Exists(path) && !PrivilegedUpdateHost.IsProtectedTree(path))
                throw new UpdateExecutionRefusedException($"Chocolatey's {folder} directory is writable by a standard user.");
        }
        var startInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.SystemDirectory,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in UpgradeArguments(target))
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["ChocolateyInstall"] = root;
        using var temporary = PrivilegedUpdateHost.CreateProtectedWorkspace();
        PrivilegedUpdateHost.SetProtectedEnvironment(startInfo, temporary.Path);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Chocolatey could not be started.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(process.WaitForExitAsync(), output, errors);
        var log = output.Result + Environment.NewLine + errors.Result;
        if (process.ExitCode is not (0 or 1641 or 3010))
            return temporary.Finish(new UpdateExecutionResult(process.ExitCode, false,
                $"Chocolatey upgrade failed (exit code {process.ExitCode}): {LastMeaningfulLine(log)}"));

        // A dependency pin can make Chocolatey report success for a different version.
        var installed = ChocolateyDependencyPlanner.ReadInstalled(root);
        if (RecordMismatch(installed, target.PackageId, target.Version) is { } rootMismatch)
            return temporary.Finish(new UpdateExecutionResult(process.ExitCode, false, $"{rootMismatch}: {LastMeaningfulLine(log)}"));
        if (target.PayloadPackageId is { } payload && target.PayloadVersion is { } payloadVersion &&
            RecordMismatch(installed, payload, payloadVersion) is { } payloadMismatch)
            return temporary.Finish(new UpdateExecutionResult(process.ExitCode, false, $"{payloadMismatch}: {LastMeaningfulLine(log)}"));
        return temporary.Finish(new UpdateExecutionResult(process.ExitCode, true, null));
    }

    internal static string? RecordMismatch(IReadOnlyDictionary<string, ChocolateyPackageDetails> installed, string packageId, string version) =>
        !installed.TryGetValue(packageId, out var package)
            ? $"Chocolatey reported success, but it has no record of {packageId}"
            : VersionOrder.Compare(package.Version, version) != 0
                ? $"Chocolatey reported success, but {packageId} is still {package.Version} instead of {version}"
                : null;

    private static string LastMeaningfulLine(string log)
    {
        var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.LastOrDefault(static line => line.Length > 0) ?? "no output was captured.";
    }

    private static string? FindChocoExecutable()
    {
        var installRoot = Environment.GetEnvironmentVariable("ChocolateyInstall");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(installRoot))
        {
            candidates.Add(Path.Combine(installRoot, "bin", "choco.exe"));
        }
        candidates.Add(@"C:\ProgramData\chocolatey\bin\choco.exe");
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "choco.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entries are skipped.
            }
        }
        return null;
    }
}
