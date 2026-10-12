using System.Text.Json;

namespace NaxUpdater.Core.Services;

// The Store broker answers native update searches one at a time, and at times
// each one takes several seconds inside Microsoft's own service. Most families
// that need a search get the same "no update" answer on every scan: system apps
// whose catalog entry lists no version, and packages whose newest catalog build
// targets another Windows release. A "no update" answer is reused while the
// installed version and the catalog's newest version are unchanged, for at most
// six hours: a newer catalog build the Store calls inapplicable can be a staged
// rollout that becomes applicable later. Errors, found updates and queued items
// are never cached.
internal sealed class StoreSearchCache(string path, TimeProvider? time = null)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private Dictionary<string, DateTimeOffset>? _entries;

    public static StoreSearchCache Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NaxUpdater", "store-search-cache.json"));

    public static string Key(string packageFamily, string installedVersion, string? catalogVersion) =>
        $"{packageFamily}|{installedVersion}|{catalogVersion ?? "-"}";

    // When the cached search ran, if it still stands.
    public DateTimeOffset? FindNoUpdate(string key)
    {
        lock (_gate)
        {
            if (!Load().TryGetValue(key, out var searchedAt)) return null;
            var age = _time.GetUtcNow() - searchedAt;
            return age >= TimeSpan.Zero && age < Lifetime ? searchedAt : null;
        }
    }

    public void RecordNoUpdate(string key)
    {
        lock (_gate)
        {
            var entries = Load();
            var now = _time.GetUtcNow();
            entries[key] = now;
            foreach (var stale in entries.Where(e => now - e.Value >= Lifetime || e.Value > now).Select(e => e.Key).ToArray())
                entries.Remove(stale);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private Dictionary<string, DateTimeOffset> Load()
    {
        if (_entries is not null) return _entries;
        try
        {
            _entries = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _entries = [];
        }
        return _entries;
    }
}
