using System.Globalization;
using System.Text.Json;
using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

internal sealed class MicrosoftStoreProductMetadataClient(HttpClient httpClient)
{
    private static readonly Uri CatalogBaseUri = new("https://displaycatalog.mp.microsoft.com/v7.0/products/");

    public async Task<StoreProductMatch?> ResolvePackageFamilyAsync(string family, string? architecture, string? installedVersion, CancellationToken token)
    {
        var suffix = $"&market={RegionInfo.CurrentRegion.TwoLetterISORegionName}&languages={CultureInfo.CurrentUICulture.Name}";
        using var lookup = await ReadOfficialAsync(new Uri(CatalogBaseUri,
            $"lookup?alternateId=PackageFamilyName&value={Uri.EscapeDataString(family)}{suffix}"), token);
        if (!lookup.RootElement.TryGetProperty("Products", out var products) || products.ValueKind != JsonValueKind.Array) return null;
        var ids = products.EnumerateArray().Where(p => p.TryGetProperty("ProductId", out _))
            .Select(p => p.GetProperty("ProductId").GetString()).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
        if (ids.Length > 8) throw new InvalidOperationException("Store package-family lookup returned too many candidates.");
        var matches = new List<StoreProductMatch>();
        foreach (var id in ids)
        {
            using var full = await ReadOfficialAsync(new Uri(CatalogBaseUri, $"{Uri.EscapeDataString(id!)}?{suffix[1..]}"), token);
            var identity = ParseIdentity(full.RootElement, id!, family, architecture);
            if (identity is not null)
            {
                var alternatives = full.RootElement.GetProperty("Product").GetProperty("DisplaySkuAvailabilities").EnumerateArray()
                    .Select(e => e.GetProperty("Sku").GetProperty("SkuId").GetString())
                    .Where(s => s is not null && s != identity.SkuId).Distinct()
                    .Select(s => ParseIdentity(full.RootElement, id!, family, architecture, s)).Where(i => i is not null).Select(i => i!).ToArray();
                matches.Add(new(identity, ParsePublishedPackage(full.RootElement, id!, family, architecture, installedVersion, identity.SkuId), alternatives));
            }
        }
        if (matches.Count > 1) throw new InvalidOperationException("Multiple Store products expose the same installed package family.");
        return matches.SingleOrDefault();
    }

