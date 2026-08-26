using NaxUpdater.Core.Internal;
using NaxUpdater.Core.Models;
using System.Runtime.InteropServices;

namespace NaxUpdater.Core.Services;

internal sealed class ShortcutScanner
{
    public IReadOnlyList<ShortcutInfo> Scan(ICollection<InventoryIssue> issues)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            issues.Add(new InventoryIssue("Windows shortcuts", "Windows Script Host shortcut support is unavailable."));
            return [];
        }

        object? shell = null;
        var results = new List<ShortcutInfo>();
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return [];
            }

            foreach (var root in ShortcutRoots())
            {
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    });
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    issues.Add(new InventoryIssue("Windows shortcuts", $"Could not enumerate {root}: {exception.Message}", exception.GetType().Name));
                    continue;
                }

                foreach (var file in files)
                {
                    object? shortcut = null;
                    try
                    {
                        shortcut = shellType.InvokeMember(
                            "CreateShortcut",
                            System.Reflection.BindingFlags.InvokeMethod,
                            null,
                            shell,
                            [file]);
                        if (shortcut is null)
                        {
                            continue;
                        }

                        var shortcutType = shortcut.GetType();
                        var target = shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null)?.ToString();
                        var arguments = shortcutType.InvokeMember("Arguments", System.Reflection.BindingFlags.GetProperty, null, shortcut, null)?.ToString();
                        results.Add(new ShortcutInfo(
                            Path.GetFileNameWithoutExtension(file),
                            file,
                            string.IsNullOrWhiteSpace(target) ? null : Environment.ExpandEnvironmentVariables(target.Trim()),
                            string.IsNullOrWhiteSpace(arguments) ? null : arguments.Trim()));
                    }
                    catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException)
                    {
                        issues.Add(new InventoryIssue("Windows shortcuts", $"Could not inspect {file}: {exception.Message}", exception.GetType().Name));
                    }
                    finally
                    {
                        ReleaseComObject(shortcut);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException)
        {
            issues.Add(new InventoryIssue("Windows shortcuts", exception.Message, exception.GetType().Name));
        }
        finally
        {
            ReleaseComObject(shell);
        }

        return results;
    }

    public static void ApplyMatches(ApplicationCandidate candidate, IReadOnlyList<ShortcutInfo> shortcuts)
    {
        var applicationName = NativePathParser.NormalizeName(candidate.DisplayName);
        foreach (var shortcut in shortcuts)
        {
            var shortcutName = NativePathParser.NormalizeName(shortcut.Name);
            if (!string.Equals(applicationName, shortcutName, StringComparison.Ordinal))
            {
                continue;
            }

            var description = string.IsNullOrWhiteSpace(shortcut.TargetPath)
                ? shortcut.ShortcutPath
                : $"{shortcut.ShortcutPath} → {shortcut.TargetPath}";
            candidate.Evidence.Add(new ApplicationEvidence(
                EvidenceKind.Shortcut,
                "Windows shortcut",
                description,
                File.Exists(shortcut.ShortcutPath)));
            if (!string.IsNullOrWhiteSpace(shortcut.TargetPath))
            {
                var verified = File.Exists(shortcut.TargetPath) || Directory.Exists(shortcut.TargetPath);
                candidate.Paths.Add(new PathCandidate(shortcut.TargetPath, "Windows shortcut", 90, verified));
            }
            if (!string.IsNullOrWhiteSpace(shortcut.Arguments))
            {
                candidate.Evidence.Add(new ApplicationEvidence(
                    EvidenceKind.Shortcut,
                    "Shortcut arguments",
                    shortcut.Arguments));
            }
        }
    }

    private static IEnumerable<string> ShortcutRoots()
    {
        return new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        }
        .Where(static path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}

internal sealed record ShortcutInfo(
    string Name,
    string ShortcutPath,
    string? TargetPath,
    string? Arguments);
