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
        // With the same numbers a prerelease is older than the release, and a patch level
        // ("7.1a", "3.0 SP1") is newer.
        var kindOrder = leftVersion.Value.Kind.CompareTo(rightVersion.Value.Kind);
        if (kindOrder != 0) return kindOrder;
        var leftSuffix = leftVersion.Value.Suffix;
        var rightSuffix = rightVersion.Value.Suffix;
        if (leftSuffix is null || rightSuffix is null) return 0;
        if (leftVersion.Value.Kind == SuffixKind.Prerelease)
        {
            var stageOrder = Stage(leftSuffix).CompareTo(Stage(rightSuffix));
            if (stageOrder != 0) return stageOrder;
        }
        return CompareParts(Tokens(leftSuffix), Tokens(rightSuffix), prerelease: true);
    }

    private enum SuffixKind { Prerelease, Release, PatchLevel }

    // Only these words mark a version as older than the release with the same numbers. Any other
    // text after the numbers was read as a prerelease too, so an installed "7.1a" or
    // "2.6.12-I001" counted as older than "7.1" or "2.6.12" and the same release was offered as
    // an update.
    private static readonly HashSet<string> PrereleaseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "dev", "nightly", "snapshot", "canary", "insider", "insiders", "alpha", "beta", "pre", "preview", "prerelease", "rc",
        "ea", "eap", "exp", "experimental", "test", "testing", "unstable", "next", "edge", "milestone"
    };

    // A word followed by a number that marks a later patch level of the same release.
    private static readonly HashSet<string> PatchLevelWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "sp", "hotfix", "hf", "patch", "fix", "update", "u", "r", "rev", "p"
    };

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var result = value.Trim().Split('+', 2)[0];
        result = Regex.Replace(result, @"^(?:version\s+|v(?=\d))", "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // Git for Windows tags and Windows file versions use these equivalent forms.
        return Regex.Replace(result, @"\.windows\.", ".", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static (string[] Core, SuffixKind Kind, string? Suffix)? Parse(string value)
    {
        var match = CoreRegex().Match(value);
        if (!match.Success) return null;
        var core = match.Groups["core"].Value.Split(['.', ','], StringSplitOptions.TrimEntries).ToList();
        // Text in parentheses ("(64-bit)", a build number) describes the release, not its order.
        var suffix = ParenthesesRegex().Replace(match.Groups["suffix"].Value, " ").Trim().TrimStart('-', '.', '_').Trim();
        if (suffix.Length == 0 || suffix.Equals("esr", StringComparison.OrdinalIgnoreCase) ||
            suffix.StartsWith("build", StringComparison.OrdinalIgnoreCase))
            return (core.ToArray(), SuffixKind.Release, null);
        // Some native updaters publish a fourth numeric build using a dash.
        if (suffix.All(char.IsAsciiDigit))
        {
            core.Add(suffix);
            return (core.ToArray(), SuffixKind.Release, null);
        }
        var tokens = Tokens(suffix);
        if (tokens.Length == 0) return (core.ToArray(), SuffixKind.Release, null);
        var first = tokens[0];
        var numbered = tokens.Length > 1 && tokens[1].All(char.IsAsciiDigit);
        // "155.0b5" and "3.13.0a1" are a beta and an alpha; "m1" is a milestone.
        if (PrereleaseWords.Contains(first) || numbered && first.ToLowerInvariant() is "a" or "b" or "m")
            return (core.ToArray(), SuffixKind.Prerelease, suffix);
        // A single letter alone ("1.0.2a", "7.1a") is a patch release, as is "SP1" or "u401".
        if (tokens.Length == 1 && first.Length == 1 && char.IsAsciiLetter(first[0]) || numbered && PatchLevelWords.Contains(first))
            return (core.ToArray(), SuffixKind.PatchLevel, suffix);
        // Any other text ("stable", "-I001", an edition name) says nothing about the order, so
        // the version counts as the release with these numbers.
        return (core.ToArray(), SuffixKind.Release, null);
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
            "dev" or "nightly" or "snapshot" or "canary" => 0,
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
            digits = dotted.Groups[1].Value + dotted.Groups[2].Value.PadLeft(2, '0') + dotted.Groups[3].Value.PadLeft(2, '0');
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
    // "24.1.2" is the same date as "24.01.02", so both order the same way against "231105".
    [GeneratedRegex(@"^(\d{2})\.(\d{1,2})\.(\d{1,2})(?:\.0)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DottedReleaseDateRegex();
    [GeneratedRegex(@"\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex ParenthesesRegex();
}
