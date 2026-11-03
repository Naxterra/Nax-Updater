using NaxUpdater.Core.Models;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NaxUpdater.Core.Services;

// A plain-text report of one update row that can be pasted into an issue. It is redacted by
// default: local and network paths, the user and computer name, URL query strings (signed
// download links carry their credentials there) and anything that looks like a credential.
public static partial class UpdateDiagnostics
{
    public static string Build(
        UpdateCheckResult update,
        string? runState,
        string? runMessage,
        IEnumerable<UpdateHistoryEntry> history,
        string applicationVersion)
    {
        var text = new StringBuilder();
        void Line(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) text.Append(label).Append(": ").AppendLine(value);
        }
        text.AppendLine($"Nax-Updater {applicationVersion} diagnostics");
        Line("Application", update.DisplayName);
        Line("Provider", $"{update.ProviderId} ({update.ProviderDisplayName})");
        Line("Installed version", update.InstalledVersion);
        Line("Available version", update.AvailableVersion);
        Line("Status", update.AvailabilityReason == UpdateAvailabilityReason.None
            ? update.Status.ToString()
            : $"{update.Status} ({update.AvailabilityReason})");
        Line("Architecture, channel, language", $"{update.Architecture}, {update.Channel}, {update.Language}");
        if (update.ExecutionPlan is { } plan)
        {
            Line("Update plan", string.Join(", ", new[]
            {
                plan.Kind.ToString(),
                plan.DownloadUri is null ? null : $"download host {plan.DownloadUri.Host}",
                plan.Sha256 is not null || plan.Sha512 is not null ? "publisher hash" : plan.SignatureOnly ? "signature only" : null,
                plan.ExpectedSigner is null ? null : $"signer {plan.ExpectedSigner}",
                plan.RequiresElevation ? "elevated" : "not elevated"
            }.Where(static part => part is not null)));
        }
        Line("Provider notes", update.Message);
        foreach (var source in update.SourceChecks ?? [])
            Line($"Source check {source.ProviderId}", $"{source.Status} {source.AvailableVersion} {source.Message}".Trim());
        Line("Last run", string.IsNullOrWhiteSpace(runMessage) ? runState : $"{runState}: {runMessage}");
        foreach (var entry in history.Take(5))
        {
            Line($"History {entry.FinishedAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)}", string.Join(", ", new[]
            {
                entry.Stage.ToString(),
                $"{entry.InstalledVersion ?? "?"} to {entry.TargetVersion ?? "?"}",
                entry.ObservedVersion is null ? null : $"read afterwards {entry.ObservedVersion}",
                entry.ExitCode is null ? null : $"exit code {entry.ExitCode}",
                entry.Error
            }.Where(static part => !string.IsNullOrWhiteSpace(part))));
        }
        return Redact(text.ToString());
    }

    public static string Redact(string text) => Redact(text, Environment.UserName, Environment.MachineName);

    internal static string Redact(string text, string? userName, string? machineName)
    {
        // Credentials first, so a token inside a URL or path is gone before anything else runs.
        text = GitHubTokenRegex().Replace(text, "<redacted>");
        text = BearerRegex().Replace(text, "Bearer <redacted>");
        text = CredentialRegex().Replace(text, static match => $"{match.Groups["name"].Value}=<redacted>");
        text = UrlRegex().Replace(text, static match => RedactUrl(match.Value));
        // A path may contain spaces, so it runs to the end of the line or a quote, and everything
        // before its last backslash is replaced; only the file name and the text after it remain.
        text = LocalPathRegex().Replace(text, static match => "<path>\\" + match.Value[(match.Value.LastIndexOf('\\') + 1)..]);
        text = NetworkPathRegex().Replace(text, static match => "<network path>\\" + match.Value[(match.Value.LastIndexOf('\\') + 1)..]);
        if (!string.IsNullOrWhiteSpace(userName) && userName.Length > 1)
            text = Regex.Replace(text, $@"\b{Regex.Escape(userName)}\b", "<user>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!string.IsNullOrWhiteSpace(machineName) && machineName.Length > 1)
            text = Regex.Replace(text, $@"\b{Regex.Escape(machineName)}\b", "<computer>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return text;
    }

    // A URL keeps its scheme, host and readable path. The query and fragment go, and so do a
    // user name and password before the host and a long random path segment, which is where
    // signed CDN links carry their token when it is not in the query.
    private static string RedactUrl(string value)
    {
        var cut = value.IndexOfAny(['?', '#']);
        if (cut >= 0) value = value[..cut];
        var scheme = value.IndexOf("://", StringComparison.Ordinal) + 3;
        var rest = value[scheme..];
        // A well-formed URL names its user part itself; it may be followed by an "@" in the path
        // (npm's /@scope), which stays.
        var parsedOk = Uri.TryCreate(value, UriKind.Absolute, out var parsed);
        if (parsedOk && parsed!.UserInfo.Length > 0 && rest.StartsWith(parsed.UserInfo + "@", StringComparison.Ordinal))
        {
            rest = rest[(parsed.UserInfo.Length + 1)..];
        }
        else
        {
            // A password with an unencoded "/" puts the "@" behind a "/": the parser then fails
            // ("john.doe:pa/ss@host") or reads the user name as the host ("u:12/34@host"). Text
            // up to such an "@" is a user part only when a host name with a dot follows the "@",
            // and either the parser failed or no host name with a dot precedes the first "/".
            // So "/@scope" paths on localhost and real hosts, and e-mail addresses in the path of
            // a real host, stay as they are.
            var at = rest.LastIndexOf('@');
            if (at > 0)
            {
                var after = rest[(at + 1)..];
                var hostAfter = after.Split('/')[0];
                var authorityHost = rest.Split('/')[0].Split(':')[0];
                if (hostAfter.Contains('.') && (!parsedOk || !authorityHost.Contains('.'))) rest = after;
            }
        }
        var parts = rest.Split('/');
        for (var index = 1; index < parts.Length; index++)
            if (TokenSegmentRegex().IsMatch(parts[index])) parts[index] = "<redacted>";
        return value[..scheme] + string.Join('/', parts);
    }

    // At least 32 characters with letters and digits that do not end in a short file extension.
    // Dots are allowed, because a JSON web token in a path has them.
    [GeneratedRegex(@"^(?=.*\d)(?=.*[A-Za-z])(?!.*\.[A-Za-z0-9]{1,5}$)[A-Za-z0-9_\-=~%.]{32,}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenSegmentRegex();

    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9]{20,}\b|\bgithub_pat_[A-Za-z0-9_]{20,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"\bBearer\s+\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"\b(?<name>token|access_token|password|passwd|pwd|secret|api[_-]?key|authorization|x-amz-signature|x-amz-credential)\s*[=:]\s*[^\s&""',;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialRegex();

    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"\b[A-Za-z]:\\[^\r\n""'<>|]*", RegexOptions.CultureInvariant)]
    private static partial Regex LocalPathRegex();

    [GeneratedRegex(@"\\\\[^\s\\""'<>|]+\\[^\r\n""'<>|]*", RegexOptions.CultureInvariant)]
    private static partial Regex NetworkPathRegex();
}
