using System.Text.RegularExpressions;

namespace NaxUpdater.Core.Services;

public static partial class VersionOrder
{
    public static int Compare(string? left, string? right)
    {
        left = Clean(left);
        right = Clean(right);
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return 0;
        if (left.Length == 0) return -1;
        if (right.Length == 0) return 1;

        if (TryReleaseDate(left, out var leftDate) && TryReleaseDate(right, out var rightDate))
            return leftDate.CompareTo(rightDate);

        var leftVersion = Parse(left);
        var rightVersion = Parse(right);
        if (leftVersion is null || rightVersion is null)
            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);

        var coreOrder = CompareParts(leftVersion.Value.Core, rightVersion.Value.Core);
        if (coreOrder != 0) return coreOrder;
        var leftPre = leftVersion.Value.Prerelease;
        var rightPre = rightVersion.Value.Prerelease;
        if (leftPre is null && rightPre is null) return 0;
        if (leftPre is null) return 1;
        if (rightPre is null) return -1;

        var stageOrder = Stage(leftPre).CompareTo(Stage(rightPre));
        if (stageOrder != 0) return stageOrder;
        return CompareParts(Tokens(leftPre), Tokens(rightPre), prerelease: true);
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var result = value.Trim().Split('+', 2)[0];
        result = Regex.Replace(result, @"^(?:version\s+|v(?=\d))", "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // Git for Windows tags and Windows file versions use these equivalent forms.
        return Regex.Replace(result, @"\.windows\.", ".", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static (string[] Core, string? Prerelease)? Parse(string value)
    {
        var match = CoreRegex().Match(value);
        if (!match.Success) return null;
        var core = match.Groups["core"].Value.Split(['.', ','], StringSplitOptions.TrimEntries).ToList();
        var suffix = match.Groups["suffix"].Value.Trim().TrimStart('-', '.');
        if (suffix.Length == 0 || suffix.Equals("esr", StringComparison.OrdinalIgnoreCase) ||
            suffix.StartsWith("build", StringComparison.OrdinalIgnoreCase))
            return (core.ToArray(), null);
        // Some native updaters publish a fourth numeric build using a dash.
        if (suffix.All(char.IsAsciiDigit))
        {
            core.Add(suffix);
            return (core.ToArray(), null);
        }
        return (core.ToArray(), suffix);
    }

    private static int CompareParts(string[] left, string[] right, bool prerelease = false)
    {
        var count = Math.Max(left.Length, right.Length);
        for (var index = 0; index < count; index++)
        {
            if (prerelease && (index >= left.Length || index >= right.Length))
                return left.Length.CompareTo(right.Length);
            var l = index < left.Length ? left[index] : "0";
            var r = index < right.Length ? right[index] : "0";
            var lNumeric = l.All(char.IsAsciiDigit);
            var rNumeric = r.All(char.IsAsciiDigit);
            int comparison;
            if (lNumeric && rNumeric)
            {
                // Compare digit strings without parsing into a fixed-width integer.
                l = l.TrimStart('0');
                r = r.TrimStart('0');
                comparison = l.Length.CompareTo(r.Length);
                if (comparison == 0) comparison = string.CompareOrdinal(l, r);
            }
            else if (prerelease && lNumeric != rNumeric)
                comparison = lNumeric ? -1 : 1;
            else
                comparison = string.Compare(l, r, StringComparison.OrdinalIgnoreCase);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    private static string[] Tokens(string suffix) =>
        TokenRegex().Matches(suffix).Select(static match => match.Value).ToArray();

    private static int Stage(string suffix)
    {
        var token = Tokens(suffix).FirstOrDefault()?.ToLowerInvariant();
        return token switch
        {
            "dev" or "nightly" => 0,
            "a" or "alpha" => 1,
            "b" or "beta" => 2,
            "pre" or "preview" => 3,
            "rc" => 4,
            _ => 3
        };
    }

    private static bool TryReleaseDate(string value, out DateOnly date)
    {
        var compact = CompactReleaseDateRegex().Match(value);
        var dotted = DottedReleaseDateRegex().Match(value);
        string digits;
        if (compact.Success) digits = compact.Groups[1].Value;
        else if (dotted.Success)
            digits = dotted.Groups[1].Value + dotted.Groups[2].Value + dotted.Groups[3].Value;
        else { date = default; return false; }
        return DateOnly.TryParseExact("20" + digits, "yyyyMMdd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out date);
    }

    [GeneratedRegex(@"^(?<core>\d+(?:[.,]\s*\d+)*)(?<suffix>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex CoreRegex();
    [GeneratedRegex(@"\d+|[A-Za-z]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
    [GeneratedRegex(@"^(\d{6})$", RegexOptions.CultureInvariant)]
    private static partial Regex CompactReleaseDateRegex();
    [GeneratedRegex(@"^(\d{2})\.(\d{2})\.(\d{2})(?:\.0)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DottedReleaseDateRegex();
}
