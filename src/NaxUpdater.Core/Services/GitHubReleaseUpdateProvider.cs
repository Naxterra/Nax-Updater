using NaxUpdater.Core.Models;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NaxUpdater.Core.Services;

public sealed class GitHubReleaseUpdateProvider : IUpdateProvider
{
    private readonly HttpClient httpClient;
    private readonly GitHubUpdateRecipe recipe;
    private readonly Func<CancellationToken, Task<string?>> githubCliReleaseReader;

    public GitHubReleaseUpdateProvider(
        HttpClient httpClient,
        GitHubUpdateRecipe recipe,
        Func<CancellationToken, Task<string?>>? githubCliReleaseReader = null)
    {
        this.httpClient = httpClient;
        this.recipe = recipe;
        this.githubCliReleaseReader = githubCliReleaseReader ?? ReadReleaseThroughGitHubCliAsync;
    }

    public string Id => $"github:{recipe.Repository}";
    public UpdateProviderDescriptor Descriptor { get; } = new(
        UpdateProviderAuthority.ProducerRelease,
        90,
        "Explicit producer repository recipe with name and publisher correlation",
        [ManagementMode.Unmanaged, ManagementMode.Registry, ManagementMode.WindowsInstaller, ManagementMode.DirectVendor]);

    public bool CanHandle(InstalledApplication application)
    {
        return application.DisplayName.Equals(recipe.DisplayName, StringComparison.OrdinalIgnoreCase) &&
               (string.IsNullOrWhiteSpace(recipe.PublisherContains) ||
                application.Publisher?.Contains(recipe.PublisherContains, StringComparison.OrdinalIgnoreCase) == true);
    }

