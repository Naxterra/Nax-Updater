using System.Xml;
using System.Xml.Linq;

namespace NaxUpdater.Core.Services;

public static class MsixManifestInspector
{
    public static MsixManifestInspection Inspect(string manifestPath, string packageDirectory)
    {
        if (!File.Exists(manifestPath))
        {
            return MsixManifestInspection.Empty;
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };
        using var stream = File.OpenRead(manifestPath);
        using var reader = XmlReader.Create(stream, settings);
        var document = XDocument.Load(reader, LoadOptions.None);

        var declaredExecutables = document.Descendants()
            .Where(static element => element.Name.LocalName == "Application")
            .Select(static element => element.Attribute("Executable")?.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var extensionCategories = document.Descendants()
            .Where(static element => element.Name.LocalName == "Extension")
            .Select(static element => element.Attribute("Category")?.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var allDeclaredExecutablesAreExternal = declaredExecutables.Length > 0 &&
                                                declaredExecutables.All(executable => !ExistsInsidePackage(packageDirectory, executable));
        var isExternalIntegrationPackage = allDeclaredExecutablesAreExternal && extensionCategories.Length > 0;
        return new MsixManifestInspection(
            isExternalIntegrationPackage,
            declaredExecutables,
            extensionCategories);
    }

    private static bool ExistsInsidePackage(string packageDirectory, string declaredExecutable)
    {
        try
        {
            if (Path.IsPathRooted(declaredExecutable))
            {
                return false;
            }

            var relativePath = declaredExecutable
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar);
            var packageRoot = Path.GetFullPath(packageDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidatePath = Path.GetFullPath(Path.Combine(packageRoot, relativePath));
            return candidatePath.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(candidatePath);
        }
        catch
        {
            return false;
        }
    }
}

public sealed record MsixManifestInspection(
    bool IsExternalIntegrationPackage,
    IReadOnlyList<string> DeclaredExecutables,
    IReadOnlyList<string> ExtensionCategories)
{
    public static MsixManifestInspection Empty { get; } = new(false, [], []);
}
