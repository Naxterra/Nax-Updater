using NaxUpdater.Core.Models;
using System.Diagnostics;
using System.Text.Json;

namespace NaxUpdater.Core.Services;

public sealed class MsixStoreUpdateProvider : IUpdateProvider
{
    private const string OpenAiPackageFamily = "OpenAI.Codex_2p2nqsd0c76g0";
    private const string OpenAiPackageIdentity = "OpenAI.Codex";
    private const string OpenAiProductId = "9PLM9XGG6VKS";
    private static readonly Uri OpenAiUpdateManifestUri = new(
        "https://persistent.oaistatic.com/codex-app-prod/windows-store-update.json");
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly IStorePackageDeploymentService _store;
    private readonly HttpClient _httpClient;

    public MsixStoreUpdateProvider(
        HttpClient? httpClient = null,
        IStorePackageDeploymentService? store = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
        _store = store ?? new StorePackageDeploymentService(_httpClient);
    }

    public string Id => "msix-store";
    public UpdateProviderDescriptor Descriptor { get; } = new(
        UpdateProviderAuthority.PlatformStore,
        100,
        "Exact installed MSIX package-family and Store product identity",
        [ManagementMode.Msix]);

    public bool CanHandle(InstalledApplication application) => application.ManagementMode == ManagementMode.Msix;
    public bool OwnsResultProviderId(string resultProviderId) =>
        resultProviderId.Equals(Id, StringComparison.Ordinal) ||
        resultProviderId.Equals("openai-codex-store", StringComparison.Ordinal);

    public async Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var packageFamily = PackageFamily(application);
        if (packageFamily is null)
        {
            return Result(application, null, UpdateStatus.ManagedExternally, null, null,
                "The installed MSIX package family could not be read.");
        }

        if (packageFamily.Equals(OpenAiPackageFamily, StringComparison.OrdinalIgnoreCase))
        {
            var openAiResult = await CheckOpenAiManifestAsync(application, packageFamily, cancellationToken);
            if (openAiResult is not null)
            {
                return openAiResult;
            }
        }

        var availability = await _store.CheckForUpdateAsync(
            packageFamily,
            application.DisplayName,
            application.Publisher,
            application.NormalizedVersion,
            PackageArchitecture(application),
            cancellationToken);
        if (!availability.IsResolved)
        {
            return Result(application, null, UpdateStatus.ManagedExternally, null, null, availability.Error);
        }
        if (!availability.IsUpdateAvailable || string.IsNullOrWhiteSpace(availability.ProductId))
        {
            return Result(application, null, UpdateStatus.Current, null, availability.ProductId,
                "Microsoft Store reports no applicable update for the installed package.");
        }

