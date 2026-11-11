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
        // The long form is read while the file still exists; an uninstaller can delete itself, and
        // a short 8.3 path can no longer be expanded then.
        var registeredUninstaller = LongPath(plan.Executable);
        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The registered uninstaller did not start.");
            var ended = await WaitForUninstallerAsync(process, () => RegistryInventoryScanner.IsRegistered(application.Identity), cancellationToken,
                isUninstaller: path => IsUninstallerProgram(path, registeredUninstaller), removedGrace: RemovedGraceFor(plan.Arguments));
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

    // A registered command can hand a link to a launcher ("steam.exe steam://uninstall/730"); the
    // launcher removes the application and stays open, and the removal then lasted until the
    // user closed it. Once the uninstall entry is gone, the first process of such a command is
    // waited for this long only. Every other command is waited for until its first process
    // ends: an uninstaller or a bootstrapper (Windows Installer, a WiX bundle, InstallShield)
    // can remove its entry and then wait for the user while it still holds the exit code that
    // says a restart is needed, and it must not lose the change lock while it runs.
    internal static readonly TimeSpan RemovedGrace = TimeSpan.FromMinutes(2);

    private static readonly System.Text.RegularExpressions.Regex LauncherLink = new(
        @"(?<![A-Za-z0-9+.\-])(?!https?://|ftp://|file://)[A-Za-z][A-Za-z0-9+.\-]*://",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static TimeSpan? RemovedGraceFor(string? arguments) =>
        arguments is not null && LauncherLink.IsMatch(arguments) ? RemovedGrace : null;

    private static bool IsWindowsInstaller(string path) =>
        Path.GetFileName(path).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase);

    // The result is false when the first process was still running after the uninstall entry had
    // been gone for "removedGrace". Without a grace the first process is waited for until it ends.
    internal static async Task<bool> WaitForUninstallerAsync(Process process, Func<bool?> stillRegistered, CancellationToken cancellationToken,
        TimeSpan? leftBehindLimit = null, Func<string?, bool>? isUninstaller = null, TimeSpan? removedGrace = null)
    {
        isUninstaller ??= static path => IsUninstallerProgram(path, null);
        var descendants = new ProcessDescendants(process);
        long? removedAt = null;
        while (!process.HasExited)
        {
            descendants.Refresh();
            await Task.WhenAny(process.WaitForExitAsync(cancellationToken), Task.Delay(250, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (removedGrace is not { } grace || process.HasExited) continue;
            // An entry that is back (a removal that was rolled back) starts the count again.
            if (stillRegistered() != false) removedAt = null;
            else removedAt ??= Stopwatch.GetTimestamp();
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
    // Windows Installer, when its file name says so ("uninstall.exe", "unins000.exe",
    // "setup.exe", "Vendor Setup 2.0.exe", also when it restarts itself elevated), or when it is a copy of the
    // registered uninstaller in another folder. A helper or a browser runs from a program
    // folder under another name. Another instance of the registered program itself does not
    // count, because an application that is its own uninstaller ("Update.exe --uninstall") can
    // start itself again and stay open. A path that cannot be read is no uninstaller.
    internal static bool IsUninstallerProgram(string? path, string? registeredUninstaller)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (IsWindowsInstaller(path)) return true;
        // An installer word counts at the end of the name or before a digit or a separator
        // ("OneDriveSetup.exe", "setup64.exe", "AppUninstaller.exe"), not before another word, so
        // a resident helper such as "SetupHelper.exe" or "AppSetupService.exe" is not waited for
        // without a limit.
        if (InstallerName.IsMatch(Path.GetFileNameWithoutExtension(path))) return true;
        // The registered path can be a short 8.3 path ("C:\PROGRA~1\App\App.exe"), while Windows
        // reports a running program by its long path, so both are compared in their long form.
        if (!string.IsNullOrWhiteSpace(registeredUninstaller) &&
            Path.GetFileName(LongPath(path)).Equals(Path.GetFileName(LongPath(registeredUninstaller)), StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(LongPath(path), LongPath(registeredUninstaller), StringComparison.OrdinalIgnoreCase)) return true;
        // An uninstaller that an administrator account started uses that account's temp folder.
        return path.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               path.Contains(@"\AppData\Local\Temp\", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly System.Text.RegularExpressions.Regex InstallerName = new(
        @"^unins|(?:uninstall(?:er)?|setup)(?:$|[\d\s_.\-])",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static string LongPath(string path)
    {
        var full = Path.GetFullPath(path);
        var buffer = new char[1024];
        var length = GetLongPathName(full, buffer, (uint)buffer.Length);
        return length > 0 && length < buffer.Length ? new string(buffer, 0, (int)length) : full;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, [System.Runtime.InteropServices.Out] char[] longPath, uint size);

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
