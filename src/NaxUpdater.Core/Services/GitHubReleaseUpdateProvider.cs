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
            request.Headers.UserAgent.ParseAdd("NaxUpdater/0.16.2");
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

    private async Task<string?> ReadReleaseThroughGitHubCliAsync(CancellationToken cancellationToken)
    {
        var executable = FindGitHubCli();
        if (executable is null)
        {
            return null;
        }
        try
        {
            var result = await new ProcessQueryRunner().RunAsync(
                executable,
                ["api", "--header", "Accept: application/vnd.github+json", $"repos/{recipe.Repository}/releases/latest"],
                TimeSpan.FromSeconds(20),
                cancellationToken);
            return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput)
                ? result.StandardOutput
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindGitHubCli()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GitHub CLI", "gh.exe")
        };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory, "gh.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }

    private async Task<UpdateCheckResult> CheckLatestTagFallbackAsync(
        InstalledApplication application,
        string apiError,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://github.com/{recipe.Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("NaxUpdater/0.16.2");
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
