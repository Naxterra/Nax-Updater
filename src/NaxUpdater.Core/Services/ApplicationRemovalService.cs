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
            await WaitForUninstallerAsync(process, () => RegistryInventoryScanner.IsRegistered(application.Identity), cancellationToken);
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
    internal static async Task WaitForUninstallerAsync(Process process, Func<bool?> stillRegistered, CancellationToken cancellationToken)
    {
        var descendants = new ProcessDescendants(process);
        while (!process.HasExited)
        {
            descendants.Refresh();
            await Task.WhenAny(process.WaitForExitAsync(cancellationToken), Task.Delay(250, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
        }
        descendants.Refresh();
        while (stillRegistered() != false && descendants.AnyDescendantRunning())
        {
            await Task.Delay(500, cancellationToken);
            descendants.Refresh();
        }
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
