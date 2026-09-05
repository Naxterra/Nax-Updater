using NaxUpdater.Core.Models;
using System.Text.RegularExpressions;

namespace NaxUpdater.Core.Services;

public sealed class MacriumUpdateProvider(HttpClient httpClient) : IUpdateProvider
{
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
        using var response = await ReadReleaseAsync(endpoint, token);
        response.EnsureSuccessStatusCode();
        var final = response.RequestMessage?.RequestUri ?? endpoint;
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
    private async Task<HttpResponseMessage> ReadReleaseAsync(Uri endpoint, CancellationToken token)
    {
        for (var redirect = 0; redirect < 5; redirect++)
        {
            var response = await httpClient.GetAsync(endpoint, token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var target = new Uri(endpoint, location);
                response.Dispose();
                if (target.Host != "updates.macrium.com" || target.Scheme is not ("http" or "https"))
                    throw new InvalidOperationException("Macrium redirected outside its official release host.");
                // The vendor's ASP endpoint emits an HTTP Location. Request the
                // same resource over HTTPS rather than downgrading transport.
                endpoint = new UriBuilder(target) { Scheme = "https", Port = -1 }.Uri;
                continue;
            }
            return response;
        }
        throw new InvalidOperationException("Macrium's release redirect did not resolve.");
    }
    internal static string? ParseReleaseVersion(Uri uri, string major)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "updates.macrium.com") return null;
        var match = Regex.Match(uri.AbsolutePath, @"^/reflect/v(?<major>\d+)/v(?<version>\d+\.\d+\.\d+)/details\k<version>\.htm$", RegexOptions.IgnoreCase);
        return match.Success && match.Groups["major"].Value == major && match.Groups["version"].Value.StartsWith(major + ".", StringComparison.Ordinal)
            ? match.Groups["version"].Value : null;
    }
}
