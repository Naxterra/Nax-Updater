using System.Text;

namespace NaxUpdater.Core.Services;

internal static class AtomicFile
{
    // The text goes to a temporary file of its own next to the target, is forced to disk and only
    // then replaces the target, so a power loss leaves the old or the new file, never a truncated one.
    public static void WriteAllText(string path, string text)
    {
        RemoveStaleTemporaries(path);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    // Every caller serializes the writes of its file (the journal under its own lock, the Store
    // cache and the Kyocera cache under a shared one), and only one Nax-Updater runs per session,
    // so a temporary file of the same target that exists now was left by an interrupted write.
    // The fixed ".tmp" name of earlier versions is removed as well.
    private static void RemoveStaleTemporaries(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is null || !Directory.Exists(directory)) return;
        var name = Path.GetFileName(path);
        foreach (var stale in Directory.EnumerateFiles(directory, name + ".*.tmp").Append(path + ".tmp"))
        {
            try
            {
                if (File.Exists(stale)) File.Delete(stale);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A leftover that cannot be removed now is tried again on the next write.
            }
        }
    }
}
