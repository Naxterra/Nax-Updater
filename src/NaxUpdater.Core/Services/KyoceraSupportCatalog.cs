using System.Collections.Concurrent;
using System.Globalization;
using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NaxUpdater.Core.Services;

public sealed record KyoceraDownload(string Name, string Product, string Version, Uri Url, string? ReleaseDate);

public sealed record KyoceraModel(string Name, string Id, string ProjectId, Uri SupportPage, IReadOnlyList<KyoceraDownload> Downloads);

// Kyocera's global user support site (the "User Support Page" that its regional download
// pages link to) publishes one JSON file per printer model with every driver and utility,
// its version and its download address. The model is found by the name of the installed
// KX printer driver ("Kyocera ECOSYS MA2100cwfx KX" is model "ECOSYS MA2100cwfx").
// With a cache folder, each downloaded file is kept with its ETag and Last-Modified, and the
// next check asks Kyocera whether it changed: an unchanged file comes back as 304 without its
// 480 KB body. The cached data only names versions and download pages; Kyocera publishes no
// hashes, so nothing from it is ever installed automatically.
public sealed partial class KyoceraSupportCatalog(HttpClient httpClient, string? cacheDirectory = null)
{
    private static readonly Uri SupportRoot = new("https://www.kyoceradocumentsolutions.com/support_product/");
    // Like every manufacturer site, Kyocera's may only cost its own rows a bounded wait.
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private const long MaxDocumentBytes = 8 * 1024 * 1024;
    private readonly ConcurrentDictionary<string, Task<KyoceraModel?>> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _indexLock = new();
    private Task<JsonDocument>? _index;

    // The download is shared by every Kyocera row of a check, so it runs under its own time
    // limit, not under the token of the row that started it; each caller only stops waiting
    // when its own token is canceled. A failed download is dropped, so the next row retries.
    public async Task<KyoceraModel?> FindModelAsync(string modelName, CancellationToken cancellationToken)
    {
        var load = _models.GetOrAdd(modelName, LoadModelAsync);
        try
        {
            return await load.WaitAsync(cancellationToken);
        }
        catch when (load.IsFaulted || load.IsCanceled)
        {
            _models.TryRemove(new KeyValuePair<string, Task<KyoceraModel?>>(modelName, load));
            throw;
        }
    }

    private Task<JsonDocument> IndexAsync()
    {
        lock (_indexLock)
        {
            if (_index is null || _index.IsFaulted || _index.IsCanceled)
                _index = LoadJsonAsync(new Uri(SupportRoot, "js/index.json"));
            return _index;
        }
    }

