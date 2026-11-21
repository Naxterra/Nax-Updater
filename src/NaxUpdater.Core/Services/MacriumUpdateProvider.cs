using NaxUpdater.Core.Models;
using System.Net;
using System.Text.RegularExpressions;

namespace NaxUpdater.Core.Services;

public sealed class MacriumUpdateProvider(HttpClient httpClient) : IUpdateProvider
{
    private static readonly HttpClient ReleaseClient = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(12),
        DefaultRequestHeaders = { { "User-Agent", "NaxUpdater" } }
    };

    public MacriumUpdateProvider() : this(ReleaseClient) { }

    internal Func<Uri, CancellationToken, Task<HttpResponseMessage?>> ReadBlockedReleaseAsync { get; init; } = ReadWithWindowsCurlAsync;

    public string Id => "macrium-release";
    public UpdateProviderDescriptor Descriptor { get; } = new(UpdateProviderAuthority.ProducerRelease, 100,
        "Macrium's official patch release for the installed major version",
        [ManagementMode.Registry, ManagementMode.WindowsInstaller, ManagementMode.NativeSelfUpdater]);
    public bool CanHandle(InstalledApplication app) => app.DisplayName.Equals("Macrium Reflect Home", StringComparison.OrdinalIgnoreCase) &&
        (app.Publisher?.Contains("Macrium", StringComparison.OrdinalIgnoreCase) == true ||
         app.Publisher?.Equals("Paramount Software (UK) Ltd.", StringComparison.OrdinalIgnoreCase) == true);
    public async Task<UpdateCheckResult> CheckAsync(InstalledApplication app, CancellationToken token)
    {
        var major = Regex.Match(app.NormalizedVersion ?? "", @"^\d+").Value;
        if (major is not ("8" or "10")) throw new InvalidOperationException("The installed Macrium release line could not be verified.");
        var endpoint = new Uri($"https://updates.macrium.com/reflect/v{major}/latest_release_notes.asp");
        var final = await ReadReleaseAsync(endpoint, token);
        var version = ParseReleaseVersion(final, major);
        if (version is null) throw new InvalidOperationException("Macrium's response did not identify an official patch for the installed major version.");
        var newer = VersionOrder.Compare(version, app.NormalizedVersion) > 0;
        return new(app.Identity, app.DisplayName, app.NormalizedVersion, newer ? version : null,
            newer ? UpdateStatus.NewerReleaseKnown : UpdateStatus.Current, Id, "Macrium official patch releases",
            "application-managed", "Preserved by Macrium Reflect", "provider-selected", "stable", final.AbsoluteUri,
            newer ? "Macrium publishes a newer patch for this major version. The installed Reflect updater must apply it; no undocumented update command is executed."
                : $"Macrium's official release for this major version is {version}; the installed version is current.", null,
            Applicability: newer ? UpdateApplicability.NotApplicable : UpdateApplicability.NotRequired);
    }
    private async Task<Uri> ReadReleaseAsync(Uri endpoint, CancellationToken token)
    {
        for (var redirect = 0; redirect < 5; redirect++)
        {
            using var primary = await httpClient.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, token);
            using var alternate = primary.StatusCode == HttpStatusCode.Forbidden
                ? await ReadBlockedReleaseAsync(endpoint, token) : null;
            var response = alternate ?? primary;
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var target = new Uri(endpoint, location);
                if (target.Host != "updates.macrium.com" || target.Scheme is not ("http" or "https") ||
                    !target.IsDefaultPort || target.UserInfo.Length != 0)
                    throw new InvalidOperationException("Macrium redirected outside its official release host.");
                // The vendor's ASP endpoint emits an HTTP Location. Request the
                // same resource over HTTPS rather than downgrading transport.
                endpoint = new UriBuilder(target) { Scheme = "https", Port = -1 }.Uri;
                continue;
            }
            response.EnsureSuccessStatusCode();
            return response.RequestMessage?.RequestUri ?? endpoint;
        }
        throw new InvalidOperationException("Macrium's release redirect did not resolve.");
    }

    private static async Task<HttpResponseMessage?> ReadWithWindowsCurlAsync(Uri endpoint, CancellationToken token)
    {
        // Macrium's public release site can challenge this HTTP client with 403.
        // Read the same resource with Windows curl, using the same redirect policy.
        var curl = Path.Combine(Environment.SystemDirectory, "curl.exe");
        if (!File.Exists(curl) || HttpClient.DefaultProxy.GetProxy(endpoint) is { } proxy && proxy != endpoint)
            return null;

        var result = await new ProcessQueryRunner().RunAsync(curl,
            ["--disable", "--silent", "--show-error", "--max-time", "7", "--max-filesize", "2000000",
             "--proto", "=https", "--user-agent", "NaxUpdater", "--output", "NUL",
             "--write-out", "%{http_code}\\n%{redirect_url}", "--", endpoint.AbsoluteUri],
            TimeSpan.FromSeconds(8), token);
        var lines = result.StandardOutput.Trim().Split('\n', 2, StringSplitOptions.TrimEntries);
        if (result.ExitCode != 0 || !int.TryParse(lines[0], out var status) || status is < 200 or >= 400)
            return null;

        var response = new HttpResponseMessage((HttpStatusCode)status);
        if (lines.Length == 2 && Uri.TryCreate(lines[1], UriKind.Absolute, out var location))
            response.Headers.Location = location;
        return response;
    }

    internal static string? ParseReleaseVersion(Uri uri, string major)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "updates.macrium.com" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0) return null;
        var match = Regex.Match(uri.AbsolutePath, @"^/reflect/v(?<major>\d+)/v(?<version>\d+\.\d+\.\d+)/details\k<version>\.htm$", RegexOptions.IgnoreCase);
        return match.Success && match.Groups["major"].Value == major && match.Groups["version"].Value.StartsWith(major + ".", StringComparison.Ordinal)
            ? match.Groups["version"].Value : null;
    }
}
