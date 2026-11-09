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
            var ended = await WaitForUninstallerAsync(process, () => RegistryInventoryScanner.IsRegistered(application.Identity), cancellationToken,
                removedGrace: RemovedGraceFor(plan.Executable));
            // The uninstall entry is gone and the started program stays open: the removal has
            // happened, and the program has no exit code to report.
            if (!ended) return new RemovalResult(true, null, false, null);
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
    // this long at most, counted from the moment no uninstaller was running any more.
    internal static readonly TimeSpan LeftBehindLimit = TimeSpan.FromMinutes(10);

    // A registered command can start a program that stays open after the removal, such as a game
    // launcher. Once the uninstall entry is gone, the first process is waited for this long only;
    // the removal otherwise lasted until the user closed that program. Windows Installer is
    // always waited for: it removes the entry before it asks for a restart, and only its end
    // gives the exit code that says whether a restart is needed.
    internal static readonly TimeSpan RemovedGrace = TimeSpan.FromMinutes(2);

    internal static TimeSpan? RemovedGraceFor(string executable) => IsWindowsInstaller(executable) ? null : RemovedGrace;

    private static bool IsWindowsInstaller(string path) =>
        Path.GetFileName(path).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase);

    // The result is false when the first process was still running after the uninstall entry had
    // been gone for "removedGrace". Without a grace the first process is waited for until it ends.
    internal static async Task<bool> WaitForUninstallerAsync(Process process, Func<bool?> stillRegistered, CancellationToken cancellationToken,
        TimeSpan? leftBehindLimit = null, Func<string?, bool>? isUninstaller = null, TimeSpan? removedGrace = null)
    {
        isUninstaller ??= IsUninstallerProgram;
        var descendants = new ProcessDescendants(process);
        long? removedAt = null;
        while (!process.HasExited)
        {
            descendants.Refresh();
            await Task.WhenAny(process.WaitForExitAsync(cancellationToken), Task.Delay(250, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (removedGrace is not { } grace || process.HasExited) continue;
            if (removedAt is null && stillRegistered() == false) removedAt = Stopwatch.GetTimestamp();
            if (removedAt is { } removed && Stopwatch.GetElapsedTime(removed) >= grace) return false;
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
        return true;
    }

    // A process that the uninstaller started is the uninstaller itself when it runs from a temp
    // folder (NSIS and Inno Setup copy themselves there and end their first process) or is
    // Windows Installer. A helper, a browser or another instance of the application runs from a
    // program folder; the registered file name does not count, because an application that is
    // its own uninstaller ("Update.exe --uninstall") can start itself again and stay open. A
    // path that cannot be read is no uninstaller.
    internal static bool IsUninstallerProgram(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (IsWindowsInstaller(path)) return true;
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
