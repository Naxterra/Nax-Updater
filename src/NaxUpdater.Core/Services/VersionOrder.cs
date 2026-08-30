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

        if (TryParseCompactReleaseDate(left, out var leftDate) &&
            TryParseCompactReleaseDate(right, out var rightDate) &&
            leftDate == rightDate)
        {
            return 0;
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

    private static bool TryParseCompactReleaseDate(string value, out int releaseDate)
    {
        var compact = CompactReleaseDateRegex().Match(value.Trim());
        if (compact.Success)
        {
            releaseDate = int.Parse(compact.Groups[1].Value);
            return IsValidReleaseDate(releaseDate);
        }

        var dotted = DottedReleaseDateRegex().Match(value.Trim());
        if (dotted.Success)
        {
            releaseDate = int.Parse(dotted.Groups[1].Value) * 10_000 +
                          int.Parse(dotted.Groups[2].Value) * 100 +
                          int.Parse(dotted.Groups[3].Value);
            return IsValidReleaseDate(releaseDate);
        }

        releaseDate = 0;
        return false;
    }

    private static bool IsValidReleaseDate(int releaseDate)
    {
        var year = releaseDate / 10_000;
        var month = releaseDate / 100 % 100;
        var day = releaseDate % 100;
        return year is >= 0 and <= 99 &&
               month is >= 1 and <= 12 &&
               day is >= 1 and <= 31;
    }

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex NumericPartRegex();

    [GeneratedRegex(@"(?:alpha|beta|rc|a|b)\d*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrereleaseRegex();

    [GeneratedRegex(@"^(\d{6})$")]
    private static partial Regex CompactReleaseDateRegex();

    [GeneratedRegex(@"^(\d{2})\.(\d{2})\.(\d{2})(?:\.0)?$")]
    private static partial Regex DottedReleaseDateRegex();
}