    public async Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await GetLatestReleaseResponseAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is (HttpRequestException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            return await CheckLatestTagFallbackAsync(application, exception.Message, cancellationToken);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return await CheckLatestTagFallbackAsync(
                    application,
                    $"GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase}.",
                    cancellationToken);
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));

            var root = document.RootElement;
            if ((root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) ||
                (root.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True))
                return Error(application, "The release is not a published stable release.");
            var tag = root.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString()?.TrimStart('v', 'V') : null;
            var releasePage = root.TryGetProperty("html_url", out var htmlValue) ? htmlValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || !root.TryGetProperty("assets", out var assets))
            {
                return Error(application, "The GitHub release did not contain a version or assets.");
            }

            var installedArchitecture = InstalledApplicationMetadata.Architecture(application);
            var architectureSupported = string.Equals(installedArchitecture, recipe.Architecture, StringComparison.OrdinalIgnoreCase) ||
                installedArchitecture is not null && recipe.AlternateArchitectureAssets.ContainsKey(installedArchitecture);
            var pattern = installedArchitecture is not null && recipe.AlternateArchitectureAssets.TryGetValue(installedArchitecture, out var alternate)
                ? alternate : recipe.AssetNamePattern;
            var assetPattern = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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
            var releaseVersion = recipe.Repository.Equals("git-for-windows/git", StringComparison.OrdinalIgnoreCase) &&
                tag.EndsWith(".windows.1", StringComparison.OrdinalIgnoreCase) ? tag[..^10] : tag;
            if (VersionOrder.Compare(latestVersion, releaseVersion) != 0)
                return Error(application, "The release tag and installer version disagree; the installer was not approved.", tag, releasePage);
            var assetName = selectedAsset.Value.GetProperty("name").GetString()!;
            var downloadUrl = selectedAsset.Value.GetProperty("browser_download_url").GetString();
            var digest = selectedAsset.Value.TryGetProperty("digest", out var digestValue) ? digestValue.GetString() : null;
            var sha256 = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : null;
            var status = VersionOrder.Compare(latestVersion, application.NormalizedVersion) > 0
                ? UpdateStatus.Available
                : UpdateStatus.Current;
            var scopeSupported = application.Scope == InstallScope.Machine && recipe.RequiresElevation ||
                application.Scope == InstallScope.CurrentUser && (!recipe.RequiresElevation || recipe.CurrentUserInstallerArguments is not null);
            if (status == UpdateStatus.Available &&
                (!architectureSupported || !scopeSupported || string.IsNullOrWhiteSpace(application.NormalizedVersion)))
            {
                return new UpdateCheckResult(
                    application.Identity, application.DisplayName, application.NormalizedVersion, latestVersion,
                    UpdateStatus.NewerReleaseKnown, Id, $"Official GitHub release · {recipe.Repository}",
                    recipe.Language, "Installer language", installedArchitecture ?? "unknown", "stable", releasePage,
                    !architectureSupported ? "No producer installer matches the verified installed architecture." :
                    !scopeSupported ? "No producer installer configuration preserves the installed user/machine scope." :
                    "The installed version must be resolved before updating.",
                    null, Applicability: UpdateApplicability.NotApplicable);
            }
            if (status == UpdateStatus.Available &&
                (string.IsNullOrWhiteSpace(downloadUrl) || sha256?.Length != 64 || string.IsNullOrWhiteSpace(recipe.ExpectedSigner)))
            {
                return new UpdateCheckResult(
                    application.Identity,
                    application.DisplayName,
                    application.NormalizedVersion,
                    latestVersion,
                    UpdateStatus.NewerReleaseKnown,
                    Id,
                    $"Official GitHub release · {recipe.Repository}",
                    recipe.Language,
                    recipe.Language.Equals("neutral", StringComparison.OrdinalIgnoreCase)
                        ? "Vendor multi-language installer"
                        : "Recipe-pinned installer language",
                    recipe.Architecture,
                    "stable",
                    releasePage,
                    "A newer producer release is verified, but automatic installation is blocked until the release has a complete asset digest, download URL, and Authenticode signer policy.",
                    null,
                    Applicability: UpdateApplicability.NotApplicable);
            }

            var arguments = application.Scope == InstallScope.CurrentUser && recipe.CurrentUserInstallerArguments is not null
                ? recipe.CurrentUserInstallerArguments.ToList() : recipe.InstallerArguments.ToList();
            var installDirectory = InstalledApplicationMetadata.InstallDirectory(application);
            if (recipe.InstallDirectoryArgument is not null && installDirectory is not null)
            {
                if (recipe.InstallDirectoryArgument.EndsWith('='))
                    arguments.Add(recipe.InstallDirectoryArgument + installDirectory);
                else
                {
                    arguments.Add(recipe.InstallDirectoryArgument);
                    arguments.Add(installDirectory);
                }
            }
            var plan = status == UpdateStatus.Available
                ? new UpdateExecutionPlan(
                    recipe.InstallerKind,
                    new Uri(downloadUrl!),
                    assetName,
                    sha256,
                    recipe.ExpectedSigner,
                    null,
                    arguments,
                    application.Scope == InstallScope.Machine && recipe.RequiresElevation,
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
                installedArchitecture ?? recipe.Architecture,
                "stable",
                releasePage,
                "The GitHub asset SHA-256 and Windows publisher signature are required before installation.",
                plan);
        }
    }

    private async Task<HttpResponseMessage> GetLatestReleaseResponseAsync(CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            response?.Dispose();
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{recipe.Repository}/releases/latest");
            request.Headers.UserAgent.ParseAdd("NaxUpdater/0.16.6");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            try
            {
                response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }
            }
            catch (Exception exception) when (attempt == 0 && exception is (HttpRequestException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(350, cancellationToken);
                continue;
            }
            if (attempt == 0)
            {
                await Task.Delay(350, cancellationToken);
            }
        }
        var cliJson = await githubCliReleaseReader(cancellationToken);
        if (!string.IsNullOrWhiteSpace(cliJson))
        {
            response?.Dispose();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"https://api.github.com/repos/{recipe.Repository}/releases/latest"),
                Content = new StringContent(cliJson, Encoding.UTF8, "application/json")
            };
        }
        return response ?? throw new HttpRequestException("GitHub returned no response.");
    }

    private Task<string?> ReadReleaseThroughGitHubCliAsync(CancellationToken cancellationToken) =>
        GitHubApiClient.ReadUsingCliAsync($"repos/{recipe.Repository}/releases/latest", cancellationToken);

    private async Task<UpdateCheckResult> CheckLatestTagFallbackAsync(
        InstalledApplication application,
        string apiError,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://github.com/{recipe.Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("NaxUpdater/0.16.6");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var finalUri = response.RequestMessage?.RequestUri;
        var match = finalUri is null
            ? Match.Empty
            : Regex.Match(finalUri.AbsolutePath, @"/releases/tag/v?(?<version>[^/]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var latestVersion = match.Success ? Uri.UnescapeDataString(match.Groups["version"].Value) : null;
        if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(latestVersion))
        {
            return Error(application, $"{apiError} The latest-release fallback could not resolve a version.");
        }

        var releasePage = finalUri!.AbsoluteUri;
        if (VersionOrder.Compare(latestVersion, application.NormalizedVersion) > 0)
        {
            return Error(
                application,
                $"{apiError} GitHub's latest-release redirect reports {latestVersion}, but the API is required to verify its exact asset digest before installation.",
                latestVersion,
                releasePage);
        }

        return new UpdateCheckResult(
            application.Identity,
            application.DisplayName,
            application.NormalizedVersion,
            latestVersion,
            UpdateStatus.Current,
            Id,
            $"Official GitHub release · {recipe.Repository}",
            recipe.Language,
            recipe.Language.Equals("neutral", StringComparison.OrdinalIgnoreCase)
                ? "Vendor multi-language installer"
                : "Recipe-pinned installer language",
            recipe.Architecture,
            "stable",
            releasePage,
            $"{apiError} GitHub's immutable latest-release redirect confirms that the installed version is current.",
            null);
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
