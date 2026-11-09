using NaxUpdater.Core.Services;

namespace NaxUpdater.Services;

internal static class ViewSettings
{
    private static readonly ViewPreferencesStore Store = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NaxUpdater", "view.json"));
    public static ViewPreferences Current { get; set; } = Store.Load();

    public static double[] ColumnWidths(string name, double[] defaults) =>
        Current.Columns?.TryGetValue(name, out var widths) == true && widths.Length == defaults.Length ? widths : defaults;

    public static void SetColumns(string name, double[] widths)
    {
        var columns = Current.Columns ?? new Dictionary<string, double[]>();
        columns[name] = widths;
        Current = Current with { Columns = columns };
    }

    public static void Save() => Store.Save(Current);
}