        var plan = new UpdateExecutionPlan(
                UpdateExecutionKind.StorePackage,
                null,
                null,
                null,
                "Microsoft Store",
                null,
                [],
                false,
                [],
                [],
                StoreProductId: availability.ProductId,
                StorePackageFamilyName: packageFamily,
                StorePublisher: application.Publisher);
        return Result(application, plan, UpdateStatus.Available, availability.AvailableVersion, availability.ProductId,
            "Microsoft Store reports an applicable update for the exact installed package family.");
    }

    private async Task<UpdateCheckResult?> CheckOpenAiManifestAsync(
        InstalledApplication application,
        string packageFamily,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(OpenAiUpdateManifestUri, cancellationToken);
            response.EnsureSuccessStatusCode();
            var finalUri = response.RequestMessage?.RequestUri ?? OpenAiUpdateManifestUri;
            if (finalUri.Scheme != Uri.UriSchemeHttps ||
                !finalUri.Host.Equals(OpenAiUpdateManifestUri.Host, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var manifest = ParseOpenAiManifest(document.RootElement);
            if (manifest is null)
            {
                return null;
            }

            var manifestNewer = VersionOrder.Compare(manifest.BuildVersion, application.NormalizedVersion) > 0;
            var storeAvailability = await _store.CheckForUpdateAsync(
                packageFamily,
                application.DisplayName,
                application.Publisher,
                application.NormalizedVersion,
                PackageArchitecture(application),
                cancellationToken);
            var storeApplicable = storeAvailability is
            {
                IsResolved: true,
                IsUpdateAvailable: true,
                ProductId: not null
            };
            var storeProductMismatch = storeApplicable &&
                                       !storeAvailability.ProductId!.Equals(manifest.StoreProductId, StringComparison.Ordinal);
            var storeTargetNewer = storeApplicable &&
                                   !string.IsNullOrWhiteSpace(storeAvailability.AvailableVersion) &&
                                   VersionOrder.Compare(storeAvailability.AvailableVersion, application.NormalizedVersion) > 0;
            var processBindings = ChatGptProcessBindings(application);
            var plan = storeTargetNewer && !storeProductMismatch
                ? new UpdateExecutionPlan(
                    UpdateExecutionKind.StorePackage,
                    null,
                    null,
                    null,
                    "Microsoft Store",
                    null,
                    [],
                    false,
                    [],
                    processBindings.Names,
                    StoreProductId: storeAvailability!.ProductId,
                    StorePackageFamilyName: packageFamily,
                    StorePublisher: application.Publisher,
                    RunningExecutablePaths: processBindings.Paths)
                : null;
            var status = storeProductMismatch
                ? UpdateStatus.Error
                : plan is not null
                    ? UpdateStatus.Available
                    : manifestNewer
                        ? UpdateStatus.NewerReleaseKnown
                        : storeApplicable && string.IsNullOrWhiteSpace(storeAvailability.AvailableVersion)
                            ? UpdateStatus.Error
                            : UpdateStatus.Current;
            var applicability = plan is not null
                ? UpdateApplicability.Applicable
                : status == UpdateStatus.Current
                    ? UpdateApplicability.NotRequired
                    : storeAvailability.IsResolved
                        ? UpdateApplicability.NotApplicable
                        : UpdateApplicability.Unknown;
            var availableVersion = plan is not null
                ? storeAvailability.AvailableVersion
                : manifestNewer
                    ? manifest.BuildVersion
                    : null;
            string? publishedVersion = null;
            if (manifestNewer && plan is null && !storeProductMismatch)
            {
                try
                {
                    publishedVersion = await new MicrosoftStoreProductMetadataClient(_httpClient).GetLatestPackageVersionAsync(
                        manifest.StoreProductId, packageFamily, PackageArchitecture(application),
                        application.NormalizedVersion, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { /* Optional rollout evidence must not hide the publisher's announcement. */ }
            }
            var awaitingPublication = publishedVersion is not null &&
                VersionOrder.Compare(publishedVersion, manifest.BuildVersion) < 0;
            var message = storeProductMismatch
                ? $"Microsoft Store resolved product {storeAvailability.ProductId}, but OpenAI's official release identity requires {manifest.StoreProductId}. Installation is blocked."
                : plan is not null
                    ? $"Microsoft Store confirms that build {storeAvailability.AvailableVersion} is applicable to this exact ChatGPT package family."
                    : awaitingPublication
                        ? $"OpenAI announces {manifest.BuildVersion}, but Microsoft Store currently publishes {publishedVersion} for this package. The newer Store package has not been published yet."
                    : manifestNewer
                        ? "OpenAI reports a newer build, but Microsoft Store does not currently offer a version-bound update to this exact package family."
                        : status == UpdateStatus.Error
                            ? "Microsoft Store reports an applicable update but did not expose a target package version; installation is blocked."
                            : "OpenAI's official manifest and Microsoft Store report that this package is current.";
            return new UpdateCheckResult(
                application.Identity,
                application.DisplayName,
                application.NormalizedVersion,
                availableVersion,
                status,
                "openai-codex-store",
                "OpenAI update manifest + Microsoft Store",
                "application-managed",
                "Preserved by Microsoft Store/MSIX package",
                "provider-selected",
                "stable",
                OpenAiUpdateManifestUri.ToString(),
                message,
                plan,
                Applicability: applicability,
                AvailabilityReason: awaitingPublication ? UpdateAvailabilityReason.AwaitingStorePublication : UpdateAvailabilityReason.None,
                PublishedPackageVersion: publishedVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Preserve the general Store metadata path if OpenAI's manifest is temporarily unavailable.
            return null;
        }
    }

    internal static OpenAiWindowsUpdateManifest? ParseOpenAiManifest(JsonElement root)
    {
        if (!root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1 ||
            !root.TryGetProperty("buildVersion", out var buildVersionElement) ||
            !root.TryGetProperty("storeProductId", out var productIdElement) ||
            !root.TryGetProperty("packageIdentity", out var packageIdentityElement))
        {
            return null;
        }
        var buildVersion = buildVersionElement.GetString()?.Trim();
        var productId = productIdElement.GetString()?.Trim();
        var packageIdentity = packageIdentityElement.GetString()?.Trim();
        return Version.TryParse(buildVersion, out _) &&
               productId?.Equals(OpenAiProductId, StringComparison.Ordinal) == true &&
               packageIdentity?.Equals(OpenAiPackageIdentity, StringComparison.Ordinal) == true
            ? new OpenAiWindowsUpdateManifest(buildVersion!, productId, packageIdentity)
            : null;
    }

    private UpdateCheckResult Result(
        InstalledApplication application,
        UpdateExecutionPlan? plan,
        UpdateStatus status,
        string? availableVersion,
        string? productId,
        string? message) => new(
            application.Identity,
            application.DisplayName,
            application.NormalizedVersion,
            availableVersion,
            status,
            Id,
            "Microsoft Store / MSIX",
            "application-managed",
            "Preserved by Microsoft Store/MSIX package",
            "provider-selected",
            "stable",
            string.IsNullOrWhiteSpace(productId) ? null : $"ms-windows-store://pdp/?ProductId={productId}",
            message,
            plan);

    private static string? PackageFamily(InstalledApplication application)
    {
        const string prefix = "msix:";
        return application.Identity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? application.Identity[prefix.Length..]
            : application.Evidence.FirstOrDefault(static item => item.Label == "MSIX package family")?.Value;
    }

    private static string? PackageArchitecture(InstalledApplication application) =>
        application.Evidence.FirstOrDefault(static item => item.Label == "MSIX package architecture")?.Value;

    private static (IReadOnlyList<string> Names, IReadOnlyList<string> Paths) ChatGptProcessBindings(
        InstalledApplication application)
    {
        var names = new List<string>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var codexRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI",
            "Codex",
            "bin")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        AddPath("ChatGPT", application.PrimaryInstallPath, requireCodexRoot: false);
        try
        {
            if (Directory.Exists(codexRoot))
            {
                foreach (var codexPath in Directory.EnumerateFiles(codexRoot, "codex.exe", SearchOption.AllDirectories).Take(32))
                {
                    AddPath("Codex", codexPath, requireCodexRoot: true);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Dynamic process enumeration below can still bind a currently running helper.
        }
        using var current = Process.GetCurrentProcess();
        foreach (var processName in new[] { "ChatGPT", "Codex" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        if (!process.HasExited && process.SessionId == current.SessionId)
                        {
                            AddPath(processName, process.MainModule?.FileName, requireCodexRoot: processName == "Codex");
                        }
                    }
                    catch
                    {
                        // Inaccessible processes are not added to an automatic close plan.
                    }
                }
            }
        }
        return (names, paths.ToArray());

        void AddPath(string processName, string? path, bool requireCodexRoot)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!Path.GetFileNameWithoutExtension(fullPath).Equals(processName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                if (requireCodexRoot)
                {
                    if (!fullPath.StartsWith(codexRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
                if (!names.Contains(processName, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(processName);
                }
                paths.Add(fullPath);
            }
            catch
            {
                // Invalid paths cannot participate in automatic process control.
            }
        }
    }
}

internal sealed record OpenAiWindowsUpdateManifest(
    string BuildVersion,
    string StoreProductId,
    string PackageIdentity);
