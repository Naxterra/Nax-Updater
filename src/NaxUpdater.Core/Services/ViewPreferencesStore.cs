using System.Text.Json;

namespace NaxUpdater.Core.Services;

public sealed record ViewPreferences(int Width = 1380, int Height = 860, bool Maximized = false,
    Dictionary<string, double[]>? Columns = null);

public sealed class ViewPreferencesStore(string path)
{
    public ViewPreferences Load()
    {
        try
        {
            return Normalize(JsonSerializer.Deserialize<ViewPreferences>(File.ReadAllText(path)) ?? new());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(ViewPreferences preferences)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(Normalize(preferences)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static ViewPreferences Normalize(ViewPreferences preferences) => preferences with
    {
        Width = Math.Clamp(preferences.Width, 1100, 16384),
        Height = Math.Clamp(preferences.Height, 600, 16384),
        Columns = preferences.Columns?.Where(p => p.Value is { Length: > 0 and <= 12 } &&
                p.Value.All(v => double.IsFinite(v) && v is >= 50 and <= 2000))
            .ToDictionary(p => p.Key, p => p.Value)
    };
}
