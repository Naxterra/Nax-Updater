using NaxUpdater.Core.Internal;
using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

internal sealed class ZeroInstallUpdateProvider(ProcessQueryRunner processRunner) : IUpdateProvider
{
    public string Id => "zero-install";

    public bool CanHandle(InstalledApplication application) => application.ManagementMode == ManagementMode.ZeroInstall;

    public async Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        var feed = application.Evidence.FirstOrDefault(static evidence => evidence.Label == "Zero Install feed")?.Value;
        var uninstallCommand = application.Evidence.FirstOrDefault(static evidence => evidence.Label == "Uninstall command")?.Value;
        var zeroInstallWindows = NativePathParser.ExecutableFromCommand(uninstallCommand);
        var cli = string.IsNullOrWhiteSpace(zeroInstallWindows)
            ? null
            : Path.Combine(Path.GetDirectoryName(zeroInstallWindows) ?? string.Empty, "0install.exe");
        if (string.IsNullOrWhiteSpace(feed) || string.IsNullOrWhiteSpace(cli) || !File.Exists(cli))
        {
            return Error(application, "Zero Install feed or CLI could not be resolved.");
        }

        var query = await processRunner.RunAsync(
            cli,
            ["select", "--refresh", "--xml", feed],
            TimeSpan.FromSeconds(45),
            cancellationToken);
        var selection = query.ExitCode == 0
            ? ZeroInstallEnricher.ParseSelection(query.StandardOutput, feed)
            : null;
        var usedCachedSelection = false;
        if (selection is null)
        {
            var offline = await processRunner.RunAsync(
                cli,
                ["select", "--offline", "--xml", feed],
                TimeSpan.FromSeconds(12),
                cancellationToken);
            selection = offline.ExitCode == 0
                ? ZeroInstallEnricher.ParseSelection(offline.StandardOutput, feed)
                : null;
            if (selection is null)
            {
                var refreshError = string.IsNullOrWhiteSpace(query.StandardError)
                    ? $"Zero Install refresh exited with {query.ExitCode}."
                    : query.StandardError.Trim();
                var offlineError = string.IsNullOrWhiteSpace(offline.StandardError)
                    ? $"Cached selection exited with {offline.ExitCode}."
                    : offline.StandardError.Trim();
                return Error(application, $"{refreshError} {offlineError}");
            }
            usedCachedSelection = true;
        }

        var status = VersionOrder.Compare(selection.Version, application.NormalizedVersion) > 0
            ? UpdateStatus.Available
            : UpdateStatus.Current;
        var processName = string.IsNullOrWhiteSpace(application.PrimaryInstallPath)
            ? application.DisplayName
            : Path.GetFileNameWithoutExtension(application.PrimaryInstallPath);
        var plan = status == UpdateStatus.Available
            ? new UpdateExecutionPlan(
                UpdateExecutionKind.NativeCommand,
                null,
                null,
                null,
                null,
                cli,
                ["update", "--batch", feed],
                false,
                [],
                [processName])
            : null;
        return new UpdateCheckResult(
            application.Identity,
            application.DisplayName,
            application.NormalizedVersion,
            selection.Version,
            status,
            Id,
            "Zero Install native feed",
            "provider-managed",
            "Zero Install application feed",
            selection.Architecture ?? "provider-selected",
            "stable",
            feed,
            usedCachedSelection
                ? $"Zero Install refresh was unavailable; cached signed selection {selection.Version} with content digest {selection.Digest} was verified instead."
                : $"Zero Install selected content digest {selection.Digest}.",
            plan);
    }

    private UpdateCheckResult Error(InstalledApplication application, string message) => new(
        application.Identity,
        application.DisplayName,
        application.NormalizedVersion,
        null,
        UpdateStatus.Error,
        Id,
        "Zero Install native feed",
        "provider-managed",
        "Zero Install application feed",
        "provider-selected",
        "stable",
        null,
        message,
        null);
}
