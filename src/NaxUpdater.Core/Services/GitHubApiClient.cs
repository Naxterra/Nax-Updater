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
            request.Headers.UserAgent.ParseAdd("NaxUpdater/0.16.5");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.IsSuccessStatusCode) return await response.Content.ReadAsStringAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { }
        return await (cliReader ?? ReadUsingCliAsync)(endpoint, token);
    }

    public static async Task<string?> ReadUsingCliAsync(string endpoint, CancellationToken token)
    {
        // Pass only a repository API path as one argument. Credentials stay in gh.
        if (!endpoint.StartsWith("repos/", StringComparison.Ordinal) || endpoint.Contains("..") ||
            endpoint.Any(char.IsControl)) return null;
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GitHub CLI", "gh.exe")
        }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(path => Path.Combine(path.Trim('"'), "gh.exe")));
        var executable = candidates.FirstOrDefault(File.Exists);
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
}
