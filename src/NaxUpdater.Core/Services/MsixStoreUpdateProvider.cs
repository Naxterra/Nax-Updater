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
    private readonly INativeStoreUpdateService? _nativeStore;

    public MsixStoreUpdateProvider(
        HttpClient? httpClient = null,
        IStorePackageDeploymentService? store = null,
        INativeStoreUpdateService? nativeStore = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
        _store = store ?? new StorePackageDeploymentService(_httpClient);
        _nativeStore = nativeStore ?? (store is null ? new NativeStoreUpdateService(_httpClient) : null);
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

            var storeAvailability = await _store.CheckForUpdateAsync(packageFamily, application.DisplayName,
                application.Publisher, application.NormalizedVersion, PackageArchitecture(application), cancellationToken);
            var storeApplicable = storeAvailability is { IsResolved: true, IsUpdateAvailable: true, ProductId: not null };
            var storeProductMismatch = storeApplicable && storeAvailability.ProductId != manifest.StoreProductId;
            var storeTargetNewer = storeApplicable && !string.IsNullOrWhiteSpace(storeAvailability.AvailableVersion) &&
                VersionOrder.Compare(storeAvailability.AvailableVersion, application.NormalizedVersion) > 0;
            var bindings = ChatGptProcessBindings(application);
            UpdateExecutionPlan? plan = storeTargetNewer && !storeProductMismatch
                ? new(UpdateExecutionKind.StorePackage, null, null, null, "Microsoft Store", null, [], false, [],
                    bindings.Names, StoreProductId: storeAvailability.ProductId, StorePackageFamilyName: packageFamily,
                    StorePublisher: application.Publisher, RunningExecutablePaths: bindings.Paths)
                : null;
            string? targetVersion = plan is null ? null : storeAvailability.AvailableVersion;
            string? publishedVersion = null;
            string? nativeError = null;
            var nativeCheckFailed = false;
            var nativeOfferChecked = false;
            if (plan is null && !storeProductMismatch)
            {
                var metadata = new MicrosoftStoreProductMetadataClient(_httpClient);
                try
                {
                    publishedVersion = await metadata.GetLatestPackageVersionAsync(manifest.StoreProductId, packageFamily,
                        PackageArchitecture(application), application.NormalizedVersion, cancellationToken);
                    if (_nativeStore is not null && !string.IsNullOrWhiteSpace(publishedVersion) &&
                        VersionOrder.Compare(publishedVersion, application.NormalizedVersion) > 0)
                    {
                        var package = await metadata.GetPublishedPackageAsync(manifest.StoreProductId, packageFamily,
                            PackageArchitecture(application), application.NormalizedVersion, cancellationToken);
                        if (package is not null)
                        {
                            publishedVersion = package.Version;
                            var offer = await _nativeStore.CheckAsync(package, cancellationToken);
                            nativeOfferChecked = true;
                            if (offer.IsAvailable)
                            {
                                targetVersion = package.Version;
                                plan = new(UpdateExecutionKind.NativeStorePackage, null, null, null, "Microsoft Store", null,
                                    [], false, [], bindings.Names, StoreProductId: package.ProductId,
                                    StorePackageFamilyName: packageFamily, StorePublisher: application.Publisher,
                                    RunningExecutablePaths: bindings.Paths, NativeStoreTarget: package);
                            }
                            else { nativeError = offer.Error; nativeCheckFailed = offer.CheckFailed; }
                        }
                        else { nativeError = "The published Store package could not be matched to the installed architecture."; nativeCheckFailed = true; }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) { nativeError = exception.Message; nativeCheckFailed = true; }
            }

            // Announcement/catalog versions are evidence, not updates for this
            // installation. Only a confirmed applicable offer supplies a target.
            var status = storeProductMismatch || nativeCheckFailed ? UpdateStatus.Error :
                plan is not null ? UpdateStatus.Available :
                storeApplicable && string.IsNullOrWhiteSpace(storeAvailability.AvailableVersion) ? UpdateStatus.Error :
                nativeOfferChecked || storeAvailability.IsResolved ? UpdateStatus.Current : UpdateStatus.ManagedExternally;
            var availableVersion = plan is not null ? targetVersion : null;
            var message = storeProductMismatch
                ? $"Microsoft Store resolved {storeAvailability.ProductId}, but OpenAI identifies {manifest.StoreProductId}."
                : plan is not null
                    ? $"Microsoft Store offers {targetVersion} for this installation. OpenAI's latest announcement is {manifest.BuildVersion}."
                : nativeCheckFailed ? $"The Store update check failed: {nativeError}"
                : status == UpdateStatus.Current ? "Windows Store returned no applicable update for this installation. Catalog-only versions are not counted as updates."
                : status == UpdateStatus.Error ? "Windows Store returned an update without a verifiable target version."
                : "An applicable Store update could not be determined for this installation.";
            return new UpdateCheckResult(
                application.Identity, application.DisplayName, application.NormalizedVersion, availableVersion,
                status, "openai-codex-store", "OpenAI update manifest + Microsoft Store",
                "application-managed", "Preserved by Microsoft Store/MSIX package", "provider-selected", "stable",
                OpenAiUpdateManifestUri.ToString(), message, plan,
                Applicability: plan is not null ? UpdateApplicability.Applicable :
                    status == UpdateStatus.Current ? UpdateApplicability.NotRequired :
                    status == UpdateStatus.Error ? UpdateApplicability.Unknown : UpdateApplicability.NotApplicable,
                AvailabilityReason: status == UpdateStatus.Current ? UpdateAvailabilityReason.NoApplicableStoreUpdate : UpdateAvailabilityReason.None,
                PublishedPackageVersion: publishedVersion,
                AnnouncedVersion: manifest.BuildVersion);
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
        var packageDirectory = Path.GetDirectoryName(application.PrimaryInstallPath);
        if (!string.IsNullOrWhiteSpace(packageDirectory))
        {
            var packagedCodex = Path.Combine(packageDirectory, "Codex.exe");
            if (File.Exists(packagedCodex)) AddPath("Codex", packagedCodex, requireCodexRoot: false);
        }
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
