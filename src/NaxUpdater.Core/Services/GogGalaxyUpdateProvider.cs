using NaxUpdater.Core.Models;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NaxUpdater.Core.Services;

public sealed class GogGalaxyUpdateProvider : IUpdateProvider
{
    private const string ExpectedSigner = "GOG  sp. z o.o";
    private readonly string _updateRoot;
    private readonly IAuthenticodeVerifier _authenticodeVerifier;
    private readonly Func<string, string?> _fileVersionReader;

    public GogGalaxyUpdateProvider(
        string? updateRoot = null,
        IAuthenticodeVerifier? authenticodeVerifier = null,
        Func<string, string?>? fileVersionReader = null)
    {
        _updateRoot = updateRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GOG.com",
            "Galaxy",
            "autoupdate-verified");
        _authenticodeVerifier = authenticodeVerifier ?? new NativeAuthenticodeVerifier();
        _fileVersionReader = fileVersionReader ?? ReadFileVersion;
    }

    public string Id => "gog-galaxy-native";
    public UpdateProviderDescriptor Descriptor { get; } = new(
        UpdateProviderAuthority.InstalledUpdateProtocol,
        100,
        "Signed updater and staged release state installed by GOG Galaxy",
        [ManagementMode.Unmanaged, ManagementMode.Registry, ManagementMode.WindowsInstaller, ManagementMode.DirectVendor]);

    public bool CanHandle(InstalledApplication application) =>
        application.DisplayName.Equals("GOG GALAXY", StringComparison.OrdinalIgnoreCase) &&
        application.Publisher?.Contains("GOG", StringComparison.OrdinalIgnoreCase) == true;

    public async Task<UpdateCheckResult> CheckAsync(
        InstalledApplication application,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(_updateRoot, "version.update.json");
        if (!File.Exists(manifestPath))
        {
            return Managed(application, "GOG Galaxy owns its update channel; no downloaded vendor update is currently staged.");
        }

        GogUpdateState? state;
        try
        {
            await using var stream = File.OpenRead(manifestPath);
            state = await JsonSerializer.DeserializeAsync<GogUpdateState>(stream, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Error(application, $"GOG's staged update metadata could not be read: {exception.Message}");
        }

        if (state is null || string.IsNullOrWhiteSpace(state.Version))
        {
            return Error(application, "GOG's staged update metadata contains no version.");
        }
        if (!state.State.Equals("Downloaded", StringComparison.OrdinalIgnoreCase))
        {
            return Managed(application, $"GOG Galaxy reports native updater state '{state.State}' for {state.Version}.", state.Version);
        }

        var updaterDirectory = Path.Combine(_updateRoot, "desktop-galaxy-updater");
        var updaterPath = Path.Combine(updaterDirectory, "GalaxyUpdater.exe");
        if (!File.Exists(updaterPath))
        {
            return Error(application, "GOG reports a downloaded update, but its staged GalaxyUpdater.exe is missing.", state.Version);
        }
        var signature = _authenticodeVerifier.Verify(updaterPath, ExpectedSigner);
        if (!signature.IsValid)
        {
            return Error(application, signature.Error ?? "Windows rejected GOG's staged updater signature.", state.Version);
        }
        var updaterVersion = _fileVersionReader(updaterPath);
        if (!string.Equals(updaterVersion, state.Version, StringComparison.OrdinalIgnoreCase))
        {
            return Error(
                application,
                $"GOG's staged metadata reports {state.Version}, but its signed updater reports {updaterVersion ?? "no version"}.",
                state.Version);
        }

        if (VersionOrder.Compare(state.Version, application.NormalizedVersion) <= 0)
        {
            return Current(application, state.Version, "GOG's signed native updater reports no newer staged client.");
        }

        return new UpdateCheckResult(
            application.Identity,
            application.DisplayName,
            application.NormalizedVersion,
            state.Version,
            UpdateStatus.NewerReleaseKnown,
            Id,
            "GOG Galaxy native updater",
            "application-managed",
            "Preserved by GOG Galaxy's staged updater",
            "x64",
            "stable",
            null,
            $"GOG staged update {state.Version} and Windows validated updater publisher '{signature.Signer}', but the adjacent runtime dependency set has no producer-authenticated manifest. Automatic elevated execution is blocked; use GOG Galaxy's own update action.",
            null,
            Applicability: UpdateApplicability.NotApplicable);
    }

    private UpdateCheckResult Managed(InstalledApplication application, string message, string? version = null) => new(
        application.Identity,
        application.DisplayName,
        application.NormalizedVersion,
        version,
        UpdateStatus.ManagedExternally,
        Id,
        "GOG Galaxy native updater",
        "application-managed",
        "Preserved by GOG Galaxy's updater",
        "x64",
        "stable",
        null,
        message,
        null);

    private UpdateCheckResult Current(InstalledApplication application, string version, string message) => new(
        application.Identity,
        application.DisplayName,
        application.NormalizedVersion,
        version,
        UpdateStatus.Current,
        Id,
        "GOG Galaxy native updater",
        "application-managed",
        "Preserved by GOG Galaxy's updater",
        "x64",
        "stable",
        null,
        message,
        null);

    private UpdateCheckResult Error(InstalledApplication application, string message, string? version = null) => new(
        application.Identity,
        application.DisplayName,
        application.NormalizedVersion,
        version,
        UpdateStatus.Error,
        Id,
        "GOG Galaxy native updater",
        "application-managed",
        "Preserved by GOG Galaxy's updater",
        "x64",
        "stable",
        null,
        message,
        null);

    private static string? ReadFileVersion(string path) =>
        FileVersionInfo.GetVersionInfo(path).ProductVersion?.Trim() is { Length: > 0 } version
            ? version
            : FileVersionInfo.GetVersionInfo(path).FileVersion?.Trim();

    private sealed record GogUpdateState(
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("version")] string Version);
}
