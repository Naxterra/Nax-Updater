using NaxUpdater.Core.Models;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;

namespace NaxUpdater.Core.Services;

public sealed class UpdateExecutionService
{
    private readonly StorePackageDeploymentService _storePackageDeploymentService = new();
    private readonly IAuthenticodeVerifier _authenticodeVerifier;

    public UpdateExecutionService(IAuthenticodeVerifier? authenticodeVerifier = null)
    {
        _authenticodeVerifier = authenticodeVerifier ?? new NativeAuthenticodeVerifier();
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
                            found |= !process.HasExited && process.Id != currentProcess.Id && process.SessionId == currentSession;
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
                            process.SessionId == currentSession && process.MainWindowHandle != IntPtr.Zero)
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
        var elevationRequiredPids = new List<int>();
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
                        if (!process.HasExited && process.Id != updaterProcessId && process.SessionId == currentSession)
                        {
                            process.Kill(entireProcessTree: true);
                            forcedTerminationUsed = true;
                        }
                    }
                    catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        try
                        {
                            if (!process.HasExited && process.Id != updaterProcessId && process.SessionId == currentSession)
                            {
                                elevationRequiredPids.Add(process.Id);
                            }
                        }
                        catch
                        {
                            // The process exited between the termination attempt and the fallback check.
                        }
                    }
                }
            }
        }

        if (elevationRequiredPids.Count > 0)
        {
            forcedTerminationUsed = true;
            var taskKill = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"))
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            foreach (var pid in elevationRequiredPids.Distinct())
            {
                taskKill.ArgumentList.Add("/PID");
                taskKill.ArgumentList.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            taskKill.ArgumentList.Add("/T");
            taskKill.ArgumentList.Add("/F");
            try
            {
                using var elevatedKill = Process.Start(taskKill);
                if (elevatedKill is not null)
                {
                    await elevatedKill.WaitForExitAsync(cancellationToken);
                }
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                return new ApplicationCloseResult(closeRequested, forcedTerminationUsed, FindRunningProcesses(update));
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

    public async Task<UpdateExecutionResult> ExecuteAsync(
        UpdateCheckResult update,
        VerifiedInstaller? installer,
        CancellationToken cancellationToken = default)
    {
        var plan = update.ExecutionPlan ?? throw new InvalidOperationException("The update has no execution plan.");
        var running = FindRunningProcesses(update);
        if (running.Count > 0)
        {
            throw new ApplicationStillRunningException(running);
        }

        if (plan.Kind == UpdateExecutionKind.NativeCommand)
        {
            if (string.IsNullOrWhiteSpace(plan.NativeExecutable) || !File.Exists(plan.NativeExecutable))
            {
                throw new InvalidOperationException("The native update provider executable is missing.");
            }
            var nativeExecutable = plan.NativeExecutable;
            var nativeWorkingDirectory = plan.NativeWorkingDirectory;
            string? nativeStagingDirectory = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(plan.NativeStagingRoot))
                {
                    nativeStagingDirectory = Path.Combine(
                        Path.GetTempPath(),
                        "NaxUpdater",
                        "Native",
                        Guid.NewGuid().ToString("N"));
                    var staged = StageNativeCommandFiles(plan, nativeStagingDirectory);
                    nativeExecutable = staged.Executable;
                    nativeWorkingDirectory = staged.WorkingDirectory;
                    if (string.IsNullOrWhiteSpace(plan.ExpectedSigner))
                    {
                        throw new InvalidOperationException("The staged native updater has no signer policy.");
                    }
                    var signature = _authenticodeVerifier.Verify(nativeExecutable, plan.ExpectedSigner);
                    if (!signature.IsValid)
                    {
                        throw new InvalidDataException(signature.Error ?? "The staged native updater signature is invalid.");
                    }
                    var stagedVersion = FileVersionInfo.GetVersionInfo(nativeExecutable).ProductVersion?.Trim();
                    if (!string.Equals(stagedVersion, update.AvailableVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"The staged native updater version is {stagedVersion ?? "unknown"}; expected {update.AvailableVersion}.");
                    }
                }

                if (plan.RequiresElevation || !string.IsNullOrWhiteSpace(nativeWorkingDirectory))
                {
                    var nativeStartInfo = new ProcessStartInfo(nativeExecutable)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = string.IsNullOrWhiteSpace(nativeWorkingDirectory)
                            ? Path.GetDirectoryName(nativeExecutable) ?? string.Empty
                            : nativeWorkingDirectory
                    };
                    if (plan.RequiresElevation)
                    {
                        nativeStartInfo.Verb = "runas";
                    }
                    foreach (var argument in plan.Arguments)
                    {
                        nativeStartInfo.ArgumentList.Add(argument);
                    }
                    try
                    {
                        using var process = Process.Start(nativeStartInfo) ?? throw new InvalidOperationException("The native update provider did not start.");
                        await process.WaitForExitAsync(cancellationToken);
                        return new UpdateExecutionResult(process.ExitCode, IsSuccessfulExitCode(process.ExitCode), null);
                    }
                    catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
                    {
                        return new UpdateExecutionResult(1223, false, "The Windows elevation prompt was cancelled.");
                    }
                }
                var query = await new ProcessQueryRunner().RunAsync(
                    nativeExecutable,
                    plan.Arguments,
                    TimeSpan.FromMinutes(20),
                    cancellationToken);
                return new UpdateExecutionResult(query.ExitCode, IsSuccessfulExitCode(query.ExitCode), query.StandardError.Trim());
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(nativeStagingDirectory) && Directory.Exists(nativeStagingDirectory))
                {
                    try
                    {
                        Directory.Delete(nativeStagingDirectory, recursive: true);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // The completed vendor update is more important than best-effort staging cleanup.
                    }
                }
            }
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
                cancellationToken);
        }

        if (installer is null || !File.Exists(installer.Path))
        {
            throw new InvalidOperationException("The verified installer is missing.");
        }

        string? extractionDirectory = null;
        var executableInstallerPath = installer.Path;
        if (plan.Kind is UpdateExecutionKind.DownloadedZipMsi or UpdateExecutionKind.DownloadedZipDriver)
        {
            if (string.IsNullOrWhiteSpace(plan.NestedInstallerRelativePath))
            {
                throw new InvalidOperationException("The verified archive does not identify its nested installer.");
            }
            extractionDirectory = Path.Combine(Path.GetTempPath(), "NaxUpdater", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(extractionDirectory);
            executableInstallerPath = plan.Kind == UpdateExecutionKind.DownloadedZipDriver
                ? ExtractAndVerifyDriverPackage(
                    installer.Path,
                    plan.NestedInstallerRelativePath,
                    extractionDirectory,
                    plan.ExpectedHardwareId,
                    update.AvailableVersion,
                    plan.ExpectedSigners ?? [])
                : ExtractNestedInstaller(
                    installer.Path,
                    plan.NestedInstallerRelativePath,
                    extractionDirectory);
        }

        var startInfo = plan.Kind switch
        {
            UpdateExecutionKind.DownloadedExe => new ProcessStartInfo(executableInstallerPath),
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
            startInfo.ArgumentList.Add(executableInstallerPath);
        }
        else if (plan.Kind == UpdateExecutionKind.DownloadedZipDriver)
        {
            startInfo.ArgumentList.Add("/add-driver");
            startInfo.ArgumentList.Add(executableInstallerPath);
            startInfo.ArgumentList.Add("/install");
        }
        foreach (var argument in plan.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The installer did not start.");
            await process.WaitForExitAsync(cancellationToken);
            return new UpdateExecutionResult(process.ExitCode, IsSuccessfulExitCode(process.ExitCode), null);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return new UpdateExecutionResult(1223, false, "The Windows elevation prompt was cancelled.");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(extractionDirectory) && Directory.Exists(extractionDirectory))
            {
                try
                {
                    Directory.Delete(extractionDirectory, recursive: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The verified installer result is more important than best-effort temporary cleanup.
                }
            }
        }
    }

    internal static NativeStagingResult StageNativeCommandFiles(
        UpdateExecutionPlan plan,
        string destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(plan.NativeStagingRoot) ||
            string.IsNullOrWhiteSpace(plan.NativeExecutable))
        {
            throw new InvalidOperationException("The native staging plan is incomplete.");
        }
        var sourceRoot = Path.GetFullPath(plan.NativeStagingRoot).TrimEnd(Path.DirectorySeparatorChar);
        var sourceExecutable = Path.GetFullPath(plan.NativeExecutable);
        var sourceWorkingDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(plan.NativeWorkingDirectory)
                ? Path.GetDirectoryName(sourceExecutable) ?? sourceRoot
                : plan.NativeWorkingDirectory);
        if (!IsWithinDirectory(sourceExecutable, sourceRoot) ||
            !IsWithinDirectory(sourceWorkingDirectory, sourceRoot) ||
            !Directory.Exists(sourceRoot) ||
            !File.Exists(sourceExecutable))
        {
            throw new InvalidDataException("The native updater or working directory escapes its verified staging root.");
        }

        var destinationRoot = Path.GetFullPath(destinationDirectory).TrimEnd(Path.DirectorySeparatorChar);
        Directory.CreateDirectory(destinationRoot);
        CopyDirectoryTree(sourceRoot, destinationRoot, skipExistingFiles: false);
        var stagedExecutable = Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, sourceExecutable));
        var stagedWorkingDirectory = Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, sourceWorkingDirectory));
        if (!string.IsNullOrWhiteSpace(plan.NativeDependencyRoot))
        {
            var dependencyRoot = Path.GetFullPath(plan.NativeDependencyRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (!Directory.Exists(dependencyRoot))
            {
                throw new InvalidDataException("The native updater dependency directory is missing.");
            }
            CopyDirectoryTree(dependencyRoot, stagedWorkingDirectory, skipExistingFiles: true);
        }
        if (!File.Exists(stagedExecutable) || !Directory.Exists(stagedWorkingDirectory))
        {
            throw new InvalidDataException("The staged native updater is incomplete after copying.");
        }
        return new NativeStagingResult(stagedExecutable, stagedWorkingDirectory, destinationRoot);
    }

    private static void CopyDirectoryTree(string sourceRoot, string destinationRoot, bool skipExistingFiles)
    {
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Native staging rejected reparse-point directory {directory}.");
            }
            var destination = Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, directory));
            Directory.CreateDirectory(destination);
        }
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Native staging rejected reparse-point file {file}.");
            }
            var destination = Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (skipExistingFiles && File.Exists(destination))
            {
                continue;
            }
            File.Copy(file, destination, overwrite: false);
        }
    }

    private static bool IsWithinDirectory(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static string ExtractNestedInstaller(string archivePath, string relativePath, string destinationDirectory)
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
        matches[0].ExtractToFile(destinationPath, overwrite: false);
        return destinationPath;
    }

    internal string ExtractAndVerifyDriverPackage(
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

        foreach (var entry in directoryEntries)
        {
            var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(entry.Name));
            entry.ExtractToFile(destinationPath, overwrite: false);
        }

        var infPath = Path.Combine(destinationDirectory, infName);
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
                return infPath;
            }
            if (!string.IsNullOrWhiteSpace(signature.Error))
            {
                signatureErrors.Add(signature.Error);
            }
        }
        throw new InvalidDataException(string.Join(" | ", signatureErrors));
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
}

internal sealed record NativeStagingResult(string Executable, string WorkingDirectory, string Root);

public sealed record UpdateExecutionResult(int ExitCode, bool IsSuccess, string? Error);

public sealed record ApplicationCloseResult(
    bool CloseRequested,
    bool ForcedTerminationUsed,
    IReadOnlyList<string> RemainingProcessNames)
{
    public bool AllClosed => RemainingProcessNames.Count == 0;
}

public sealed class ApplicationStillRunningException(IReadOnlyList<string> processNames)
    : InvalidOperationException($"Close the following application processes before updating: {string.Join(", ", processNames)}")
{
    public IReadOnlyList<string> ProcessNames { get; } = processNames;
}
