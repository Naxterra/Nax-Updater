using System.Text.RegularExpressions;

namespace NaxUpdater.Core.Services;

public static partial class VersionOrder
{
    public static int Compare(string? left, string? right)
    {
        if (string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        if (string.IsNullOrWhiteSpace(left))
        {
            return -1;
        }
        if (string.IsNullOrWhiteSpace(right))
        {
            return 1;
        }

        var leftCore = PrereleaseRegex().Replace(left, string.Empty);
        var rightCore = PrereleaseRegex().Replace(right, string.Empty);
        var leftParts = NumericPartRegex().Matches(leftCore).Select(static match => long.Parse(match.Value)).ToArray();
        var rightParts = NumericPartRegex().Matches(rightCore).Select(static match => long.Parse(match.Value)).ToArray();
        var count = Math.Max(leftParts.Length, rightParts.Length);
        for (var index = 0; index < count; index++)
        {
            var leftPart = index < leftParts.Length ? leftParts[index] : 0;
            var rightPart = index < rightParts.Length ? rightParts[index] : 0;
            var comparison = leftPart.CompareTo(rightPart);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        var leftIsPrerelease = IsPrerelease(left);
        var rightIsPrerelease = IsPrerelease(right);
        if (leftIsPrerelease != rightIsPrerelease)
        {
            return leftIsPrerelease ? -1 : 1;
        }
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrerelease(string value) => PrereleaseRegex().IsMatch(value) &&
                                                       !value.Contains("esr", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex NumericPartRegex();

    [GeneratedRegex(@"(?:alpha|beta|rc|a|b)\d*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrereleaseRegex();
}
