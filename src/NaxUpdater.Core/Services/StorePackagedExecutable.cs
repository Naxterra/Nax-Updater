using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace NaxUpdater.Core.Services;

// Store packages are signed as a whole; their files carry no Authenticode
// signature. Windows validates the package signature at install and protects
// the WindowsApps folder, so a tool is trusted when it lies inside the installed
// folder of the exact, Store-signed package family.
internal static class StorePackagedExecutable
{
    public static string? Resolve(string packageFamilyName, string relativeExecutable)
    {
        foreach (var root in StoreSignedRoots(packageFamilyName))
        {
            var candidate = Path.GetFullPath(Path.Combine(root, relativeExecutable));
            if (candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static bool IsInsideStoreSignedPackage(string executable, string packageFamilyName)
    {
        var fullPath = Path.GetFullPath(executable);
        return File.Exists(fullPath) && StoreSignedRoots(packageFamilyName)
            .Any(root => fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> StoreSignedRoots(string packageFamilyName)
    {
        var roots = new List<string>();
        try
        {
            foreach (var package in new PackageManager().FindPackagesForUser(string.Empty, packageFamilyName))
            {
                if (package.SignatureKind != PackageSignatureKind.Store ||
                    !package.Id.FamilyName.Equals(packageFamilyName, StringComparison.OrdinalIgnoreCase)) continue;
                roots.Add(Path.GetFullPath(package.InstalledLocation.Path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Runtime.InteropServices.COMException) { }
        return roots;
    }
}
