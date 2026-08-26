using NaxUpdater.Core.Models;
using System.ComponentModel;
using System.Diagnostics;

namespace NaxUpdater.Core.Services;

public sealed class UpdateExecutionService
{
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
            var query = await new ProcessQueryRunner().RunAsync(
                plan.NativeExecutable,
                plan.Arguments,
                TimeSpan.FromMinutes(20),
                cancellationToken);
            return new UpdateExecutionResult(query.ExitCode, IsSuccessfulExitCode(query.ExitCode), query.StandardError.Trim());
        }

        if (installer is null || !File.Exists(installer.Path))
        {
            throw new InvalidOperationException("The verified installer is missing.");
        }

        var startInfo = plan.Kind switch
        {
            UpdateExecutionKind.DownloadedExe => new ProcessStartInfo(installer.Path),
            UpdateExecutionKind.DownloadedMsi => new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe")),
            _ => throw new InvalidOperationException($"Unsupported execution kind {plan.Kind}.")
        };
        startInfo.UseShellExecute = true;
        if (plan.RequiresElevation)
        {
            startInfo.Verb = "runas";
        }
        if (plan.Kind == UpdateExecutionKind.DownloadedMsi)
        {
            startInfo.ArgumentList.Add("/i");
            startInfo.ArgumentList.Add(installer.Path);
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
    }

    public static bool IsSuccessfulExitCode(int exitCode) => exitCode is 0 or 1641 or 3010;
}

public sealed record UpdateExecutionResult(int ExitCode, bool IsSuccess, string? Error);

public sealed class ApplicationStillRunningException(IReadOnlyList<string> processNames)
    : InvalidOperationException($"Close the following application processes before updating: {string.Join(", ", processNames)}")
{
    public IReadOnlyList<string> ProcessNames { get; } = processNames;
}
