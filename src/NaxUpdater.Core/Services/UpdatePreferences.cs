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
        // A skip covers only the skipped release; a newer one is offered again.
        : preference.SkippedVersion is { } skipped && update.AvailableVersion is { } available &&
          VersionOrder.Compare(skipped, available) == 0 ? UpdateExclusion.SkippedVersion
        : UpdateExclusion.None;

    public UpdatePreference? Get(UpdateCheckResult update)
    {
        lock (_gate)
        {
            return ReadFile(preserveUnreadable: false).GetValueOrDefault(KeyFor(update));
        }
    }

    public UpdateExclusion ExclusionFor(UpdateCheckResult update) => ExclusionFor(Get(update), update);

    // One read for a whole list, keyed by KeyFor.
    public IReadOnlyDictionary<string, UpdatePreference> ReadAll()
    {
        lock (_gate)
        {
            return ReadFile(preserveUnreadable: false);
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
            var preferences = ReadFile(preserveUnreadable: true);
            var key = KeyFor(update);
            var current = preferences.GetValueOrDefault(key) ?? new UpdatePreference(key, update.DisplayName, null, false, default);
            var changed = change(current) with { DisplayName = update.DisplayName, ChangedAt = DateTimeOffset.UtcNow };
            if (changed is { Held: false, SkippedVersion: null }) preferences.Remove(key);
            else preferences[key] = changed;
            var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("The preferences path has no directory.");
            Directory.CreateDirectory(directory);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences.Values.OrderBy(static value => value.Key, StringComparer.Ordinal), JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
    }

    private Dictionary<string, UpdatePreference> ReadFile(bool preserveUnreadable)
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.Ordinal);
            return (JsonSerializer.Deserialize<List<UpdatePreference>>(File.ReadAllText(_path), JsonOptions) ?? [])
                .Where(static value => value is not null && !string.IsNullOrWhiteSpace(value.Key))
                .GroupBy(static value => value.Key, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        }
        catch (JsonException) when (preserveUnreadable)
        {
            // A damaged file is kept next to the new one instead of being overwritten.
            File.Move(_path, _path + ".unreadable", overwrite: true);
            return new(StringComparer.Ordinal);
        }
        catch (Exception exception) when (!preserveUnreadable && exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }
}
