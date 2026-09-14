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
public sealed record ChocolateyPackageOffer(ChocolateyUpdateTarget? Target, bool Found, string? Error);

public interface IChocolateyPackageService
{
    bool IsAvailable { get; }
    Task<ChocolateyPackageOffer> FindLatestAsync(IEnumerable<string> nameCandidates, string? installedVersion, CancellationToken token);
    Task<PreparedChocolateyUpdate> PrepareAsync(UpdateCheckResult update, CancellationToken token);
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
        if (VersionOrder.Compare(found.Version, installedVersion) <= 0)
        {
            return new(null, true, null);
        }
        return new(found, true, null);
    }

    public async Task<PreparedChocolateyUpdate> PrepareAsync(UpdateCheckResult update, CancellationToken token)
    {
        var target = update.ExecutionPlan?.ChocolateyTarget
            ?? throw new InvalidOperationException("The Chocolatey update target is missing.");
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

    private static async Task<List<ChocolateyUpdateTarget>> SearchExactAsync(string name, CancellationToken token)
    {
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

    private static async Task<UpdateExecutionResult> RunElevatedUpgradeAsync(ChocolateyUpdateTarget target, CancellationToken token)
    {
        var exe = ResolveExecutablePath() ?? throw new InvalidOperationException("Chocolatey (choco.exe) is no longer available.");
        var logPath = Path.Combine(Path.GetTempPath(), $"naxupdater-choco-{Guid.NewGuid():N}.log");
        try
        {
            // Elevation (Verb=runas) requires ShellExecute, which cannot redirect
            // stdout/stderr directly. Route Chocolatey's own output to a log file
            // via cmd.exe instead so the result is still diagnosable.
            var innerCommand = $"\"{exe}\" upgrade {target.PackageId} --version=\"{target.Version}\" -y -r --no-progress > \"{logPath}\" 2>&1";
            var startInfo = new ProcessStartInfo("cmd.exe", $"/d /c \"{innerCommand}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Chocolatey could not be started.");
            await process.WaitForExitAsync(CancellationToken.None); // Windows owns the elevated deployment once started.
            var log = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath, token) : "";
            // Chocolatey exit codes: 0 = success, 1641/3010 = success, reboot needed.
            var success = process.ExitCode is 0 or 1641 or 3010;
            return new UpdateExecutionResult(process.ExitCode, success,
                success ? null : $"Chocolatey upgrade failed (exit code {process.ExitCode}): {LastMeaningfulLine(log)}");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return new UpdateExecutionResult(1223, false, "The Windows elevation prompt was canceled.");
        }
        finally
        {
            try { if (File.Exists(logPath)) File.Delete(logPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

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
