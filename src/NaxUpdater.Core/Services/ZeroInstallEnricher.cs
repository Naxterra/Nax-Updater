using NaxUpdater.Core.Internal;
using NaxUpdater.Core.Models;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NaxUpdater.Core.Services;

internal sealed partial class ZeroInstallEnricher(ProcessQueryRunner processRunner)
{
    public async Task EnrichAsync(
        ApplicationCandidate candidate,
        ICollection<InventoryIssue> issues,
        CancellationToken cancellationToken)
    {
        if (candidate.UninstallString?.Contains("0install", StringComparison.OrdinalIgnoreCase) != true)
        {
            return;
        }

        var zeroInstallWindows = NativePathParser.ExecutableFromCommand(candidate.UninstallString);
        var feed = FeedRegex().Match(candidate.UninstallString).Value;
        if (string.IsNullOrWhiteSpace(zeroInstallWindows) || string.IsNullOrWhiteSpace(feed))
        {
            return;
        }

        var zeroInstallCli = Path.Combine(Path.GetDirectoryName(zeroInstallWindows) ?? string.Empty, "0install.exe");
        if (!File.Exists(zeroInstallCli))
        {
            issues.Add(new InventoryIssue("Zero Install", $"The Zero Install CLI was not found for {candidate.DisplayName}."));
            return;
        }

        candidate.ManagementMode = ManagementMode.ZeroInstall;
        candidate.Evidence.Add(new ApplicationEvidence(EvidenceKind.ZeroInstall, "Zero Install feed", feed, true));

        var selectionResult = await processRunner.RunAsync(
            zeroInstallCli,
            ["select", "--offline", "--xml", feed],
            TimeSpan.FromSeconds(8),
            cancellationToken);
        if (selectionResult.ExitCode != 0)
        {
            issues.Add(new InventoryIssue("Zero Install", $"Could not query {candidate.DisplayName}: {CleanError(selectionResult)}"));
            return;
        }

        var selection = ParseSelection(selectionResult.StandardOutput, feed);
        if (selection is null)
        {
            issues.Add(new InventoryIssue("Zero Install", $"No selected implementation was reported for {candidate.DisplayName}."));
            return;
        }

        candidate.ProviderVersion = selection.Version;
        candidate.Evidence.Add(new ApplicationEvidence(
            EvidenceKind.ZeroInstall,
            "Selected implementation",
            $"{selection.Version} · {selection.Digest}",
            true));

        var storeResult = await processRunner.RunAsync(
            zeroInstallCli,
            ["store", "find", selection.Digest],
            TimeSpan.FromSeconds(5),
            cancellationToken);
        var implementationPath = storeResult.ExitCode == 0 ? storeResult.StandardOutput.Trim() : null;
        if (string.IsNullOrWhiteSpace(implementationPath))
        {
            return;
        }

        var executablePath = string.IsNullOrWhiteSpace(selection.CommandPath)
            ? implementationPath
            : Path.Combine(implementationPath, selection.CommandPath);
        var verified = File.Exists(executablePath) || Directory.Exists(executablePath);
        candidate.Paths.Add(new PathCandidate(executablePath, "Zero Install store", 120, verified));
        candidate.Evidence.Add(new ApplicationEvidence(
            EvidenceKind.ZeroInstall,
            "Implementation path",
            executablePath,
            verified));
    }

    public static ZeroInstallSelection? ParseSelection(string xml, string feed)
    {
        try
        {
            var document = XDocument.Parse(xml, LoadOptions.None);
            var selection = document.Descendants()
                .FirstOrDefault(element =>
                    element.Name.LocalName == "selection" &&
                    string.Equals(element.Attribute("interface")?.Value, feed, StringComparison.OrdinalIgnoreCase));
            if (selection is null)
            {
                return null;
            }

            var version = selection.Attribute("version")?.Value;
            var digest = selection.Attribute("id")?.Value;
            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(digest))
            {
                return null;
            }

            var commandPath = selection.Elements()
                .FirstOrDefault(element =>
                    element.Name.LocalName == "command" &&
                    string.Equals(element.Attribute("name")?.Value, "run", StringComparison.OrdinalIgnoreCase))
                ?.Attribute("path")?.Value;
            return new ZeroInstallSelection(version, digest, commandPath, selection.Attribute("arch")?.Value);
        }
        catch
        {
            return null;
        }
    }

    private static string CleanError(ProcessQueryResult result)
    {
        var error = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        return string.IsNullOrWhiteSpace(error) ? $"exit code {result.ExitCode}" : error.Trim();
    }

    [GeneratedRegex(@"https?://[^\s\""']+?\.xml", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FeedRegex();
}

public sealed record ZeroInstallSelection(string Version, string Digest, string? CommandPath, string? Architecture);
