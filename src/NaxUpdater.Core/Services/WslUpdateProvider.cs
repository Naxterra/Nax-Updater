using NaxUpdater.Core.Models;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace NaxUpdater.Core.Services;

public sealed class WslUpdateProvider(HttpClient httpClient) : IUpdateProvider
{
    internal const string PackageFamily = "MicrosoftCorporationII.WindowsSubsystemForLinux_8wekyb3d8bbwe";
    public string Id => "wsl-native";
    public UpdateProviderDescriptor Descriptor { get; } = new(UpdateProviderAuthority.InstalledUpdateProtocol, 100,
        "Exact WSL package identity, Microsoft's stable release and signed Windows update command", [ManagementMode.Msix]);
    public bool CanHandle(InstalledApplication app) => app.ManagementMode == ManagementMode.Msix &&
        app.Identity.Equals("msix:" + PackageFamily, StringComparison.OrdinalIgnoreCase);

    public async Task<UpdateCheckResult> CheckAsync(InstalledApplication app, CancellationToken token)
    {
        if (!CanHandle(app)) throw new InvalidOperationException("This is not the installed WSL package.");
        var json = await GitHubApiClient.ReadAsync(httpClient, "repos/microsoft/WSL/releases/latest", token)
            ?? throw new InvalidOperationException("Microsoft's WSL release metadata could not be retrieved.");
        using var document = JsonDocument.Parse(json);
        var release = document.RootElement;
        var tag = release.GetProperty("tag_name").GetString();
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean() ||
            release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean() ||
            !Version.TryParse(tag, out var parsed))
            throw new InvalidOperationException("Microsoft did not return a stable WSL version.");
        var version = parsed.ToString();
        var architecture = app.Evidence.FirstOrDefault(e => e.Label == "MSIX package architecture")?.Value?.ToLowerInvariant();
        var hostArchitecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        if (architecture != hostArchitecture)
            throw new InvalidOperationException("The installed WSL package architecture does not match the native Windows updater.");
        var releaseUrl = $"https://github.com/microsoft/WSL/releases/tag/{version}";
        if (release.GetProperty("html_url").GetString() != releaseUrl)
            throw new InvalidOperationException("The WSL release URL is not the expected Microsoft repository release.");
        var newer = VersionOrder.Compare(version, app.NormalizedVersion) > 0;
        // Use WSL's installed, embedded-signed executable, not the Windows
        // System32 forwarding stub (which is signed through a system catalog).
        var executable = app.PrimaryInstallPath;
        if (newer && (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable) ||
            !Path.GetFileName(executable).Equals("wsl.exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The installed native WSL updater was not found.");
        var plan = newer ? new UpdateExecutionPlan(UpdateExecutionKind.NativeCommand, null, null, null,
            "Microsoft Corporation", executable, ["--update", "--web-download"], true, [], [],
            NativeWorkingDirectory: Path.GetDirectoryName(executable)) : null;
        return new(app.Identity, app.DisplayName, app.NormalizedVersion, newer ? version : null,
            newer ? UpdateStatus.Available : UpdateStatus.Current, Id, "Microsoft WSL release + native Windows updater",
            "application-managed", "Preserved by Microsoft's WSL updater", architecture, "stable", releaseUrl,
            "Microsoft's signed wsl.exe updates WSL from Microsoft's GitHub release using --update --web-download. " +
            "Linux distributions are not unregistered or reinstalled. The installed WSL package version is verified afterward; the native updater may install a newer stable release.", plan);
    }
}
