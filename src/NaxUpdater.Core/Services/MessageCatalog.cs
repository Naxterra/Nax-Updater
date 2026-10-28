using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NaxUpdater.Core.Services;

// The checks and the update engine write their messages in English, often built from
// several literal pieces with versions and names in between. The catalog holds a German
// version of every such template ("The publisher's site lists {0} as its newest release.")
// and translates a finished message by replacing each English template it contains,
// translating the inserted values as well. Text without a template stays as it is.
public static partial class MessageCatalog
{
    private static readonly Lazy<IReadOnlyList<Entry>> GermanEntries = new(() => Load("de"));
    private static readonly ConcurrentDictionary<string, string> GermanCache = new(StringComparer.Ordinal);

    // Translation runs in the interface, also inside error handlers, so it never throws:
    // text it cannot translate in time is shown as it is. Only a bounded number of texts
    // is cached, because messages with times or error details rarely repeat.
    public static string Translate(string? text, CultureInfo culture)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 4000 || !culture.TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase))
            return text ?? string.Empty;
        if (GermanCache.TryGetValue(text, out var cached)) return cached;
        string translated;
        try
        {
            translated = Apply(text, GermanEntries.Value, 0);
        }
        catch (Exception exception) when (exception is RegexMatchTimeoutException or FormatException)
        {
            translated = text;
        }
        if (GermanCache.Count < 2048) GermanCache.TryAdd(text, translated);
        return translated;
    }

    // Names, commands and identifiers that stay as they are ("same: " lines); they are listed
    // so the catalog test can tell them apart from messages nobody has translated yet.
    internal static IReadOnlyDictionary<string, string> GermanTemplates()
    {
        var templates = GermanEntries.Value.ToDictionary(static entry => entry.English, static entry => entry.Translation, StringComparer.Ordinal);
        foreach (var line in Lines("de"))
            if (line.StartsWith("same: ", StringComparison.Ordinal)) templates.TryAdd(line[6..], line[6..]);
        return templates;
    }

    private static string Apply(string text, IReadOnlyList<Entry> entries, int depth)
    {
        foreach (var entry in entries)
            if (entry.Placeholders == 0 && entry.English.Equals(text, StringComparison.Ordinal))
                return entry.Translation;
        if (depth > 3) return text;
        var result = new StringBuilder(text);
        // Longer templates first, so a sentence is translated as a whole before any
        // shorter template could match a part of it.
        foreach (var entry in entries)
        {
            var current = result.ToString();
            // A phrase of one or two words ("release") is translated only as a whole value,
            // never inside other text.
            if (entry.WordCount < 3 || !current.Contains(entry.Anchor, StringComparison.Ordinal)) continue;
            var replaced = entry.Pattern.Replace(current, match =>
            {
                var values = new object[entry.Placeholders];
                for (var index = 0; index < values.Length; index++)
                    values[index] = Apply(match.Groups["p" + index].Value, entries, depth + 1);
                return string.Format(CultureInfo.InvariantCulture, entry.Translation, values);
            });
            if (replaced != current)
                result.Clear().Append(replaced);
        }
        return result.ToString();
    }

    private static IEnumerable<string> Lines(string language)
    {
        using var stream = typeof(MessageCatalog).Assembly.GetManifestResourceStream($"NaxUpdater.Core.Services.MessageCatalog.{language}.txt");
        if (stream is null) yield break;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line) yield return line;
    }

    private static IReadOnlyList<Entry> Load(string language)
    {
        var entries = new List<Entry>();
        string? english = null;
        foreach (var line in Lines(language))
        {
            if (line.StartsWith("en: ", StringComparison.Ordinal)) english = line[4..];
            else if (line.StartsWith(language + ": ", StringComparison.Ordinal) && english is not null)
            {
                // Surrounding spaces only join pieces of a message; the text keeps its own.
                entries.Add(Entry.Create(english.Trim(), line[(language.Length + 2)..].Trim()));
                english = null;
            }
        }
        return entries.OrderByDescending(static entry => entry.Anchor.Length).ThenByDescending(static entry => entry.English.Length).ToArray();
    }

    private sealed record Entry(string English, string Translation, int Placeholders, string Anchor, Regex Pattern)
    {
        public int WordCount { get; } = Regex.Matches(PlaceholderRegex().Replace(English, " "), "[A-Za-z]+").Count;

        public static Entry Create(string english, string translation)
        {
            var parts = PlaceholderRegex().Split(english);
            // A template that starts with a value starts at the beginning of the text or after
            // a sentence, clause or line break, so earlier text is never taken into the value.
            var pattern = new StringBuilder(parts[0].Length == 0 ? @"(?<=^|[.;:] |\n)" : "(?<![A-Za-z])");
            var placeholders = 0;
            for (var index = 0; index < parts.Length; index++)
            {
                if (index % 2 == 0) pattern.Append(Regex.Escape(Unescape(parts[index])));
                else
                {
                    // A value never spans a sentence boundary or a line, and a value inside the
                    // template also not a clause; a value at the very end runs to the end of the
                    // sentence. The lengths are bounded so backtracking stays small.
                    var last = index == parts.Length - 2 && parts[^1].Length == 0;
                    pattern.Append("(?<p").Append(parts[index]).Append('>')
                        .Append(last ? @"(?:(?!\. [A-Z]|\n).){1,2000}" : @"(?:(?!\. [A-Z]|; |: |\n).){1,300}?").Append(')');
                    placeholders = Math.Max(placeholders, int.Parse(parts[index], CultureInfo.InvariantCulture) + 1);
                }
            }
            var anchor = parts.Where((_, index) => index % 2 == 0).Select(Unescape).OrderByDescending(static part => part.Length).First();
            return new Entry(english, translation, placeholders, anchor,
                new Regex(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromMilliseconds(50)));
        }
    }

    private static string Unescape(string literal) =>
        literal.Replace("{{", "{", StringComparison.Ordinal).Replace("}}", "}", StringComparison.Ordinal);

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex PlaceholderRegex();
}