    private async Task<JsonDocument> ReadOfficialAsync(Uri uri, CancellationToken token)
    {
        using var response = await httpClient.GetAsync(uri, token);
        response.EnsureSuccessStatusCode();
        var final = response.RequestMessage?.RequestUri ?? uri;
        if (final.Scheme != Uri.UriSchemeHttps || !final.Host.Equals(CatalogBaseUri.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Store metadata redirected outside the official catalog.");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
    }

    internal static StoreProductIdentity? ParseIdentity(JsonElement root, string productId, string family, string? architecture, string? requestedSku = null)
    {
        if (!TryGet(root, "Product", out var product) || !TryGetString(product, "ProductId", out var returnedId) || returnedId != productId ||
            !TryGet(product, "DisplaySkuAvailabilities", out var skus) || skus.ValueKind != JsonValueKind.Array) return null;
        var identities = new List<StoreProductIdentity>();
        foreach (var entry in skus.EnumerateArray())
        {
            if (!TryGet(entry, "Sku", out var sku) || !TryGetString(sku, "SkuId", out var skuId) ||
                !TryGet(sku, "Properties", out var properties) || !TryGet(properties, "Packages", out var packages) ||
                packages.ValueKind != JsonValueKind.Array) continue;
            if (requestedSku is not null && skuId != requestedSku) continue;
            if (packages.EnumerateArray().Any(p => TryGetString(p, "PackageFamilyName", out var candidate) &&
                candidate.Equals(family, StringComparison.OrdinalIgnoreCase) && PackageSupportsArchitecture(p, NormalizeArchitecture(architecture))))
                identities.Add(new(productId, skuId, family));
        }
        return identities.OrderBy(p => p.SkuId, StringComparer.Ordinal).FirstOrDefault();
    }

    public async Task<PublishedStorePackage?> GetPublishedPackageAsync(
        string productId, string family, string? architecture, string? installedVersion, CancellationToken token, string? skuId = null)
    {
        var market = RegionInfo.CurrentRegion.TwoLetterISORegionName;
        var uri = new Uri(CatalogBaseUri, $"{Uri.EscapeDataString(productId)}?market={market}&languages={CultureInfo.CurrentUICulture.Name}");
        using var response = await httpClient.GetAsync(uri, token);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri ?? uri;
        if (finalUri.Scheme != Uri.UriSchemeHttps || !finalUri.Host.Equals(CatalogBaseUri.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Store package metadata redirected outside the official HTTPS catalog.");
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        return ParsePublishedPackage(document.RootElement, productId, family, architecture, installedVersion, skuId);
    }

    internal static PublishedStorePackage? ParsePublishedPackage(
        JsonElement root, string productId, string family, string? architecture, string? installedVersion, string? requestedSku = null)
    {
        if (!TryGet(root, "Product", out var product) ||
            TryGetString(product, "ProductId", out var returnedId) && returnedId != productId ||
            !TryGet(product, "DisplaySkuAvailabilities", out var skus) || skus.ValueKind != JsonValueKind.Array)
            return null;
        var requested = NormalizeArchitecture(architecture);
        if (requested is null) return null;
        var candidates = new List<PublishedStorePackage>();
        foreach (var entry in skus.EnumerateArray())
        {
            if (!TryGet(entry, "Sku", out var sku) || !TryGetString(sku, "SkuId", out var skuId) ||
                !TryGet(sku, "Properties", out var properties) || !TryGet(properties, "Packages", out var packages) ||
                packages.ValueKind != JsonValueKind.Array) continue;
            if (requestedSku is not null && skuId != requestedSku) continue;
            foreach (var package in packages.EnumerateArray())
            {
                if (!TryGetString(package, "PackageFamilyName", out var packageFamily) || packageFamily != family ||
                    !PackageSupportsArchitecture(package, requested) ||
                    !TryReadVersion(package, family, out var version) ||
                    !TryGetString(package, "PackageFullName", out var fullName) ||
                    !PackageIdentityMatches(fullName, family, requested, version)) continue;
                // Bundle envelope versions can be unrelated to the installed
                // inner app version. Exact app-package identities may legitimately
                // cross major versions; Store eligibility still gates execution.
                if (Version.TryParse(installedVersion, out var installed) && version.Major != installed.Major &&
                    fullName.Split('_')[3] == "~") continue;
                candidates.Add(new(productId, skuId, family, version.ToString(4), fullName, requested));
            }
        }
        return candidates.OrderByDescending(static item => item.Version, Comparer<string>.Create(VersionOrder.Compare))
            .ThenBy(static item => item.SkuId, StringComparer.Ordinal).FirstOrDefault();
    }

    private static bool PackageIdentityMatches(string fullName, string family, string architecture, Version version)
    {
        var parts = fullName.Split('_');
        return parts.Length == 5 &&
            $"{parts[0]}_{parts[4]}".Equals(family, StringComparison.OrdinalIgnoreCase) &&
            Version.TryParse(parts[1], out var identityVersion) && identityVersion == version &&
            NormalizeArchitecture(parts[2]) is { } identityArchitecture &&
            (identityArchitecture == architecture || identityArchitecture == "neutral");
    }

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
        request.Headers.UserAgent.ParseAdd("NaxUpdater/0.16.10");
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
            (encodedElement.ValueKind == JsonValueKind.Number && encodedElement.TryGetUInt64(out var encoded) ||
             encodedElement.ValueKind == JsonValueKind.String && ulong.TryParse(encodedElement.GetString(), out encoded)))
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
