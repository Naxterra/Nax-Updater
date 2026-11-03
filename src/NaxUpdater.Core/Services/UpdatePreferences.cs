using NaxUpdater.Core.Models;
using System.Text.Json;

namespace NaxUpdater.Core.Services;

// The owner's choice for one application: skip exactly one release, or hold the application so
// that Update All leaves it out. Held and skipped applications are still checked and can still
// be updated one by one.
public sealed record UpdatePreference(string Key, string DisplayName, string? SkippedVersion, bool Held, DateTimeOffset ChangedAt);

public enum UpdateExclusion
{
    None,
    SkippedVersion,
    Held
}

// The saved choices exist but cannot be read. Treating them as empty would put every held
// application back into Update All without a word, so the caller has to decide.
public sealed class UpdatePreferencesUnreadableException(string message, Exception inner) : IOException(message, inner);

public sealed class FileUpdatePreferences(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path = Path.GetFullPath(path);

    // The correlation key survives a change of provider and an MSI major upgrade that replaces
    // the product code, so a choice does not silently disappear when the source changes.
    public static string KeyFor(UpdateCheckResult update) =>
        string.IsNullOrWhiteSpace(update.CorrelationKey) ? update.ApplicationIdentity : update.CorrelationKey;

    public static UpdateExclusion ExclusionFor(UpdatePreference? preference, UpdateCheckResult update) =>
        preference is null ? UpdateExclusion.None
        : preference.Held ? UpdateExclusion.Held
        : SkipsRelease(preference, update) ? UpdateExclusion.SkippedVersion
        : UpdateExclusion.None;

    // A skip covers only the skipped release; a newer one is offered again. It is kept while the
    // application is also held, so releasing the hold brings the skip back.
    public static bool SkipsRelease(UpdatePreference? preference, UpdateCheckResult update) =>
        preference?.SkippedVersion is { } skipped && update.AvailableVersion is { } available &&
        VersionOrder.Compare(skipped, available) == 0;

    /// <exception cref="UpdatePreferencesUnreadableException">The saved choices cannot be read.</exception>
    public UpdatePreference? Get(UpdateCheckResult update) => ReadAll().GetValueOrDefault(KeyFor(update));

    /// <exception cref="UpdatePreferencesUnreadableException">The saved choices cannot be read.</exception>
    public UpdateExclusion ExclusionFor(UpdateCheckResult update) => ExclusionFor(Get(update), update);

    // One read for a whole list, keyed by KeyFor.
    /// <exception cref="UpdatePreferencesUnreadableException">The saved choices cannot be read.</exception>
    public IReadOnlyDictionary<string, UpdatePreference> ReadAll()
    {
        lock (_gate)
        {
            try
            {
                return ReadFile();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                throw new UpdatePreferencesUnreadableException($"The saved skip and hold choices could not be read: {exception.Message}", exception);
            }
        }
    }

    public void SkipVersion(UpdateCheckResult update) =>
        Change(update, current => current with { SkippedVersion = update.AvailableVersion });

    public void ClearSkippedVersion(UpdateCheckResult update) =>
        Change(update, static current => current with { SkippedVersion = null });

    public void SetHeld(UpdateCheckResult update, bool held) =>
        Change(update, current => current with { Held = held });

    private void Change(UpdateCheckResult update, Func<UpdatePreference, UpdatePreference> change)
    {
        lock (_gate)
        {
            Dictionary<string, UpdatePreference> preferences;
            try
            {
                preferences = ReadFile();
            }
            catch (JsonException)
            {
                // The owner is making a new choice, so a damaged file is kept aside under a name
                // of its own and the new choice starts a new file. A locked file still fails.
                AtomicFile.KeepAside(_path);
                preferences = new(StringComparer.Ordinal);
            }
            var key = KeyFor(update);
            var current = preferences.GetValueOrDefault(key) ?? new UpdatePreference(key, update.DisplayName, null, false, default);
            var changed = change(current) with { DisplayName = update.DisplayName, ChangedAt = DateTimeOffset.UtcNow };
            if (changed is { Held: false, SkippedVersion: null }) preferences.Remove(key);
            else preferences[key] = changed;
            var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("The preferences path has no directory.");
            Directory.CreateDirectory(directory);
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(preferences.Values.OrderBy(static value => value.Key, StringComparer.Ordinal), JsonOptions));
        }
    }

    private Dictionary<string, UpdatePreference> ReadFile()
    {
        if (!File.Exists(_path)) return new(StringComparer.Ordinal);
        return (JsonSerializer.Deserialize<List<UpdatePreference>>(File.ReadAllText(_path), JsonOptions) ?? [])
            .Where(static value => value is not null && !string.IsNullOrWhiteSpace(value.Key))
            .GroupBy(static value => value.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
    }
}
