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
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var processName in plan.RunningProcessNames)
        {
            try
            {
                if (Process.GetProcessesByName(processName).Length > 0)
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
            if (plan.RequiresElevation || !string.IsNullOrWhiteSpace(plan.NativeWorkingDirectory))
            {
                var nativeStartInfo = new ProcessStartInfo(plan.NativeExecutable)
                {
                    UseShellExecute = true,
                    WorkingDirectory = string.IsNullOrWhiteSpace(plan.NativeWorkingDirectory)
                        ? Path.GetDirectoryName(plan.NativeExecutable) ?? string.Empty
                        : plan.NativeWorkingDirectory
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
                plan.NativeExecutable,
                plan.Arguments,
                TimeSpan.FromMinutes(20),
                cancellationToken);
            return new UpdateExecutionResult(query.ExitCode, IsSuccessfulExitCode(query.ExitCode), query.StandardError.Trim());
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

public sealed record UpdateExecutionResult(int ExitCode, bool IsSuccess, string? Error);

public sealed class ApplicationStillRunningException(IReadOnlyList<string> processNames)
    : InvalidOperationException($"Close the following application processes before updating: {string.Join(", ", processNames)}")
{
    public IReadOnlyList<string> ProcessNames { get; } = processNames;
}
