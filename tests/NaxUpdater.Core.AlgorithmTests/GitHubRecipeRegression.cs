using System.Net;
using System.Text;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

internal static class GitHubRecipeRegression
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        assert(GitHubReleaseUpdateProvider.MatchesDisplayName("Nax-TaskManager 2.0.0-alpha.23", "Nax-TaskManager") &&
               GitHubReleaseUpdateProvider.MatchesDisplayName("Nax-Copy", "Nax-Copy") &&
               !GitHubReleaseUpdateProvider.MatchesDisplayName("Nax-Copy Helper", "Nax-Copy") &&
               !GitHubReleaseUpdateProvider.MatchesDisplayName("Nax-TaskManagerX 2.0", "Nax-TaskManager"),
            "A recipe matched another product's name, or missed a version-suffixed registration.");

        // An installed pre-release follows its channel; unsigned releases rely on the mandatory digest.
        var digest = new string('a', 64);
        string Release(string tag, bool prerelease) =>
            $"{{\"tag_name\":\"{tag}\",\"prerelease\":{(prerelease ? "true" : "false")},\"draft\":false,\"html_url\":\"https://github.com/o/r/releases/tag/{tag}\"," +
            $"\"assets\":[{{\"name\":\"App-{tag.TrimStart('v')}-setup.exe\",\"url\":\"https://api.github.com/repos/o/r/releases/assets/7\"," +
            $"\"browser_download_url\":\"https://github.com/o/r/releases/download/{tag}/App-{tag.TrimStart('v')}-setup.exe\",\"digest\":\"sha256:{digest}\"}}]}}";
        using var http = new HttpClient(new Respond(request => request.RequestUri!.AbsolutePath switch
        {
            "/repos/o/r/releases/latest" => Release("v1.9.0", false),
            "/repos/o/r/releases" => "[" + Release("v2.0.0-alpha.24", true) + "," + Release("v2.0.0-alpha.23", true) + "," + Release("v1.9.0", false) + "]",
            _ => null
        }));
        var recipe = new GitHubUpdateRecipe
        {
            Id = "o.r", DisplayName = "App", PublisherContains = "Owner", Repository = "o/r",
            AssetNamePattern = @"^App-(?<version>[0-9]+(?:\.[0-9]+)+(?:-[0-9A-Za-z.]+)?)-setup\.exe$",
            Architecture = "x64", Unsigned = true, FollowInstalledPrerelease = true, PrivateRepository = true,
            InstallerKind = UpdateExecutionKind.DownloadedExe, InstallerArguments = ["/VERYSILENT"], CurrentUserInstallerArguments = ["/VERYSILENT"]
        };
        var provider = new GitHubReleaseUpdateProvider(http, recipe, _ => Task.FromResult<string?>(null));
        InstalledApplication App(string version) => new("fixture:app", "App " + version, "Owner", version, version, "fixture",
            Environment.ProcessPath, "fixture", null, null, InstallScope.CurrentUser, ManagementMode.Registry, ConfidenceLevel.High, false, [], null, []);
        var alpha = await provider.CheckAsync(App("2.0.0-alpha.23"), CancellationToken.None);
        assert(alpha.Status == UpdateStatus.Available && alpha.AvailableVersion == "2.0.0-alpha.24" &&
               alpha.ExecutionPlan is { RequireAuthenticode: false, ExpectedSigner: null, AuthenticatedGitHubAsset: true, Sha256: var sha } && sha == digest &&
               alpha.ExecutionPlan.DownloadUri!.Host == "api.github.com" && alpha.ExecutionPlan.AllowedDownloadHosts.Contains("api.github.com"),
            "An installed pre-release did not follow its channel, or the unsigned private plan lost its digest or API asset URL.");
        var stable = await provider.CheckAsync(App("1.8.0"), CancellationToken.None);
        assert(stable.AvailableVersion == "1.9.0", "A stable installation was moved onto the pre-release channel.");

        // Audit of 2026-10-22, finding 3: an installed prerelease never falls back to the
        // stable release when its channel cannot be read, and an older release with a
        // legacy installer name settles an installation without requiring that installer.
        string Legacy(string tag) =>
            $"{{\"tag_name\":\"{tag}\",\"prerelease\":false,\"draft\":false,\"html_url\":\"https://github.com/o/r/releases/tag/{tag}\"," +
            $"\"assets\":[{{\"name\":\"OldApp_{tag.TrimStart('v')}_Installer.msi\",\"browser_download_url\":\"https://github.com/o/r/releases/download/{tag}/x.msi\"}}]}}";
        using var emptyChannel = new HttpClient(new Respond(request => request.RequestUri!.AbsolutePath switch
        {
            "/repos/o/r/releases/latest" => Legacy("v1.0.0"),
            "/repos/o/r/releases" => "[]",
            _ => null
        }));
        var channelProvider = new GitHubReleaseUpdateProvider(emptyChannel, recipe, _ => Task.FromResult<string?>(null));
        var unreadable = await channelProvider.CheckAsync(App("2.0.0-alpha.32"), CancellationToken.None);
        assert(unreadable.Status == UpdateStatus.Error && unreadable.ExecutionPlan is null && unreadable.Message?.Contains("prerelease channel") == true,
            $"An installed prerelease was checked against the stable release when its channel was empty: {unreadable.Status} · {unreadable.Message}");
        var older = await channelProvider.CheckAsync(App("2.0.0"), CancellationToken.None);
        assert(older is { Status: UpdateStatus.Current, ExecutionPlan: null },
            $"An older release with a legacy installer name was reported as a failed check: {older.Status} · {older.Message}");
        var newer = await channelProvider.CheckAsync(App("0.9.0"), CancellationToken.None);
        assert(newer.Status == UpdateStatus.Error && newer.Message?.Contains("No release asset matched") == true,
            "A newer release without a matching installer was not reported as a failed check.");

        // The token goes only to api.github.com; only the signed redirect target is used.
        string? sentAuthorization = null;
        using var resolver = new HttpClient(new Respond(request =>
        {
            sentAuthorization = request.Headers.Authorization?.ToString();
            return "redirect:https://objects.githubusercontent.com/signed?x=1";
        }));
        var signed = await UpdatePackageDownloader.ResolveGitHubAssetAsync(new Uri("https://api.github.com/repos/o/r/releases/assets/7"),
            CancellationToken.None, _ => Task.FromResult<string?>("gho_fixturetokenvalue123456"), resolver);
        assert(signed.Host == "objects.githubusercontent.com" && sentAuthorization == "Bearer gho_fixturetokenvalue123456",
            "The private asset was not resolved through an authenticated API request.");
        try
        {
            await UpdatePackageDownloader.ResolveGitHubAssetAsync(new Uri("https://evil.example/repos/o/r/releases/assets/7"),
                CancellationToken.None, _ => Task.FromResult<string?>("gho_fixturetokenvalue123456"), resolver);
            assert(false, "The GitHub token was offered to a host other than api.github.com.");
        }
        catch (InvalidOperationException) { assert(true, "Foreign asset host rejected."); }
    }

    private sealed class Respond(Func<HttpRequestMessage, string?> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var text = body(request);
            if (text is null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
            if (text.StartsWith("redirect:", StringComparison.Ordinal))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found) { RequestMessage = request };
                redirect.Headers.Location = new Uri(text["redirect:".Length..]);
                return Task.FromResult(redirect);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(text, Encoding.UTF8, "application/json") });
        }
    }
}
