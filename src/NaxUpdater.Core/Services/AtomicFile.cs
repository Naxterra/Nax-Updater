using System.Text;

namespace NaxUpdater.Core.Services;

internal static class AtomicFile
{
    // The text goes to a temporary file of its own next to the target, is forced to disk and only
    // then replaces the target, so a power loss leaves the old or the new file, never a truncated one.
    public static void WriteAllText(string path, string text)
    {
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

    // A damaged file is kept under a name of its own, so a second damage cannot replace the
    // copy that was kept the first time.
    public static string KeepAside(string path)
    {
        var target = $"{path}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        File.Move(path, target);
        return target;
    }
}
