using NaxUpdater.Core.Models;
using System.Diagnostics;
using System.Windows.Automation;
using Windows.Management.Deployment;

namespace NaxUpdater.Core.Services;

internal sealed class ApplicationOwnedUpdateService
{
    private const string ChatGptPackageFamily = "OpenAI.Codex_2p2nqsd0c76g0";
    private const string ChatGptApplicationId = "OpenAI.Codex_2p2nqsd0c76g0!App";
    private static readonly string[] UpdateButtonNames = ["Update", "Aktualisieren"];

    public async Task<UpdateExecutionResult> ExecuteAsync(
        UpdateCheckResult update,
        CancellationToken cancellationToken)
    {
        if (update.ExecutionPlan?.StorePackageFamilyName?.Equals(
                ChatGptPackageFamily,
                StringComparison.OrdinalIgnoreCase) != true)
        {
            return new UpdateExecutionResult(-1, false, "The application-owned updater identity is not supported.");
        }

        var installedBefore = GetInstalledVersion(ChatGptPackageFamily);
        var targetVersion = Version.TryParse(update.AvailableVersion, out var parsedTarget)
            ? parsedTarget
            : null;
        if (targetVersion is not null && installedBefore is not null && installedBefore >= targetVersion)
        {
            return new UpdateExecutionResult(0, true, null);
        }

        var window = await FindOrLaunchChatGptWindowAsync(cancellationToken);
        if (window == IntPtr.Zero)
        {
            return new UpdateExecutionResult(-1, false, "ChatGPT did not expose a window for its application-owned updater.");
        }

        string? invocationError = null;
        try
        {
            var updateButton = FindUpdateAction(window);
            if (updateButton is null ||
                !updateButton.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) ||
                pattern is not InvokePattern invokePattern)
            {
                return new UpdateExecutionResult(-1, false,
                    "ChatGPT is running, but its accessible Update/Aktualisieren action is not currently available.");
            }
            invokePattern.Invoke();
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException)
        {
            invocationError = exception.Message;
        }
        if (invocationError is not null)
        {
            return new UpdateExecutionResult(-1, false, $"ChatGPT's application-owned updater could not be invoked: {invocationError}");
        }

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var installed = GetInstalledVersion(ChatGptPackageFamily);
            if (installed is not null &&
                (targetVersion is not null ? installed >= targetVersion : installedBefore is null || installed > installedBefore))
            {
                return new UpdateExecutionResult(0, true, null);
            }
            await Task.Delay(1000, cancellationToken);
        }

        var installedAfter = GetInstalledVersion(ChatGptPackageFamily);
        return new UpdateExecutionResult(
            -1,
            false,
            $"ChatGPT's updater was invoked, but the installed package remained {installedAfter?.ToString() ?? "unknown"}; expected {targetVersion?.ToString() ?? update.AvailableVersion ?? "a newer version"}.");
    }

    internal static bool HasUpdateAction(IntPtr window) => FindUpdateAction(window) is not null;

    private static AutomationElement? FindUpdateAction(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return null;
        }
        var root = AutomationElement.FromHandle(window);
        var buttonCondition = new PropertyCondition(
            AutomationElement.ControlTypeProperty,
            ControlType.Button);
        var buttons = root.FindAll(TreeScope.Descendants, buttonCondition);
        for (var index = 0; index < buttons.Count; index++)
        {
            var candidate = buttons[index];
            var name = candidate.Current.Name ?? string.Empty;
            if (candidate.Current.IsEnabled && UpdateButtonNames.Any(expected =>
                    name.Equals(expected, StringComparison.CurrentCultureIgnoreCase)))
            {
                return candidate;
            }
        }
        return null;
    }

    private static async Task<IntPtr> FindOrLaunchChatGptWindowAsync(CancellationToken cancellationToken)
    {
        var window = FindChatGptWindow();
        if (window != IntPtr.Zero)
        {
            return window;
        }

        var explorer = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "explorer.exe"))
        {
            UseShellExecute = true
        };
        explorer.ArgumentList.Add($"shell:AppsFolder\\{ChatGptApplicationId}");
        Process.Start(explorer)?.Dispose();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250, cancellationToken);
            window = FindChatGptWindow();
            if (window != IntPtr.Zero)
            {
                return window;
            }
        }
        return IntPtr.Zero;
    }

    private static IntPtr FindChatGptWindow()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited && process.SessionId == current.SessionId && process.MainWindowHandle != IntPtr.Zero)
                    {
                        return process.MainWindowHandle;
                    }
                }
                catch
                {
                    // Continue to another ChatGPT process if this one exits during inspection.
                }
            }
        }
        return IntPtr.Zero;
    }

    private static Version? GetInstalledVersion(string packageFamilyName)
    {
        try
        {
            return new PackageManager()
                .FindPackagesForUser(string.Empty, packageFamilyName)
                .Select(static package => package.Id.Version)
                .Select(static version => new Version(
                    checked((int)version.Major),
                    checked((int)version.Minor),
                    checked((int)version.Build),
                    checked((int)version.Revision)))
                .OrderDescending()
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
