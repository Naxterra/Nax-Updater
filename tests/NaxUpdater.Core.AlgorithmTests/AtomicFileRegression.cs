using NaxUpdater.Core.Services;

// State files are written through a temporary file of their own; an interrupted write leaves
// that file behind. Each scenario is one method.
internal static class AtomicFileRegression
{
    public static void Run(Action<bool, string> assert)
    {
        interrupted_write_leftovers_are_removed(assert);
    }

    private static void interrupted_write_leftovers_are_removed(Action<bool, string> assert)
    {
        var folder = Path.Combine(Path.GetTempPath(), "nax-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // An interrupted write leaves its own temporary file; earlier versions used ".tmp".
            var path = Path.Combine(folder, "update-operation.json");
            File.WriteAllText(path + ".0123456789abcdef0123456789abcdef.tmp", "partial");
            File.WriteAllText(path + ".tmp", "partial");
            AtomicFile.WriteAllText(path, "{}");
            var left = Directory.GetFiles(folder).Select(Path.GetFileName).ToArray();
            assert(left.SequenceEqual(["update-operation.json"]) && File.ReadAllText(path) == "{}",
                $"Temporary files of interrupted writes were left behind, or the file was not written: {string.Join(", ", left)}");
        }
        finally { Directory.Delete(folder, true); }
    }
}
