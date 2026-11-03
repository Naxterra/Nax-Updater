using NaxUpdater.Core.Models;
using System.Text.Json;

namespace NaxUpdater.Core.Services;

public interface IUpdateHistory
{
    void Append(UpdateHistoryEntry entry);
    IReadOnlyList<UpdateHistoryEntry> ReadNewestFirst();
}

// The operation journal holds only the transaction that is running or was interrupted, for
// recovery. The history keeps every finished outcome, so a batch can be reviewed after the
// window was closed. It is capped so that it cannot grow without limit.
public sealed class FileUpdateHistory(string path, int capacity = 500) : IUpdateHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path = Path.GetFullPath(path);

    public void Append(UpdateHistoryEntry entry)
    {
        lock (_gate)
        {
            var entries = ReadFile(preserveUnreadable: true);
            entries.Add(entry);
            if (entries.Count > capacity) entries.RemoveRange(0, entries.Count - capacity);
            var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("The history path has no directory.");
            Directory.CreateDirectory(directory);
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(entries, JsonOptions));
        }
    }

    public IReadOnlyList<UpdateHistoryEntry> ReadNewestFirst()
    {
        lock (_gate)
        {
            var entries = ReadFile(preserveUnreadable: false);
            entries.Reverse();
            return entries;
        }
    }

    private List<UpdateHistoryEntry> ReadFile(bool preserveUnreadable)
    {
        try
        {
            if (!File.Exists(_path)) return [];
            return JsonSerializer.Deserialize<List<UpdateHistoryEntry>>(File.ReadAllText(_path), JsonOptions)?
                .Where(static entry => entry is not null).ToList() ?? [];
        }
        catch (JsonException) when (preserveUnreadable)
        {
            // A damaged file is kept next to the new one instead of being overwritten.
            AtomicFile.KeepAside(_path);
            return [];
        }
        catch (Exception exception) when (!preserveUnreadable && exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
