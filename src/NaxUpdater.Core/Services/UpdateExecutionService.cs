using NaxUpdater.Core.Models;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace NaxUpdater.Core.Services;

public sealed class UpdateExecutionService
{
    private readonly IStorePackageDeploymentService _storePackageDeploymentService;
    private readonly IAuthenticodeVerifier _authenticodeVerifier;

    public UpdateExecutionService(
        IAuthenticodeVerifier? authenticodeVerifier = null,
        IStorePackageDeploymentService? storePackageDeploymentService = null)
    {
        _authenticodeVerifier = authenticodeVerifier ?? new NativeAuthenticodeVerifier();
        _storePackageDeploymentService = storePackageDeploymentService ?? new StorePackageDeploymentService();
    }

    public IReadOnlyList<string> FindRunningProcesses(UpdateCheckResult update)
    {
        var plan = update.ExecutionPlan;
        if (plan is null)
        {
            return [];
        }
        using var currentProcess = Process.GetCurrentProcess();
        var currentSession = currentProcess.SessionId;
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var processName in plan.RunningProcessNames)
        {
            try
            {
                var processes = Process.GetProcessesByName(processName);
                var found = false;
                foreach (var process in processes)
                {
                    using (process)
                    {
                        try
                        {
                            if (!process.HasExited && process.Id != currentProcess.Id && process.SessionId == currentSession)
                            {
                                var matches = ProcessMatchesPlan(plan, process, out var identityKnown);
                                found |= matches || !identityKnown;
                            }
                        }
                        catch
                        {
                            // Ignore processes that exit while their state is inspected.
                        }
                    }
                }
                if (found)
                {
                    running.Add(processName);
                }
            }
            catch
            {
                // An inaccessible process is treated as not safely closable by NaxUpdater.
            }
        }
        return running.Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task<ApplicationCloseResult> CloseForUpdateAsync(
        UpdateCheckResult update,
        TimeSpan gracefulTimeout,
        TimeSpan forcedTimeout,
        CancellationToken cancellationToken = default)
    {
        var plan = update.ExecutionPlan;
        if (plan is null || plan.RunningProcessNames.Count == 0)
        {
            return new ApplicationCloseResult(true, false, []);
        }

        using var updaterProcess = Process.GetCurrentProcess();
        var updaterProcessId = updaterProcess.Id;
        var currentSession = updaterProcess.SessionId;

        var closeRequested = false;
        foreach (var processName in plan.RunningProcessNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                continue;
            }
            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        if (!process.HasExited && process.Id != updaterProcessId &&
                            process.SessionId == currentSession && process.MainWindowHandle != IntPtr.Zero &&
                            ProcessMatchesPlan(plan, process, out _))
                        {
                            closeRequested |= process.CloseMainWindow();
                        }
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
                    {
                        // A process without an accessible main window cannot be closed gracefully here.
                    }
                }
            }
        }

        var gracefulDeadline = DateTimeOffset.UtcNow + gracefulTimeout;
        var remaining = FindRunningProcesses(update);
        while (remaining.Count > 0 && DateTimeOffset.UtcNow < gracefulDeadline)
        {
            await Task.Delay(250, cancellationToken);
            remaining = FindRunningProcesses(update);
        }

        if (remaining.Count == 0)
        {
            return new ApplicationCloseResult(true, false, []);
        }

        var forcedTerminationUsed = false;
        foreach (var processName in plan.RunningProcessNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                continue;
            }
            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        if (!process.HasExited && process.Id != updaterProcessId && process.SessionId == currentSession &&
                            ProcessMatchesPlan(plan, process, out _))
                        {
                            process.Kill(entireProcessTree: true);
                            forcedTerminationUsed = true;
                        }
                    }
                    catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        // Do not pass a stale PID across a UAC boundary. An inaccessible process
                        // remains in the final verification result and blocks the update safely.
                    }
                }
            }
        }

        var forcedDeadline = DateTimeOffset.UtcNow + forcedTimeout;
        remaining = FindRunningProcesses(update);
        while (remaining.Count > 0 && DateTimeOffset.UtcNow < forcedDeadline)
        {
            await Task.Delay(250, cancellationToken);
            remaining = FindRunningProcesses(update);
        }

        return new ApplicationCloseResult(closeRequested, forcedTerminationUsed, remaining);
    }

    public async Task<PreparedUpdateExecution> PrepareAsync(
        UpdateCheckResult update,
        VerifiedInstaller? installer,
        CancellationToken cancellationToken = default)
    {
        var plan = update.ExecutionPlan ?? throw new InvalidOperationException("The update has no execution plan.");
        if (plan.Kind == UpdateExecutionKind.StorePackage)
        {
            var availability = await _storePackageDeploymentService.CheckForUpdateAsync(
                plan.StorePackageFamilyName ?? string.Empty,
                update.DisplayName,
                plan.StorePublisher,
                update.InstalledVersion,
                update.Architecture,
                cancellationToken);
            if (!availability.IsResolved ||
                !availability.IsUpdateAvailable ||
                !string.Equals(availability.ProductId, plan.StoreProductId, StringComparison.Ordinal) ||
                !string.Equals(availability.AvailableVersion, update.AvailableVersion, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    availability.Error ?? "Microsoft Store no longer exposes the exact version-bound update prepared for this package family.");
            }
            return new PreparedUpdateExecution(installer, null, null, null, null);
        }

        if (plan.Kind == UpdateExecutionKind.NativeCommand)
        {
            if (string.IsNullOrWhiteSpace(plan.NativeExecutable) || !File.Exists(plan.NativeExecutable))
            {
                throw new InvalidOperationException("The native update provider executable is missing.");
            }
            var nativeExecutable = plan.NativeExecutable;
            var nativeWorkingDirectory = plan.NativeWorkingDirectory;
            var nativeLocks = AcquirePreparedContentLocks(nativeExecutable, null);
            try
            {
                if (plan.RequiresElevation)
                {
                    var signature = _authenticodeVerifier.Verify(nativeExecutable, plan.ExpectedSigner!);
                    if (!signature.IsValid)
                    {
                        throw new InvalidDataException(signature.Error ?? "The elevated native updater signature is invalid.");
                    }
                }
                var nativeHash = await PreparedContentHashAsync(nativeExecutable, null, cancellationToken);
                return new PreparedUpdateExecution(
                    null,
                    nativeExecutable,
                    nativeWorkingDirectory,
                    null,
                    nativeHash,
                    nativeLocks);
            }
            catch
            {
                DisposePreparedLocks(nativeLocks);
                throw;
            }
        }

        if (installer is null || !File.Exists(installer.Path))
        {
            throw new InvalidOperationException("The verified installer is missing.");
        }

        string? extractionDirectory = null;
        var executableInstallerPath = installer.Path;
        IReadOnlyList<PreparedContentLock>? contentLocks = null;
        try
        {
            if (plan.Kind is UpdateExecutionKind.DownloadedZipMsi or UpdateExecutionKind.DownloadedZipDriver)
            {
                if (string.IsNullOrWhiteSpace(plan.NestedInstallerRelativePath))
                {
                    throw new InvalidOperationException("The verified archive does not identify its nested installer.");
                }
                extractionDirectory = Path.Combine(Path.GetTempPath(), "NaxUpdater", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(extractionDirectory);
                if (plan.Kind == UpdateExecutionKind.DownloadedZipDriver)
                {
                    var driverPayload = ExtractAndVerifyDriverPackage(
                        installer.Path,
                        plan.NestedInstallerRelativePath,
                        extractionDirectory,
                        plan.ExpectedHardwareId,
                        update.AvailableVersion,
                        plan.ExpectedSigners ?? []);
                    executableInstallerPath = driverPayload.InfPath;
                    contentLocks = driverPayload.ContentLocks;
                }
                else
                {
                    var nestedPayload = ExtractNestedInstaller(
                        installer.Path,
                        plan.NestedInstallerRelativePath,
                        extractionDirectory);
                    executableInstallerPath = nestedPayload.Path;
                    contentLocks = nestedPayload.ContentLocks;
                }
                if (plan.Kind == UpdateExecutionKind.DownloadedZipMsi && plan.RequireAuthenticode)
                {
                    var signers = ExpectedSigners(plan);
                    if (signers.Count == 0 || !signers.Any(signer =>
                            _authenticodeVerifier.Verify(executableInstallerPath, signer).IsValid))
                    {
                        throw new InvalidDataException("The nested MSI did not match an approved Authenticode publisher.");
                    }
                }
            }
            contentLocks ??= AcquirePreparedContentLocks(executableInstallerPath, extractionDirectory);
            var contentHash = await PreparedContentHashAsync(
                executableInstallerPath,
                extractionDirectory,
                cancellationToken);
            return new PreparedUpdateExecution(
                installer,
                executableInstallerPath,
                Path.GetDirectoryName(executableInstallerPath),
                extractionDirectory,
                contentHash,
                contentLocks);
        }
        catch
        {
            DisposePreparedLocks(contentLocks);
            CleanupPreparedDirectory(extractionDirectory);
            throw;
        }
    }

    public async Task<UpdateExecutionResult> ExecuteAsync(
        UpdateCheckResult update,
        VerifiedInstaller? installer,
        CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareAsync(update, installer, cancellationToken);
        return await ExecutePreparedAsync(update, prepared, cancellationToken);
    }

    public void DiscardPrepared(PreparedUpdateExecution prepared)
    {
        prepared.Installer?.Dispose();
        DisposePreparedLocks(prepared.ContentLocks);
        CleanupPreparedDirectory(prepared.CleanupDirectory);
    }

    public async Task<UpdateExecutionResult> ExecutePreparedAsync(
        UpdateCheckResult update,
        PreparedUpdateExecution prepared,
        CancellationToken cancellationToken = default)
    {
        var plan = update.ExecutionPlan ?? throw new InvalidOperationException("The update has no execution plan.");
        var running = FindRunningProcesses(update);
        if (running.Count > 0)
        {
            throw new ApplicationStillRunningException(running);
        }

        if (plan.Kind == UpdateExecutionKind.StorePackage)
        {
            if (string.IsNullOrWhiteSpace(plan.StorePackageFamilyName))
            {
                return new UpdateExecutionResult(-1, false, "The Microsoft Store product identity is incomplete.");
            }
            var identity = string.IsNullOrWhiteSpace(plan.StoreProductId)
                ? await _storePackageDeploymentService.ResolveAsync(
                    plan.StorePackageFamilyName,
                    update.DisplayName,
                    plan.StorePublisher,
                    cancellationToken)
                : new StoreCatalogIdentity(plan.StoreProductId, update.DisplayName, plan.StorePackageFamilyName, true);
            if (identity is null)
            {
                return new UpdateExecutionResult(-1, false, _storePackageDeploymentService.LastError ?? "No exact Microsoft Store product matched the installed package family.");
            }
            return await _storePackageDeploymentService.InstallOrUpdateAsync(
                identity.ProductId,
                plan.StorePackageFamilyName,
                update.DisplayName,
                plan.StorePublisher,
                update.AvailableVersion!,
                cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(prepared.ExecutablePath) || !File.Exists(prepared.ExecutablePath))
        {
            DiscardPrepared(prepared);
            throw new InvalidOperationException("The prepared executable update payload is missing.");
        }
        if (prepared.ContentLocks is not { Count: > 0 })
        {
            DiscardPrepared(prepared);
            throw new InvalidOperationException("The prepared update payload is not protected against replacement.");
        }
        string currentHash;
        try
        {
            currentHash = await PreparedContentHashAsync(
                prepared.ExecutablePath,
                prepared.CleanupDirectory,
                cancellationToken);
        }
        catch
        {
            DiscardPrepared(prepared);
            throw;
        }
        if (!string.Equals(currentHash, prepared.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            DiscardPrepared(prepared);
            throw new InvalidDataException("The prepared update payload changed after verification.");
        }

        if (plan.Kind == UpdateExecutionKind.NativeCommand)
        {
            try
            {
                if (plan.RequiresElevation || !string.IsNullOrWhiteSpace(prepared.WorkingDirectory))
                {
                    var nativeStartInfo = new ProcessStartInfo(prepared.ExecutablePath)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = string.IsNullOrWhiteSpace(prepared.WorkingDirectory)
                            ? Path.GetDirectoryName(prepared.ExecutablePath) ?? string.Empty
                            : prepared.WorkingDirectory
                    };
                    if (plan.RequiresElevation)
                    {
                        nativeStartInfo.Verb = "runas";
                    }
                    foreach (var argument in plan.Arguments)
                    {
                        nativeStartInfo.ArgumentList.Add(argument);
                    }
                    return await RunStartedProcessAsync(nativeStartInfo, cancellationToken);
                }
                var query = await new ProcessQueryRunner().RunAsync(
                    prepared.ExecutablePath,
                    plan.Arguments,
                    TimeSpan.FromMinutes(20),
                    cancellationToken);
                return new UpdateExecutionResult(query.ExitCode, IsSuccessfulExitCode(query.ExitCode), query.StandardError.Trim());
            }
            finally
            {
                prepared.Installer?.Dispose();
                DisposePreparedLocks(prepared.ContentLocks);
                CleanupPreparedDirectory(prepared.CleanupDirectory);
            }
        }

        var startInfo = plan.Kind switch
        {
            UpdateExecutionKind.DownloadedExe => new ProcessStartInfo(prepared.ExecutablePath),
            UpdateExecutionKind.DownloadedMsi or UpdateExecutionKind.DownloadedZipMsi =>
                new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe")),
            UpdateExecutionKind.DownloadedZipDriver =>
                new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe")),
            _ => throw new InvalidOperationException($"Unsupported execution kind {plan.Kind}.")
        };
        startInfo.UseShellExecute = true;
        if (plan.RequiresElevation)
        {
            startInfo.Verb = "runas";
        }
        if (plan.Kind is UpdateExecutionKind.DownloadedMsi or UpdateExecutionKind.DownloadedZipMsi)
        {
            startInfo.ArgumentList.Add("/i");
            startInfo.ArgumentList.Add(prepared.ExecutablePath);
        }
        else if (plan.Kind == UpdateExecutionKind.DownloadedZipDriver)
        {
            startInfo.ArgumentList.Add("/add-driver");
            startInfo.ArgumentList.Add(prepared.ExecutablePath);
            startInfo.ArgumentList.Add("/install");
        }
        foreach (var argument in plan.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            return await RunStartedProcessAsync(startInfo, cancellationToken);
        }
        finally
        {
            prepared.Installer?.Dispose();
            DisposePreparedLocks(prepared.ContentLocks);
            CleanupPreparedDirectory(prepared.CleanupDirectory);
        }
    }

    private static async Task<UpdateExecutionResult> RunStartedProcessAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The updater process did not start.");
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Cancellation stops NaxUpdater from promising a result; it must not delete staged
                // files out from under an installer that Windows has already started.
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            return new UpdateExecutionResult(process.ExitCode, IsSuccessfulExitCode(process.ExitCode), null);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return new UpdateExecutionResult(1223, false, "The Windows elevation prompt was cancelled.");
        }
    }

    private static IReadOnlyList<string> ExpectedSigners(UpdateExecutionPlan plan) =>
        plan.ExpectedSigners is { Count: > 0 }
            ? plan.ExpectedSigners.Where(static signer => !string.IsNullOrWhiteSpace(signer)).ToArray()
            : string.IsNullOrWhiteSpace(plan.ExpectedSigner)
                ? []
                : [plan.ExpectedSigner];

    private static async Task<string> PreparedContentHashAsync(
        string executablePath,
        string? contentRoot,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = !string.IsNullOrWhiteSpace(contentRoot) && Directory.Exists(contentRoot)
            ? Directory.GetFiles(contentRoot, "*", SearchOption.AllDirectories)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [executablePath];
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var label = !string.IsNullOrWhiteSpace(contentRoot)
                ? Path.GetRelativePath(contentRoot, path).Replace(Path.DirectorySeparatorChar, '/')
                : Path.GetFileName(path);
            hash.AppendData(Encoding.UTF8.GetBytes(label.ToUpperInvariant()));
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[128 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }
                hash.AppendData(buffer, 0, read);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static IReadOnlyList<PreparedContentLock> AcquirePreparedContentLocks(
        string executablePath,
        string? contentRoot)
    {
        var paths = !string.IsNullOrWhiteSpace(contentRoot) && Directory.Exists(contentRoot)
            ? Directory.GetFiles(contentRoot, "*", SearchOption.AllDirectories)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [executablePath];
        var locks = new List<PreparedContentLock>(paths.Length);
        try
        {
            foreach (var path in paths)
            {
                locks.Add(new PreparedContentLock(
                    path,
                    new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)));
            }
            return locks;
        }
        catch
        {
            DisposePreparedLocks(locks);
            throw;
        }
    }

    private static void DisposePreparedLocks(IEnumerable<PreparedContentLock>? locks)
    {
        if (locks is null)
        {
            return;
        }
        foreach (var item in locks)
        {
            item.Dispose();
        }
    }

    private static void CleanupPreparedDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A completed update is more important than best-effort temporary cleanup.
        }
    }

    internal static PreparedNestedPayload ExtractNestedInstaller(
        string archivePath,
        string relativePath,
        string destinationDirectory)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0 || normalized.Split('/').Any(static part => part is "" or "." or ".."))
        {
            throw new InvalidDataException("The nested installer path is unsafe.");
        }
        using var archive = ZipFile.OpenRead(archivePath);
        var matches = archive.Entries
            .Where(entry => entry.FullName.Replace('\\', '/').Equals(normalized, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1 || !matches[0].Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The exact nested MSI was not found in the verified archive.");
        }
        var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(matches[0].Name));
        var contentLock = ExtractEntryLocked(matches[0], destinationPath);
        return new PreparedNestedPayload(destinationPath, [contentLock]);
    }

    internal PreparedDriverPayload ExtractAndVerifyDriverPackage(
        string archivePath,
        string relativeInfPath,
        string destinationDirectory,
        string? expectedHardwareId,
        string? expectedDriverVersion,
        IReadOnlyList<string> expectedCatalogSigners)
    {
        var normalized = relativeInfPath.Replace('\\', '/').TrimStart('/');
        var parts = normalized.Split('/');
        if (parts.Length < 2 || parts.Any(static part => part is "" or "." or "..") ||
            !parts[^1].EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The nested driver INF path is unsafe.");
        }

        var directory = string.Join('/', parts[..^1]) + "/";
        var infName = parts[^1];
        using var archive = ZipFile.OpenRead(archivePath);
        var directoryEntries = archive.Entries
            .Where(entry => entry.FullName.Replace('\\', '/').StartsWith(directory, StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(entry.Name) &&
                            !entry.FullName.Replace('\\', '/')[directory.Length..].Contains('/'))
            .ToArray();
        var infEntries = directoryEntries
            .Where(entry => entry.Name.Equals(infName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (infEntries.Length != 1)
        {
            throw new InvalidDataException("The exact nested driver INF was not found in the verified archive.");
        }

        var contentLocks = new List<PreparedContentLock>(directoryEntries.Length);
        try
        {
            foreach (var entry in directoryEntries)
            {
                var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(entry.Name));
                contentLocks.Add(ExtractEntryLocked(entry, destinationPath));
            }
        }
        catch
        {
            DisposePreparedLocks(contentLocks);
            throw;
        }
        var infPath = Path.Combine(destinationDirectory, infName);
        try
        {
            var infText = File.ReadAllText(infPath);
            if (!string.IsNullOrWhiteSpace(expectedHardwareId) &&
                !infText.Contains(expectedHardwareId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The verified driver INF does not support expected hardware ID {expectedHardwareId}.");
            }
            var driverVersion = ReadInfDriverVersion(infText);
            if (string.IsNullOrWhiteSpace(expectedDriverVersion) ||
                !driverVersion.Equals(expectedDriverVersion, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The verified driver INF version is {driverVersion}; expected {expectedDriverVersion ?? "an explicit version"}.");
            }

            var catalogPath = Path.Combine(destinationDirectory, Path.GetFileNameWithoutExtension(infName) + ".cat");
            if (!File.Exists(catalogPath) || expectedCatalogSigners.Count == 0)
            {
                throw new InvalidDataException("The verified driver archive does not provide a catalog signature policy.");
            }
            var signatureErrors = new List<string>();
            foreach (var signer in expectedCatalogSigners)
            {
                var signature = _authenticodeVerifier.Verify(catalogPath, signer);
                if (signature.IsValid)
                {
                    return new PreparedDriverPayload(infPath, contentLocks);
                }
                if (!string.IsNullOrWhiteSpace(signature.Error))
                {
                    signatureErrors.Add(signature.Error);
                }
            }
            throw new InvalidDataException(string.Join(" | ", signatureErrors));
        }
        catch
        {
            DisposePreparedLocks(contentLocks);
            throw;
        }
    }

    private static PreparedContentLock ExtractEntryLocked(ZipArchiveEntry entry, string destinationPath)
    {
        using (var output = new FileStream(
                   destinationPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
        {
            using var input = entry.Open();
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }
        return new PreparedContentLock(
            destinationPath,
            new FileStream(destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read));
    }

    internal static string ReadInfDriverVersion(string infText)
    {
        foreach (var line in infText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("DriverVer", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var comma = trimmed.IndexOf(',');
            if (comma >= 0 && comma + 1 < trimmed.Length)
            {
                return trimmed[(comma + 1)..].Trim();
            }
        }
        throw new InvalidDataException("The driver INF does not declare DriverVer.");
    }

    public static bool IsSuccessfulExitCode(int exitCode) => exitCode is 0 or 1641 or 3010;

    private static bool ProcessMatchesPlan(
        UpdateExecutionPlan plan,
        Process process,
        out bool identityKnown)
    {
        var allowedPaths = plan.RunningExecutablePaths;
        if (allowedPaths is not { Count: > 0 })
        {
            identityKnown = true;
            return true;
        }
        try
        {
            var processPath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                identityKnown = false;
                return false;
            }
            identityKnown = true;
            var fullProcessPath = Path.GetFullPath(processPath);
            return allowedPaths.Any(path =>
            {
                try
                {
                    return Path.GetFullPath(path).Equals(fullProcessPath, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            identityKnown = false;
            return false;
        }
    }
}

public sealed class ApplicationStillRunningException(IReadOnlyList<string> processNames)
    : InvalidOperationException($"Close the following application processes before updating: {string.Join(", ", processNames)}")
{
    public IReadOnlyList<string> ProcessNames { get; } = processNames;
}

internal sealed record PreparedDriverPayload(
    string InfPath,
    IReadOnlyList<PreparedContentLock> ContentLocks);

internal sealed record PreparedNestedPayload(
    string Path,
    IReadOnlyList<PreparedContentLock> ContentLocks);
