using NaxUpdater.Core.Models;
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

    private readonly StorePackageDeploymentService _store;
    private readonly HttpClient _httpClient;

    public MsixStoreUpdateProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
        _store = new StorePackageDeploymentService(_httpClient);
    }

    public string Id => "msix-store";

    public bool CanHandle(InstalledApplication application) => application.ManagementMode == ManagementMode.Msix;

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

            var updateAvailable = VersionOrder.Compare(manifest.BuildVersion, application.NormalizedVersion) > 0;
            var plan = updateAvailable
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
                    ["ChatGPT", "Codex"],
                    StoreProductId: manifest.StoreProductId,
                    StorePackageFamilyName: packageFamily,
                    StorePublisher: application.Publisher)
                : null;
            return new UpdateCheckResult(
                application.Identity,
                application.DisplayName,
                application.NormalizedVersion,
                updateAvailable ? manifest.BuildVersion : null,
                updateAvailable ? UpdateStatus.Available : UpdateStatus.Current,
                "openai-codex-store",
                "OpenAI update manifest + Microsoft Store",
                "application-managed",
                "Preserved by Microsoft Store/MSIX package",
                "provider-selected",
                "stable",
                OpenAiUpdateManifestUri.ToString(),
                updateAvailable
                    ? "OpenAI's official Windows update manifest reports a newer package; the exact Microsoft Store product will be deployed."
                    : "OpenAI's official Windows update manifest reports that the installed package is current.",
                plan);
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
}

internal sealed record OpenAiWindowsUpdateManifest(
    string BuildVersion,
    string StoreProductId,
    string PackageIdentity);
