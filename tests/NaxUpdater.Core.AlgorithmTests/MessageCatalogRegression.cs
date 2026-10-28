using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NaxUpdater.Core.Services;

// The checks write English messages; the German interface shows them through the
// message catalog. Each method is one scenario.
internal static class MessageCatalogRegression
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    public static void Run(Action<bool, string> assert)
    {
        every_core_message_has_a_german_translation(assert);
        german_message_keeps_versions_and_names(assert);
        english_setting_shows_the_message_unchanged(assert);
        unknown_text_is_shown_unchanged(assert);
        composed_scan_messages_are_shown_in_german(assert);
        long_or_unusual_text_never_throws(assert);
    }

    // Translation runs inside the interface's error handlers; a long error text full of
    // template fragments must come back quickly and without an exception.
    private static void long_or_unusual_text_never_throws(Action<bool, string> assert)
    {
        var fragment = "x match to y; z reports the current version: Chocolatey published a b, but c, and d e does not pin one version of f; ";
        var text = string.Concat(Enumerable.Repeat(fragment, 30));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string? result = null;
        Exception? failure = null;
        try { result = MessageCatalog.Translate(text, German); }
        catch (Exception exception) { failure = exception; }
        assert(failure is null && result is { Length: > 0 }, "Translating a long error text threw: " + failure?.GetType().Name);
        assert(watch.Elapsed < TimeSpan.FromSeconds(2), $"Translating a long error text took {watch.Elapsed.TotalMilliseconds:F0} ms.");
        var huge = new string('a', 5000);
        assert(MessageCatalog.Translate(huge, German) == huge, "A very long text was not shown unchanged.");
    }

    // Messages as a real scan composes them from several pieces and values.
    private static void composed_scan_messages_are_shown_in_german(Action<bool, string> assert)
    {
        string[] messages =
        [
            "The underlying device drivers were checked against manufacturer sources. Package and INF versions are not interchangeable. NVIDIA High Definition Audio: NoVerifiedCatalog. The driver package is inventoried, but no exact manufacturer catalog identity is available. No action is offered.",
            "registered product code match to Rustlang.Rustup; WinGet fallback catalog reports the current version.",
            "The native Microsoft Store check at 28.10.2026 16:24 found no applicable update; the installed version and the Store catalog are unchanged since.",
            "The installed version 6, 43, 16, 2 is newer than the fallback catalog version 6.43.16.1; the older catalog value is not presented as an available release.",
            "The installed update owner is Battle.net. No compatible version-check protocol is implemented for this owner; current status is unknown.",
            "GitHub's newest release or prerelease is 2.0.0-alpha.44, which is not newer than the installed version.",
            "No producer installer configuration preserves the installed user/machine scope."
        ];
        // Typical English words that the German text never contains.
        var english = new Regex(@"\b(the|is|was|were|are|not|reports|match to|current version|installed)\b", RegexOptions.IgnoreCase);
        foreach (var message in messages)
        {
            var german = MessageCatalog.Translate(message, German);
            assert(!english.IsMatch(german.Replace("NoVerifiedCatalog", string.Empty, StringComparison.Ordinal)), "A composed message kept English text: " + german);
        }
        if (Environment.GetEnvironmentVariable("NAXUPDATER_TRANSLATE_FILE") is { Length: > 0 } input && File.Exists(input))
            File.WriteAllLines(input + ".de.txt", File.ReadAllLines(input).Select(line => MessageCatalog.Translate(line, German)), new UTF8Encoding(false));
    }

    private static void every_core_message_has_a_german_translation(Action<bool, string> assert)
    {
        var catalog = MessageCatalog.GermanTemplates();
        var missing = CoreMessageTemplates().Where(template => !catalog.ContainsKey(template)).Distinct().Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0 && Environment.GetEnvironmentVariable("NAXUPDATER_MISSING_MESSAGES") is { Length: > 0 } output)
            File.WriteAllLines(output, missing.Select(static template => "en: " + template), new UTF8Encoding(false));
        assert(missing.Count == 0, "Messages without a German translation: " + string.Join(" | ", missing.Take(5)));
        var placeholderMismatch = catalog.Where(static pair => Placeholders(pair.Key) != Placeholders(pair.Value)).Select(static pair => pair.Key).ToList();
        assert(placeholderMismatch.Count == 0, "German messages that lose or add a value: " + string.Join(" | ", placeholderMismatch.Take(5)));
    }

    private static void german_message_keeps_versions_and_names(Action<bool, string> assert)
    {
        var text = MessageCatalog.Translate("The publisher's site lists 26.10.21.0 as its newest release.", German);
        assert(text.Contains("26.10.21.0", StringComparison.Ordinal) && !text.Contains("newest release", StringComparison.Ordinal),
            "A German message lost its version or kept the English sentence: " + text);
        var joined = MessageCatalog.Translate(
            "The underlying device drivers were checked against manufacturer sources. Package and INF versions are not interchangeable.", German);
        assert(!joined.Contains("checked against", StringComparison.Ordinal) && !joined.Contains("interchangeable", StringComparison.Ordinal),
            "A message made of two sentences was not translated sentence by sentence: " + joined);
    }

    private static void english_setting_shows_the_message_unchanged(Action<bool, string> assert)
    {
        const string message = "The publisher's site lists 26.10.21.0 as its newest release.";
        assert(MessageCatalog.Translate(message, English) == message, "The English interface changed an English message.");
    }

    private static void unknown_text_is_shown_unchanged(Action<bool, string> assert)
    {
        const string message = "Zugriff verweigert (0x80070005)";
        assert(MessageCatalog.Translate(message, German) == message, "Text without a template was changed.");
    }

    // The set of placeholder numbers, so "{0} {2}" against "{0} {1}" counts as a mismatch.
    private static string Placeholders(string template) =>
        string.Join(",", Regex.Matches(template, @"\{(\d+)\}").Select(static match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().Order());

    // Every string literal in the Core sources that reads as a sentence: literals joined
    // with "+" form one template, and interpolation holes become {0}, {1}, ...
    internal static IEnumerable<string> CoreMessageTemplates()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "NaxUpdater.slnx"))) root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("Repository root not found.");
        var core = Path.Combine(root, "src", "NaxUpdater.Core");
        foreach (var file in Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                Path.GetFileName(file) == "MessageCatalog.cs") continue;
            foreach (var template in Templates(File.ReadAllText(file)))
                if (IsMessage(template)) yield return template.Trim();
        }
    }

    internal static bool IsMessage(string template)
    {
        var words = Regex.Matches(template, @"(?<![{\w])[A-Za-z][A-Za-z'-]*").Count;
        return words >= 3 && template.Contains(' ') &&
            !template.Contains("://", StringComparison.Ordinal) && !template.StartsWith('^') && !template.Contains("(?", StringComparison.Ordinal) &&
            !template.Contains('\\') && !template.Contains('\n') && !template.StartsWith('-') && !template.StartsWith('/') &&
            !Regex.IsMatch(template, @"^[A-Za-z]+(\.[A-Za-z]+)+$") && !template.Contains("SELECT ", StringComparison.Ordinal);
    }

    private static List<string> Templates(string source)
    {
        var templates = new List<string>();
        var index = 0;
        var pending = new StringBuilder();
        var pendingHoles = 0;
        var pendingOpen = false;
        void Flush()
        {
            if (pendingOpen) templates.Add(pending.ToString());
            pending.Clear();
            pendingHoles = 0;
            pendingOpen = false;
        }
        while (index < source.Length)
        {
            var c = source[index];
            if (c == '/' && index + 1 < source.Length && source[index + 1] == '/') { index = source.IndexOf('\n', index) is var end and >= 0 ? end : source.Length; continue; }
            if (c == '/' && index + 1 < source.Length && source[index + 1] == '*') { index = source.IndexOf("*/", index + 2, StringComparison.Ordinal) is var end and >= 0 ? end + 2 : source.Length; continue; }
            if (c == '\'') { index = SkipChar(source, index); continue; }
            var start = index;
            var interpolated = false;
            var verbatim = false;
            while (index < source.Length && source[index] is '$' or '@')
            {
                if (source[index] == '$') interpolated = true; else verbatim = true;
                index++;
            }
            if (index < source.Length && source[index] == '"')
            {
                if (index + 2 < source.Length && source[index + 1] == '"' && source[index + 2] == '"')
                {
                    var close = source.IndexOf("\"\"\"", index + 3, StringComparison.Ordinal);
                    index = close < 0 ? source.Length : close + 3;
                    Flush();
                    continue;
                }
                var (text, holes, next, nested) = ReadString(source, index + 1, interpolated, verbatim, pendingHoles);
                templates.AddRange(nested);
                pending.Append(text);
                pendingHoles = holes;
                pendingOpen = true;
                index = next;
                // Literals joined with "+" continue the same message.
                var look = index;
                while (look < source.Length && char.IsWhiteSpace(source[look])) look++;
                if (look < source.Length && source[look] == '+')
                {
                    look++;
                    while (look < source.Length && char.IsWhiteSpace(source[look])) look++;
                    var quote = look;
                    while (quote < source.Length && source[quote] is '$' or '@') quote++;
                    if (quote < source.Length && source[quote] == '"') { index = look; continue; }
                }
                Flush();
                continue;
            }
            index = start + 1;
        }
        Flush();
        return templates;
    }

    private static int SkipChar(string source, int index)
    {
        index++;
        while (index < source.Length && source[index] != '\'')
        {
            if (source[index] == '\\') index++;
            index++;
        }
        return index + 1;
    }

    private static (string Text, int Holes, int Next, List<string> Nested) ReadString(string source, int index, bool interpolated, bool verbatim, int holeOffset)
    {
        var text = new StringBuilder();
        var nested = new List<string>();
        var holes = holeOffset;
        while (index < source.Length)
        {
            var c = source[index];
            if (c == '"')
            {
                if (verbatim && index + 1 < source.Length && source[index + 1] == '"') { text.Append('"'); index += 2; continue; }
                return (text.ToString(), holes, index + 1, nested);
            }
            if (!verbatim && c == '\\' && index + 1 < source.Length)
            {
                var escaped = source[index + 1];
                text.Append(escaped switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', _ => escaped });
                if (escaped is not ('n' or 't' or 'r' or '0' or '"' or '\\' or '\'')) text.Insert(text.Length - 1, '\\');
                index += 2;
                continue;
            }
            if (interpolated && c == '{')
            {
                if (index + 1 < source.Length && source[index + 1] == '{') { text.Append("{{"); index += 2; continue; }
                var end = HoleEnd(source, index + 1);
                nested.AddRange(Templates(source[(index + 1)..end]).Where(IsMessage));
                text.Append('{').Append(holes++).Append('}');
                index = end + 1;
                continue;
            }
            if (interpolated && c == '}' && index + 1 < source.Length && source[index + 1] == '}') { text.Append("}}"); index += 2; continue; }
            if (!interpolated && (c == '{' || c == '}'))
            {
                text.Append(c).Append(c);
                index++;
                continue;
            }
            text.Append(c);
            index++;
        }
        return (text.ToString(), holes, index, nested);
    }

    private static int HoleEnd(string source, int index)
    {
        var depth = 0;
        while (index < source.Length)
        {
            var c = source[index];
            if (c == '"' || (c is '$' or '@' && index + 1 < source.Length && source[index + 1] is '"' or '$' or '@'))
            {
                var interpolated = false;
                var verbatim = false;
                while (source[index] is '$' or '@') { if (source[index] == '$') interpolated = true; else verbatim = true; index++; }
                index = ReadString(source, index + 1, interpolated, verbatim, 0).Next;
                continue;
            }
            if (c == '\'') { index = SkipChar(source, index); continue; }
            if (c is '{' or '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == '}')
            {
                if (depth == 0) return index;
                depth--;
            }
            index++;
        }
        return index;
    }
}