    private async Task<KyoceraModel?> LoadModelAsync(string modelName)
    {
        var index = await IndexAsync();
        string? id = null;
        string? projectId = null;
        foreach (var entry in index.RootElement.GetProperty("models").EnumerateArray())
        {
            if (entry.GetArrayLength() < 4 || !string.Equals(entry[2].GetString(), modelName, StringComparison.OrdinalIgnoreCase)) continue;
            id = entry[1].GetString();
            projectId = entry[3].GetString();
            break;
        }
        if (id is null || projectId is null || !DigitsRegex().IsMatch(id) || !DigitsRegex().IsMatch(projectId)) return null;
        using var model = await LoadJsonAsync(new Uri(SupportRoot, $"js/models/{id}.json"));
        var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase) ? "de" : "en";
        return new KyoceraModel(modelName, id, projectId, new Uri(SupportRoot, $"model_{language}.html?m={id}&p={projectId}"), ParseDownloads(model.RootElement));
    }

    internal static IReadOnlyList<KyoceraDownload> ParseDownloads(JsonElement model)
    {
        var downloads = new List<KyoceraDownload>();
        if (!model.TryGetProperty("downloads", out var groups) || groups.ValueKind != JsonValueKind.Object) return downloads;
        foreach (var group in groups.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var item in group.Value.EnumerateObject())
            {
                var name = Text(item.Value, "name");
                var url = Text(item.Value, "url");
                if (name is null || url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                    !uri.Host.EndsWith(".kyoceradocumentsolutions.com", StringComparison.OrdinalIgnoreCase)) continue;
                // File names carry the product and its version: "Status Monitor 5_5.4.12.3.exe",
                // "Kx87_UPD_8.7.0917_en_RC3_signed.zip". The "version" field is not always
                // the product version ("5.5.4.12.3" for Status Monitor 5.4.12.3).
                var file = Path.GetFileNameWithoutExtension(name);
                var utility = UtilityFileRegex().Match(file);
                var kx = KxDriverFileRegex().Match(file);
                var (product, version) = kx.Success ? ("KX Driver", kx.Groups["version"].Value)
                    : utility.Success ? (utility.Groups["product"].Value, utility.Groups["version"].Value)
                    : (file, Text(item.Value, "version") ?? "");
                if (downloads.Any(existing => existing.Name == name)) continue;
                downloads.Add(new KyoceraDownload(name, product.Trim(), version, uri, Text(item.Value, "release_date")));
            }
        }
        return downloads;
    }

    // "Kyocera TWAIN Driver", "KYOCERA Status Monitor 5" and "Wi-Fi Setup Tool" against the
    // download names "Kyocera TWAIN Driver", "Status Monitor 5" and "WIFI Setup Tool".
    internal static string ProductKey(string name) =>
        NonAlphanumericRegex().Replace(name.ToLowerInvariant().Replace("kyocera", "", StringComparison.Ordinal), "");

    // The model of an installed KX driver, or null for any other printer driver.
    internal static string? ModelOfKxDriver(string driverName)
    {
        var match = KxDriverNameRegex().Match(driverName.Trim());
        return match.Success ? match.Groups["model"].Value.Trim() : null;
    }

    public static IReadOnlyList<(string DriverName, string Model, string Version)> InstalledKxDrivers(ICollection<string>? issues = null)
    {
        var drivers = new List<(string, string, string)>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT Name, DriverVersion FROM MSFT_PrinterDriver");
            foreach (ManagementObject item in searcher.Get())
            {
                var name = item["Name"]?.ToString();
                if (name is null || ModelOfKxDriver(name) is not { } model || item["DriverVersion"] is not ulong packed) continue;
                var version = string.Join('.', new[] { 48, 32, 16, 0 }.Select(shift => (packed >> shift) & 0xffff));
                if (!drivers.Any(existing => existing.Item1 == name)) drivers.Add((name, model, version));
            }
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            issues?.Add($"Printer drivers: {exception.Message}");
        }
        return drivers;
    }

    private async Task<JsonDocument> LoadJsonAsync(Uri uri)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout);
        var cache = CachePaths(uri);
        var cached = cache is null ? null : ReadCache(cache.Value, uri);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("NaxUpdater/0.17.73");
            if (cached?.Entry.ETag is { } etag) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            if (cached?.Entry.LastModified is { } lastModified) request.Headers.IfModifiedSince = lastModified;
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified && cached is not null)
                return JsonDocument.Parse(cached.Body);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxDocumentBytes)
                throw new InvalidDataException("Kyocera's support data is unexpectedly large.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + read > MaxDocumentBytes) throw new InvalidDataException("Kyocera's support data is unexpectedly large.");
                buffer.Write(chunk, 0, read);
            }
            // GetString keeps a UTF-8 byte order mark as U+FEFF, which the parser would reject.
            var body = System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimStart('﻿');
            var document = JsonDocument.Parse(body);
            if (cache is { } paths)
                WriteCache(paths, new KyoceraCacheEntry(uri.AbsoluteUri, response.Headers.ETag?.ToString(), response.Content.Headers.LastModified), body);
            return document;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"Kyocera's support site did not answer within {RequestTimeout.TotalSeconds:0} seconds.");
        }
    }

    private sealed record KyoceraCacheEntry(string Uri, string? ETag, DateTimeOffset? LastModified);

    // A scan's background driver check and a later check of the driver view can use the same
    // cache folder at the same time, so the cache is read and written under one lock; otherwise
    // one writer's entry could pair with the other's body.
    private static readonly Lock CacheLock = new();

    private sealed record CachedDocument(KyoceraCacheEntry Entry, string Body);

    private (string Body, string Entry)? CachePaths(Uri uri)
    {
        if (string.IsNullOrWhiteSpace(cacheDirectory)) return null;
        var name = "kyocera-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(uri.AbsoluteUri)))[..16].ToLowerInvariant();
        return (Path.Combine(cacheDirectory, name + ".json"), Path.Combine(cacheDirectory, name + ".entry.json"));
    }

    // A missing, damaged or foreign cache entry only means a full download.
    private static CachedDocument? ReadCache((string Body, string Entry) paths, Uri uri)
    {
        lock (CacheLock)
        try
        {
            if (!File.Exists(paths.Body) || !File.Exists(paths.Entry)) return null;
            var entry = JsonSerializer.Deserialize<KyoceraCacheEntry>(File.ReadAllText(paths.Entry));
            if (entry is null || !entry.Uri.Equals(uri.AbsoluteUri, StringComparison.Ordinal) || entry.ETag is null && entry.LastModified is null) return null;
            var body = File.ReadAllText(paths.Body);
            using (JsonDocument.Parse(body)) { }
            return new CachedDocument(entry, body);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // The body is written before its entry, so an entry never names a body that was not saved.
    private static void WriteCache((string Body, string Entry) paths, KyoceraCacheEntry entry, string body)
    {
        if (entry.ETag is null && entry.LastModified is null) return;
        lock (CacheLock)
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.Body)!);
            if (File.Exists(paths.Entry)) File.Delete(paths.Entry);
            AtomicFile.WriteAllText(paths.Body, body);
            AtomicFile.WriteAllText(paths.Entry, JsonSerializer.Serialize(entry));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Without a cache the next check downloads the file again.
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex DigitsRegex();

    [GeneratedRegex(@"^(?<product>.+?)[_ ](?<version>\d+(?:\.\d+)+)$")]
    private static partial Regex UtilityFileRegex();

    [GeneratedRegex(@"^Kx\d+_UPD_(?<version>\d+(?:\.\d+)+)_", RegexOptions.IgnoreCase)]
    private static partial Regex KxDriverFileRegex();

    [GeneratedRegex(@"^Kyocera (?<model>.+?) KX(?: \(XPS\))?$", RegexOptions.IgnoreCase)]
    private static partial Regex KxDriverNameRegex();

    [GeneratedRegex(@"[^a-z0-9]")]
    private static partial Regex NonAlphanumericRegex();
}
