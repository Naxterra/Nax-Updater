using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace NaxUpdater.Core.Services;

// Store packages are signed as a whole; their files carry no Authenticode
// signature. Windows validates the package signature at install and protects
// the WindowsApps folder, so a tool is trusted when it lies inside the installed
// folder of the exact, Store-signed package family.
internal static class StorePackagedExecutable
{
    // Packages that their publisher also distributes directly, signed with its own
    // certificate (SignatureKind Developer). The family name is derived from the exact
    // publisher name ("CN=Python Software Foundation, ..."), and Windows validated the
    // package signature at install, so the family identifies the publisher as firmly.
    private static readonly HashSet<string> PublisherSignedFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "PythonSoftwareFoundation.PythonManager_3847v3x7pw1km"
    };

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
                var trustedSignature = package.SignatureKind == PackageSignatureKind.Store ||
                    package.SignatureKind == PackageSignatureKind.Developer && PublisherSignedFamilies.Contains(packageFamilyName);
                if (!trustedSignature || !package.Id.FamilyName.Equals(packageFamilyName, StringComparison.OrdinalIgnoreCase)) continue;
                roots.Add(Path.GetFullPath(package.InstalledLocation.Path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Runtime.InteropServices.COMException) { }
        return roots;
    }
}
