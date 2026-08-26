using System.Text;

namespace NaxUpdater.Core.Internal;

internal static class NativePathParser
{
    public static string? FromDisplayIcon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (expanded.StartsWith('"'))
        {
            var closingQuote = expanded.IndexOf('"', 1);
            return closingQuote > 1 ? expanded[1..closingQuote].Trim() : null;
        }

        var extensionEnd = FindExecutableExtensionEnd(expanded);
        return extensionEnd > 0 ? expanded[..extensionEnd].Trim().Trim('"') : expanded.Trim().Trim('"');
    }

    public static string? ExecutableFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        if (expanded.StartsWith('"'))
        {
            var closingQuote = expanded.IndexOf('"', 1);
            return closingQuote > 1 ? expanded[1..closingQuote] : null;
        }

        var executableEnd = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return executableEnd >= 0 ? expanded[..(executableEnd + 4)].Trim() : expanded.Split(' ', 2)[0].Trim();
    }

    public static (string? Executable, string Arguments) SplitExecutableAndArguments(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return (null, string.Empty);
        }
        var expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        string? executable;
        var argumentStart = 0;
        if (expanded.StartsWith('"'))
        {
            var closingQuote = expanded.IndexOf('"', 1);
            if (closingQuote <= 1)
            {
                return (null, string.Empty);
            }
            executable = expanded[1..closingQuote];
            argumentStart = closingQuote + 1;
        }
        else
        {
            var executableEnd = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (executableEnd < 0)
            {
                return (null, string.Empty);
            }
            argumentStart = executableEnd + 4;
            executable = expanded[..argumentStart].Trim();
        }

        if (!Path.IsPathRooted(executable))
        {
            var systemCandidate = Path.Combine(Environment.SystemDirectory, Path.GetFileName(executable));
            if (File.Exists(systemCandidate))
            {
                executable = systemCandidate;
            }
        }
        return (executable, expanded[argumentStart..].Trim());
    }

    public static string NormalizeName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }
        return builder.ToString();
    }

    public static string? FindLikelyExecutable(string directory, string displayName)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return null;
            }

            var normalizedName = NormalizeName(displayName);
            return Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly)
                .Select(path => new
                {
                    Path = path,
                    Score = ScoreExecutable(path, normalizedName)
                })
                .OrderByDescending(static item => item.Score)
                .ThenBy(static item => item.Path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(static item => item.Score > 0)
                ?.Path;
        }
        catch
        {
            return null;
        }
    }

    private static int ScoreExecutable(string path, string normalizedDisplayName)
    {
        var name = NormalizeName(Path.GetFileNameWithoutExtension(path));
        if (name is "uninstall" or "unins000" or "setup" or "update" or "updater")
        {
            return 0;
        }
        if (name == normalizedDisplayName)
        {
            return 100;
        }
        if (normalizedDisplayName.Length >= 4 &&
            (name.Contains(normalizedDisplayName, StringComparison.Ordinal) ||
             normalizedDisplayName.Contains(name, StringComparison.Ordinal)))
        {
            return 60;
        }
        return 10;
    }

    private static int FindExecutableExtensionEnd(string value)
    {
        foreach (var extension in new[] { ".exe", ".dll", ".ico" })
        {
            var index = value.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return index + extension.Length;
            }
        }
        return -1;
    }
}
