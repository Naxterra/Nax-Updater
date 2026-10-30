using System.Text;

namespace NaxUpdater.Core.Services;

internal static class GitHubApiClient
{
    public static async Task<string?> ReadAsync(
        HttpClient client, string endpoint, CancellationToken token,
        Func<string, CancellationToken, Task<string?>>? cliReader = null)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/" + endpoint);
            request.Headers.UserAgent.ParseAdd("NaxUpdater/0.17.47");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.IsSuccessStatusCode) return await response.Content.ReadAsStringAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { }
        return await (cliReader ?? ReadUsingCliAsync)(endpoint, token);
    }

    // The signed-in GitHub CLI account's token, for private release assets only.
    public static async Task<string?> ReadTokenUsingCliAsync(CancellationToken token)
    {
        var executable = FindCli();
        if (executable is null) return null;
        try
        {
            var result = await new ProcessQueryRunner().RunAsync(executable, ["auth", "token", "--hostname", "github.com"], TimeSpan.FromSeconds(15), token);
            var value = result.StandardOutput.Trim();
            return result.ExitCode == 0 && value.Length is > 20 and < 256 && !value.Any(char.IsWhiteSpace) ? value : null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    public static async Task<string?> ReadUsingCliAsync(string endpoint, CancellationToken token)
    {
        // Pass only a repository API path as one argument. Credentials stay in gh.
        if (!endpoint.StartsWith("repos/", StringComparison.Ordinal) || endpoint.Contains("..") ||
            endpoint.Any(char.IsControl)) return null;
        var executable = FindCli();
        if (executable is null) return null;
        try
        {
            var result = await new ProcessQueryRunner().RunAsync(executable,
                ["api", "--header", "Accept: application/vnd.github+json", endpoint], TimeSpan.FromSeconds(20), token);
            return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardOutput : null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private static string? FindCli()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GitHub CLI", "gh.exe")
        }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(path => Path.Combine(path.Trim('"'), "gh.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }
}
