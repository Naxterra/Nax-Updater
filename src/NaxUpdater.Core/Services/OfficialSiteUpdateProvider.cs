using System.Text.RegularExpressions;
using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

// First in the owner's source order: the publisher's own site. A recipe names where
// the publisher lists its versions, where the installer and its published hash
// are, and which signer to expect. Nothing is installed without the publisher's
// hash; the signer is required too unless the publisher ships unsigned files.
public sealed class OfficialSiteUpdateProvider(HttpClient httpClient, OfficialSiteRecipe recipe, string? appDataRoot = null) : IUpdateProvider
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(2);

    public string Id => $"official:{recipe.Id}";
    public UpdateProviderDescriptor Descriptor { get; } = new(
        UpdateProviderAuthority.ProducerRelease,
        95,
        "Publisher's official download site with its published installer hash",
        [ManagementMode.Unmanaged, ManagementMode.Registry, ManagementMode.WindowsInstaller, ManagementMode.DirectVendor]);

    public bool CanHandle(InstalledApplication application) =>
        NameMatch(application) is not null &&
        (string.IsNullOrWhiteSpace(recipe.PublisherContains) ||
         application.Publisher?.Contains(recipe.PublisherContains, StringComparison.OrdinalIgnoreCase) == true);

    private Match? NameMatch(InstalledApplication application)
    {
        var match = Regex.Match(application.DisplayName, recipe.DisplayNamePattern, RegexOptions.CultureInvariant, PatternTimeout);
        return match.Success ? match : null;
    }

    public async Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        var installed = application.NormalizedVersion;
        if (string.IsNullOrWhiteSpace(installed)) return Error(application, "The installed version could not be read.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        Capture(NameMatch(application), values);

        string document;
        try { document = await ReadAsync(recipe.VersionUrl, cancellationToken); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return Error(application, $"The publisher's version source could not be read: {exception.Message}");
        }

        var installedMajor = installed.Split('.', '-')[0];
        var candidates = Regex.Matches(document, recipe.VersionPattern, RegexOptions.CultureInvariant, PatternTimeout)
            .Select(m => (Match: m, Version: VersionOf(m)))
            .Where(static c => c.Version is { Length: > 0 } v && char.IsDigit(v[0]))
            .Where(c => !recipe.StayOnInstalledMajor || c.Version!.Split('.', '-')[0] == installedMajor)
            .ToArray();
        if (candidates.Length == 0) return Error(application, "The publisher's version source lists no matching release.");
        var (latest, version) = candidates.OrderByDescending(c => c.Version!, Comparer<string>.Create(VersionOrder.Compare)).First();
        Capture(latest, values);
        values["version"] = version!;
        values["version_"] = version!.Replace('.', '_');
        values["major"] = version.Split('.', '-')[0];
        var notes = recipe.ReleaseNotesUrl is null ? recipe.VersionUrl : Expand(recipe.ReleaseNotesUrl, values, escape: false);

        if (VersionOrder.Compare(version, installed) <= 0)
            return Result(application, version, UpdateStatus.Current, notes, $"The publisher's site lists {version} as its newest release.", null);

        // A staged rollout is offered exactly when the application's own updater
        // would offer it to this installation (see ElectronStagedRollout).
        if (recipe.RolloutPattern is not null &&
            Regex.Match(document, recipe.RolloutPattern, RegexOptions.CultureInvariant, PatternTimeout) is { Success: true } rollout &&
            int.TryParse(rollout.Groups["percent"].Value, out var percent) && percent < 100 &&
            ElectronStagedRollout.Position(application, appDataRoot) is var position &&
            (position is null || !ElectronStagedRollout.Includes(percent, position.Value)))
            return Result(application, version, UpdateStatus.NewerReleaseKnown, notes,
                ElectronStagedRollout.HeldMessage(version, percent, position), null,
                UpdateApplicability.NotApplicable, UpdateAvailabilityReason.StagedRollout);

        if (recipe.DownloadPageUrl is not null && recipe.DownloadPattern is not null)
        {
            try
            {
                var page = await ReadAsync(Expand(recipe.DownloadPageUrl, values, escape: false), cancellationToken);
                var link = Regex.Match(page, Expand(recipe.DownloadPattern, values, escape: true), RegexOptions.CultureInvariant, PatternTimeout);
                if (!link.Success) return Error(application, "The publisher's download page no longer lists the installer link.", version);
                Capture(link, values);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return Error(application, $"The publisher's download page could not be read: {exception.Message}", version);
            }
        }

        if (!Uri.TryCreate(Expand(recipe.DownloadUrl, values, escape: false), UriKind.Absolute, out var download) ||
            download.Scheme != Uri.UriSchemeHttps || !recipe.AllowedHosts.Contains(download.Host, StringComparer.OrdinalIgnoreCase))
            return Error(application, "The publisher's download link is not an allowed HTTPS address.", version);

        if (recipe.SignatureOnly && (recipe.Unsigned || string.IsNullOrWhiteSpace(recipe.ExpectedSigner)))
            return Error(application, "A signature-only recipe must name the publisher's exact signer.", version);
        string? hash = latest.Groups["hash"].Success ? latest.Groups["hash"].Value : null;
        if (!recipe.SignatureOnly && hash is null && recipe.HashPattern is not null)
        {
            try
            {
                var hashDocument = recipe.HashUrl is null ? document : await ReadAsync(Expand(recipe.HashUrl, values, escape: false), cancellationToken);
                var found = Regex.Match(hashDocument, Expand(recipe.HashPattern, values, escape: true), RegexOptions.CultureInvariant, PatternTimeout);
                if (found.Success) hash = found.Groups["hash"].Value;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested) { }
        }
        hash = recipe.SignatureOnly ? null : NormalizeHash(hash);
        var sha512 = recipe.HashAlgorithm.Equals("sha512", StringComparison.OrdinalIgnoreCase);
        if (!recipe.SignatureOnly && (hash is null || hash.Length != (sha512 ? 128 : 64)))
            return Result(application, version, UpdateStatus.NewerReleaseKnown, notes,
                $"The publisher lists {version}, but its published installer hash could not be read; installation is blocked until it can.", null,
                UpdateApplicability.NotApplicable, UpdateAvailabilityReason.AwaitingReleaseVerification);

        var architecture = InstalledApplicationMetadata.Architecture(application);
        // "neutral": the installed executables do not reveal the product's architecture
        // (FileBot's x64 MSI installs 32-bit launchers).
        if (architecture is not null && !recipe.Architecture.Equals("neutral", StringComparison.OrdinalIgnoreCase) &&
            !architecture.Equals(recipe.Architecture, StringComparison.OrdinalIgnoreCase))
            return Result(application, version, UpdateStatus.NewerReleaseKnown, notes,
                "The publisher's installer does not match the installed architecture.", null, UpdateApplicability.NotApplicable);
        var currentUser = application.Scope == InstallScope.CurrentUser;
        if (currentUser && recipe.RequiresElevation && !recipe.AlwaysElevate && recipe.CurrentUserInstallerArguments is null)
            return Result(application, version, UpdateStatus.NewerReleaseKnown, notes,
                "No installer configuration preserves this installation's per-user scope.", null, UpdateApplicability.NotApplicable);

        var fileName = recipe.FileName is null
            ? Path.GetFileName(Uri.UnescapeDataString(download.AbsolutePath))
            : Path.GetFileName(Expand(recipe.FileName, values, escape: false));
        var plan = new UpdateExecutionPlan(
            recipe.InstallerKind, download, fileName, sha512 ? null : hash, recipe.Unsigned ? null : recipe.ExpectedSigner, null,
            (currentUser ? recipe.CurrentUserInstallerArguments ?? recipe.InstallerArguments : recipe.InstallerArguments).ToArray(),
            (!currentUser || recipe.AlwaysElevate) && recipe.RequiresElevation, recipe.AllowedHosts.ToArray(), recipe.RunningProcessNames.ToArray(),
            Sha512: sha512 ? hash : null, RequireAuthenticode: !recipe.Unsigned, AllowHashVerifiedRedirects: recipe.AllowMirrorRedirects,
            NestedInstallerRelativePath: recipe.NestedInstaller is null ? null : Expand(recipe.NestedInstaller, values, escape: false),
            SignatureOnly: recipe.SignatureOnly);
        return Result(application, version, UpdateStatus.Available, notes,
            recipe.SignatureOnly
                ? $"The publisher's site offers {version}. It publishes no installer hash, so the exact {recipe.ExpectedSigner} signature is the verified proof (signature-only)."
            : recipe.Unsigned
                ? $"The publisher's site offers {version}; its published {recipe.HashAlgorithm.ToUpperInvariant()} is verified before installation (the publisher ships unsigned installers)."
                : $"The publisher's site offers {version}; its published {recipe.HashAlgorithm.ToUpperInvariant()} and the {recipe.ExpectedSigner} signature are verified before installation.",
            plan);
    }

    private string? VersionOf(Match match)
    {
        if (recipe.VersionFormat is null) return match.Groups["version"].Success ? match.Groups["version"].Value : null;
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        Capture(match, groups);
        var version = Expand(recipe.VersionFormat, groups, escape: false);
        return version.Contains('{') ? null : version;
    }

    private async Task<string> ReadAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("NaxUpdater/0.17.19");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        // Version sources are small documents; never read a large file by accident.
        if (response.Content.Headers.ContentLength > 8 * 1024 * 1024)
            throw new HttpRequestException("The version source is unexpectedly large.");
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private string? NormalizeHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (recipe.HashEncoding.Equals("base64", StringComparison.OrdinalIgnoreCase))
        {
            try { return Convert.ToHexString(Convert.FromBase64String(value)).ToLowerInvariant(); }
            catch (FormatException) { return null; }
        }
        return Regex.IsMatch(value, "^[0-9A-Fa-f]+$", RegexOptions.CultureInvariant) ? value.ToLowerInvariant() : null;
    }

    private static void Capture(Match? match, Dictionary<string, string> values)
    {
        if (match is null) return;
        foreach (Group group in match.Groups)
            if (group.Success && !int.TryParse(group.Name, out _)) values[group.Name] = group.Value;
    }

    internal static string Expand(string template, IReadOnlyDictionary<string, string> values, bool escape) =>
        Regex.Replace(template, @"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}", m =>
            values.TryGetValue(m.Groups["name"].Value, out var value) ? escape ? Regex.Escape(value) : value : m.Value,
            RegexOptions.CultureInvariant);

    private UpdateCheckResult Result(InstalledApplication application, string? available, UpdateStatus status, string? notes,
        string message, UpdateExecutionPlan? plan, UpdateApplicability applicability = UpdateApplicability.Applicable,
        UpdateAvailabilityReason reason = UpdateAvailabilityReason.None) => new(
        application.Identity, application.DisplayName, application.NormalizedVersion, available, status, Id,
        "Official publisher site", recipe.Language, "Publisher installer", recipe.Architecture, "stable", notes, message, plan,
        Applicability: applicability, AvailabilityReason: reason);

    private UpdateCheckResult Error(InstalledApplication application, string message, string? available = null) =>
        Result(application, available, UpdateStatus.Error, recipe.VersionUrl, message, null);
}
