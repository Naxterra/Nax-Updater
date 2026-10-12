using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

// electron-updater's staged rollout (AppUpdater.isStagingMatch): each installation
// keeps a random UUID in <userData>\.updaterId and receives a release once the
// UUID's last four bytes, read big-endian and divided by 0xFFFFFFFF, fall below
// stagingPercentage / 100. Nax-Updater applies the same rule to the same file, so
// a staged release is offered exactly when the application's own updater offers
// it. CurseForge logs the same numbers ("Staging percentage: 0.25, percentage:
// 0.9036619034371483, user id: ...").
public static class ElectronStagedRollout
{
    private static readonly Regex UpdaterId = new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex StagingPercentage = new(
        @"(?m)^stagingPercentage:\s*['""]?(?<percent>[0-9]+)", RegexOptions.CultureInvariant);

    public static int? Percentage(string feed) =>
        StagingPercentage.Match(feed) is { Success: true } match && int.TryParse(match.Groups["percent"].Value, out var percent) ? percent : null;

    // This installation's place in the rollout (0..1), or null when the application
    // has not stored its updater ID yet.
    public static double? Position(InstalledApplication application, string? appDataRoot = null)
    {
        foreach (var directory in UserDataDirectories(application, appDataRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)))
        {
            var position = Position(Path.Combine(directory, ".updaterId"));
            if (position is not null) return position;
        }
        return null;
    }

    internal static double? Position(string updaterIdPath)
    {
        try
        {
            if (!File.Exists(updaterIdPath)) return null;
            var id = File.ReadAllText(updaterIdPath).Trim();
            if (!UpdaterId.IsMatch(id)) return null;
            // Bytes 12..15 of the UUID are its last eight hex digits, in string order.
            return uint.Parse(id[^8..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) / (double)uint.MaxValue;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    public static bool Includes(int percent, double position) => position < percent / 100.0;

    // The smallest rollout percentage that includes this installation.
    public static int ThresholdPercent(double position) => Math.Min(100, (int)Math.Floor(position * 100) + 1);

    public static string HeldMessage(string version, int percent, double? position) => position is { } value
        ? string.Create(CultureInfo.InvariantCulture,
            $"The publisher is rolling {version} out gradually ({percent} % of installations). This installation's rollout position is {value * 100:0.0} %, so the application's own updater does not offer it yet either; it is offered once the rollout reaches {ThresholdPercent(value)} %.")
        : string.Create(CultureInfo.InvariantCulture,
            $"The publisher is rolling {version} out gradually ({percent} % of installations). The application has not stored its rollout ID yet, so it is offered once the rollout is complete.");

    // Electron keeps userData in %APPDATA%\<app name>: the product name for most
    // applications (CurseForge, Vortex), the package name for others whose install
    // folder carries it (Upgrade Sync uses %APPDATA%\upgrade-sync).
    private static IEnumerable<string> UserDataDirectories(InstalledApplication application, string appDataRoot)
    {
        var executable = InstalledApplicationMetadata.Executable(application);
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) yield break;
        var names = new List<string?>();
        try { names.Add(FileVersionInfo.GetVersionInfo(executable).ProductName); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        names.Add(Path.GetFileName(Path.GetDirectoryName(executable)));
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..") continue;
            yield return Path.Combine(appDataRoot, name.Trim());
        }
    }
}
