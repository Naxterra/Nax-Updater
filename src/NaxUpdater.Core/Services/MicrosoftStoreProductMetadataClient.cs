using System.Globalization;
using System.Text.Json;

namespace NaxUpdater.Core.Services;

internal sealed class MicrosoftStoreProductMetadataClient(HttpClient httpClient)
{
    private static readonly Uri CatalogBaseUri = new("https://displaycatalog.mp.microsoft.com/v7.0/products/");

    public async Task<string?> GetLatestPackageVersionAsync(
        string productId,
        string packageFamilyName,
        string? architecture,
        string? installedVersion,
        CancellationToken cancellationToken)
    {
        var market = RegionInfo.CurrentRegion.TwoLetterISORegionName;
        var language = CultureInfo.CurrentUICulture.Name;
        var uri = new Uri(
            CatalogBaseUri,
            $"{Uri.EscapeDataString(productId)}?market={Uri.EscapeDataString(market)}&languages={Uri.EscapeDataString(language)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("NaxUpdater/0.15.17");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseLatestPackageVersion(document.RootElement, packageFamilyName, architecture, installedVersion);
    }

    internal static string? ParseLatestPackageVersion(
        JsonElement root,
        string packageFamilyName,
        string? architecture,
        string? installedVersion)
    {
        if (!TryGet(root, "Product", out var product) ||
            !TryGet(product, "DisplaySkuAvailabilities", out var skuAvailabilities) ||
            skuAvailabilities.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var requestedArchitecture = NormalizeArchitecture(architecture);
        var candidates = new List<(Version Version, bool ArchitectureMatch)>();
        foreach (var displaySku in skuAvailabilities.EnumerateArray())
        {
            if (!TryGet(displaySku, "Sku", out var sku) ||
                !TryGet(sku, "Properties", out var properties) ||
                !TryGet(properties, "Packages", out var packages) ||
                packages.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var package in packages.EnumerateArray())
            {
                if (!TryGetString(package, "PackageFamilyName", out var candidateFamily) ||
                    !candidateFamily.Equals(packageFamilyName, StringComparison.OrdinalIgnoreCase) ||
                    IsEncryptedPackage(package) ||
                    !TryReadVersion(package, packageFamilyName, out var version))
                {
                    continue;
                }

                candidates.Add((version, PackageSupportsArchitecture(package, requestedArchitecture)));
            }
        }

        var applicable = candidates.Any(static candidate => candidate.ArchitectureMatch)
            ? candidates.Where(static candidate => candidate.ArchitectureMatch)
            : candidates;
        if (Version.TryParse(installedVersion, out var installed))
        {
            var sameVersionLine = applicable.Where(candidate => candidate.Version.Major == installed.Major).ToArray();
            if (sameVersionLine.Length == 0)
            {
                // Store bundle identities can use unrelated calendar/rank versions while their inner app
                // packages retain product versions (for example PowerShell 7.x vs bundle 2026.x).
                // In that case only the deployment catalog can authoritatively determine applicability.
                return null;
            }
            applicable = sameVersionLine;
        }
        var latest = applicable.Select(static candidate => candidate.Version).OrderDescending().FirstOrDefault();
        return latest?.ToString(4);
    }

    internal static bool IsNewer(string? installedVersion, string? availableVersion)
    {
        return Version.TryParse(installedVersion, out var installed) &&
               Version.TryParse(availableVersion, out var available) &&
               available > installed;
    }

    private static bool TryReadVersion(JsonElement package, string packageFamilyName, out Version version)
    {
        if (TryGetString(package, "PackageFullName", out var fullName))
        {
            var familySeparator = packageFamilyName.LastIndexOf('_');
            var identityName = familySeparator > 0 ? packageFamilyName[..familySeparator] : packageFamilyName;
            var prefix = $"{identityName}_";
            if (familySeparator > 0 && fullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var separator = fullName.IndexOf('_', prefix.Length);
                if (separator > prefix.Length &&
                    Version.TryParse(fullName[prefix.Length..separator], out version!))
                {
                    return true;
                }
            }
        }

        if (TryGet(package, "Version", out var encodedElement) &&
            encodedElement.TryGetUInt64(out var encoded))
        {
            version = new Version(
                (int)(encoded >> 48),
                (int)((encoded >> 32) & 0xffff),
                (int)((encoded >> 16) & 0xffff),
                (int)(encoded & 0xffff));
            return true;
        }

        version = new Version();
        return false;
    }

    private static bool PackageSupportsArchitecture(JsonElement package, string? requestedArchitecture)
    {
        if (requestedArchitecture is null ||
            !TryGet(package, "Architectures", out var architectures) ||
            architectures.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        foreach (var value in architectures.EnumerateArray())
        {
            var candidate = NormalizeArchitecture(value.GetString());
            if (candidate is "neutral" || candidate == requestedArchitecture)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsEncryptedPackage(JsonElement package) =>
        TryGetString(package, "PackageFormat", out var format) &&
        format.StartsWith("E", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeArchitecture(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "x86" => "x86",
        "x64" or "amd64" => "x64",
        "arm" => "arm",
        "arm64" => "arm64",
        "neutral" => "neutral",
        _ => null
    };

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!TryGet(element, name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }
}
