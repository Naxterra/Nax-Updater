using System.Text.RegularExpressions;

namespace NaxUpdater.Core.Services;

public static partial class VersionNormalizer
{
    public static string? Normalize(string? version, string? strategy)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var trimmed = version.Trim();
        if (!string.Equals(strategy, "FirstThreeNumericComponents", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var components = NumericComponentRegex()
            .Matches(trimmed)
            .Select(static match => match.Value)
            .Take(3)
            .ToArray();
        return components.Length == 3 ? string.Join('.', components) : trimmed;
    }

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex NumericComponentRegex();
}
