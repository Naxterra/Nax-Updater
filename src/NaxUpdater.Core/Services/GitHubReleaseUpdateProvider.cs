using NaxUpdater.Core.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NaxUpdater.Core.Services;

public sealed class GitHubReleaseUpdateProvider(
    HttpClient httpClient,
    GitHubUpdateRecipe recipe) : IUpdateProvider
{
    public string Id => $"github:{recipe.Repository}";

    public bool CanHandle(InstalledApplication application)
    {
        return application.DisplayName.Equals(recipe.DisplayName, StringComparison.OrdinalIgnoreCase) &&
               (string.IsNullOrWhiteSpace(recipe.PublisherContains) ||
                application.Publisher?.Contains(recipe.PublisherContains, StringComparison.OrdinalIgnoreCase) == true);
    }

    public async Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{recipe.Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("NaxUpdater/0.2");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));

        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString()?.TrimStart('v', 'V') : null;
        var releasePage = root.TryGetProperty("html_url", out var htmlValue) ? htmlValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag) || !root.TryGetProperty("assets", out var assets))
        {
            return Error(application, "The GitHub release did not contain a version or assets.");
        }

        var assetPattern = new Regex(recipe.AssetNamePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        JsonElement? selectedAsset = null;
        Match? selectedMatch = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            var match = assetPattern.Match(name);
            if (match.Success)
            {
                selectedAsset = asset;
                selectedMatch = match;
                break;
            }
        }
        if (selectedAsset is null || selectedMatch is null)
        {
            return Error(application, $"No release asset matched {recipe.AssetNamePattern}.", tag);
        }

        var latestVersion = selectedMatch.Groups["version"].Success
            ? selectedMatch.Groups["version"].Value
            : tag;
        var assetName = selectedAsset.Value.GetProperty("name").GetString()!;
        var downloadUrl = selectedAsset.Value.GetProperty("browser_download_url").GetString();
        var digest = selectedAsset.Value.TryGetProperty("digest", out var digestValue) ? digestValue.GetString() : null;
        var sha256 = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : null;
        var status = VersionOrder.Compare(latestVersion, application.NormalizedVersion) > 0
            ? UpdateStatus.Available
            : UpdateStatus.Current;
        if (status == UpdateStatus.Available &&
            (string.IsNullOrWhiteSpace(downloadUrl) || sha256?.Length != 64 || string.IsNullOrWhiteSpace(recipe.ExpectedSigner)))
        {
            return Error(application, "The release lacks a complete SHA-256 digest, download URL, or signer policy; installation is blocked.", latestVersion, releasePage);
        }

        var plan = status == UpdateStatus.Available
            ? new UpdateExecutionPlan(
                recipe.InstallerKind,
                new Uri(downloadUrl!),
                assetName,
                sha256,
                recipe.ExpectedSigner,
                null,
                recipe.InstallerArguments.ToArray(),
                recipe.RequiresElevation,
                ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com", "github-releases.githubusercontent.com"],
                recipe.RunningProcessNames.ToArray())
            : null;
        return new UpdateCheckResult(
            application.Identity,
            application.DisplayName,
            application.NormalizedVersion,
            latestVersion,
            status,
            Id,
            $"Official GitHub release · {recipe.Repository}",
            recipe.Language,
            recipe.Language.Equals("neutral", StringComparison.OrdinalIgnoreCase)
                ? "Vendor multi-language installer"
                : "Recipe-pinned installer language",
            recipe.Architecture,
            "stable",
            releasePage,
            "The GitHub asset SHA-256 and Windows publisher signature are required before installation.",
            plan);
    }

    private UpdateCheckResult Error(
        InstalledApplication application,
        string message,
        string? availableVersion = null,
        string? releasePage = null) => new(
            application.Identity,
            application.DisplayName,
            application.NormalizedVersion,
            availableVersion,
            UpdateStatus.Error,
            Id,
            $"Official GitHub release · {recipe.Repository}",
            recipe.Language,
            recipe.Language.Equals("neutral", StringComparison.OrdinalIgnoreCase)
                ? "Vendor multi-language installer"
                : "Recipe-pinned installer language",
            recipe.Architecture,
            "stable",
            releasePage,
            message,
            null);
}
