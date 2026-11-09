using NaxUpdater.Core.Models;
using System.ComponentModel;
using System.Diagnostics;
using Windows.Management.Deployment;

namespace NaxUpdater.Core.Services;

public sealed class ApplicationRemovalService
{
    public async Task<RemovalResult> RemoveAsync(
        InstalledApplication application,
        CancellationToken cancellationToken = default)
    {
        var plan = application.RemovalPlan ?? throw new InvalidOperationException("No safe removal method is registered for this application.");
        if (plan.Kind == RemovalKind.MsixPackage)
        {
            return await RemoveMsixAsync(plan, cancellationToken);
        }
        if (string.IsNullOrWhiteSpace(plan.Executable) || !File.Exists(plan.Executable))
        {
            return new RemovalResult(false, null, false, "The registered uninstaller no longer exists.");
        }

        var startInfo = new ProcessStartInfo(plan.Executable)
        {
            UseShellExecute = true,
            Arguments = plan.Arguments ?? string.Empty
        };
        if (plan.RequiresElevation)
        {
            startInfo.Verb = "runas";
        }
        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The registered uninstaller did not start.");
            await WaitForUninstallerAsync(process, () => RegistryInventoryScanner.IsRegistered(application.Identity), cancellationToken,
                isUninstaller: path => IsUninstallerProgram(path, plan.Executable));
            var exitCode = process.ExitCode;
            return new RemovalResult(
                IsSuccessfulExitCode(exitCode),
                exitCode,
                exitCode is 1641 or 3010,
                IsSuccessfulExitCode(exitCode) ? null : $"The uninstaller exited with code {exitCode}.");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return new RemovalResult(false, 1223, false, "The Windows elevation prompt was cancelled.");
        }
    }

    public static bool IsSuccessfulExitCode(int exitCode) => exitCode is 0 or 1605 or 1641 or 3010;

    // The removal is over when the uninstaller and the processes it started have ended. An NSIS
    // uninstaller copies itself to the temp folder, starts the copy and exits at once with code
    // 0; the removal was then reported as finished and the next change could start while the
    // real uninstaller was still open. The wait also ends when the application's uninstall entry
    // is gone, so a browser page that an uninstaller opens at its end does not hold it.
    //
    // An uninstaller that the user cancels keeps its entry and can leave a helper or a browser
    // running. That wait had no limit, so every action stayed locked and the window could not be
    // closed. A left-behind process that is the uninstaller itself is still waited for as long
    // as it runs, because the user has it open. Any other left-behind process is waited for
    // this long at most, counted from the moment no uninstaller was running any more. The first
    // process is always waited for: only its end gives the exit code, and Windows Installer
    // removes the uninstall entry before it asks for a restart.
    internal static readonly TimeSpan LeftBehindLimit = TimeSpan.FromMinutes(10);

    internal static async Task WaitForUninstallerAsync(Process process, Func<bool?> stillRegistered, CancellationToken cancellationToken,
        TimeSpan? leftBehindLimit = null, Func<string?, bool>? isUninstaller = null)
    {
        isUninstaller ??= static path => IsUninstallerProgram(path, null);
        var descendants = new ProcessDescendants(process);
        while (!process.HasExited)
        {
            descendants.Refresh();
            await Task.WhenAny(process.WaitForExitAsync(cancellationToken), Task.Delay(250, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
        }
        descendants.Refresh();
        var uninstallerSeen = Stopwatch.GetTimestamp();
        while (stillRegistered() != false)
        {
            if (descendants.AnyDescendantRunning(isUninstaller)) uninstallerSeen = Stopwatch.GetTimestamp();
            else if (!descendants.AnyDescendantRunning() || Stopwatch.GetElapsedTime(uninstallerSeen) >= (leftBehindLimit ?? LeftBehindLimit)) break;
            await Task.Delay(500, cancellationToken);
            descendants.Refresh();
        }
    }

    // A process that the uninstaller started is the uninstaller itself when it runs from a temp
    // folder (NSIS and Inno Setup copy themselves there and end their first process), when it is
    // Windows Installer, or when it has the file name of the registered uninstaller. A helper or
    // a browser runs from its own program folder. A path that cannot be read is no uninstaller.
    internal static bool IsUninstallerProgram(string? path, string? registeredUninstaller)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var name = Path.GetFileName(path);
        if (name.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrWhiteSpace(registeredUninstaller) &&
            name.Equals(Path.GetFileName(registeredUninstaller), StringComparison.OrdinalIgnoreCase)) return true;
        // An uninstaller that an administrator account started uses that account's temp folder.
        return path.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               path.Contains(@"\AppData\Local\Temp\", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<RemovalResult> RemoveMsixAsync(RemovalPlan plan, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plan.PackageFullName))
        {
            return new RemovalResult(false, null, false, "The MSIX package identity is missing.");
        }
        try
        {
            var manager = new PackageManager();
            var result = await manager.RemovePackageAsync(plan.PackageFullName).AsTask(cancellationToken);
            if (result.ExtendedErrorCode is not null && result.ExtendedErrorCode.HResult != 0)
            {
                return new RemovalResult(false, result.ExtendedErrorCode.HResult, false, result.ErrorText);
            }
            return new RemovalResult(true, 0, false, null);
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return new RemovalResult(false, exception.HResult, false, exception.Message);
        }
    }
}
