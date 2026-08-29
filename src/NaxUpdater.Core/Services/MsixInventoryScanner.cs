using NaxUpdater.Core.Internal;
using NaxUpdater.Core.Models;
using System.Text.RegularExpressions;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace NaxUpdater.Core.Services;

internal sealed class MsixInventoryScanner
{
    public IReadOnlyList<ApplicationCandidate> Scan(ICollection<InventoryIssue> issues)
    {
        var results = new List<ApplicationCandidate>();
        try
        {
            var packageManager = new PackageManager();
            foreach (var package in packageManager.FindPackagesForUser(string.Empty))
            {
                try
                {
                    if (package.IsFramework || package.IsResourcePackage)
                    {
                        continue;
                    }

                    var installedPath = ReadInstalledPath(package);
                    var displayName = ReadDisplayName(package, installedPath);
                    var publisher = ReadPublisher(package);
                    var version = FormatVersion(package.Id.Version);
                    var installedOn = ReadInstalledDate(package);
                    var isSystemComponent = package.SignatureKind == PackageSignatureKind.System;
                    var removalPlan = isSystemComponent
                        ? null
                        : new RemovalPlan(
                            RemovalKind.MsixPackage,
                            null,
                            null,
                            package.Id.FullName,
                            false);
                    var candidate = new ApplicationCandidate
                    {
                        Identity = $"msix:{package.Id.FamilyName}",
                        DisplayName = displayName,
                        Publisher = publisher,
                        ProviderVersion = version,
                        InstalledOn = installedOn,
                        InstallDateSource = installedOn.HasValue ? "MSIX package installed or updated date" : null,
                        Scope = InstallScope.CurrentUser,
                        ManagementMode = ManagementMode.Msix,
                        IsSystemComponent = isSystemComponent,
                        MsixPackageFamilyName = package.Id.FamilyName,
                        RemovalPlan = removalPlan
                    };
                    candidate.Evidence.Add(new ApplicationEvidence(
                        EvidenceKind.MsixPackage,
                        "MSIX package family",
                        package.Id.FamilyName,
                        true));
                    candidate.Evidence.Add(new ApplicationEvidence(
                        EvidenceKind.MsixPackage,
                        "MSIX package version",
                        version,
                        true));
                    candidate.Evidence.Add(new ApplicationEvidence(
                        EvidenceKind.MsixPackage,
                        "MSIX package architecture",
                        package.Id.Architecture.ToString().ToLowerInvariant(),
                        true));
                    if (installedOn.HasValue)
                    {
                        candidate.Evidence.Add(new ApplicationEvidence(
                            EvidenceKind.MsixPackage,
                            "Install date",
                            installedOn.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                            true));
                    }
                    if (removalPlan is not null)
                    {
                        candidate.Evidence.Add(new ApplicationEvidence(
                            EvidenceKind.MsixPackage,
                            "Removal method",
                            RemovalKind.MsixPackage.ToString(),
                            true));
                    }

                    try
                    {
                        if (!string.IsNullOrWhiteSpace(installedPath))
                        {
                            var manifestPath = Path.Combine(installedPath, "AppxManifest.xml");
                            candidate.MsixManifest = MsixManifestInspector.Inspect(manifestPath, installedPath);
                            candidate.IsMsixIntegrationPackage = candidate.MsixManifest.IsExternalIntegrationPackage;
                            if (candidate.IsMsixIntegrationPackage)
                            {
                                candidate.IsSystemComponent = true;
                                candidate.RemovalPlan = null;
                                candidate.Evidence.Add(new ApplicationEvidence(
                                    EvidenceKind.MsixPackage,
                                    "MSIX package role",
                                    "External application integration (manifest-only registration package)",
                                    true));
                                foreach (var executable in candidate.MsixManifest.DeclaredExecutables)
                                {
                                    candidate.Evidence.Add(new ApplicationEvidence(
                                        EvidenceKind.MsixPackage,
                                        "Externally registered executable",
                                        executable,
                                        true));
                                }
                                if (candidate.MsixManifest.ExtensionCategories.Count > 0)
                                {
                                    candidate.Evidence.Add(new ApplicationEvidence(
                                        EvidenceKind.MsixPackage,
                                        "Registered Windows extensions",
                                        string.Join(", ", candidate.MsixManifest.ExtensionCategories),
                                        true));
                                }
                            }
                            else
                            {
                                candidate.Paths.Add(new PathCandidate(installedPath, "MSIX installed location", 100, Directory.Exists(installedPath)));
                            }
                            candidate.Evidence.Add(new ApplicationEvidence(
                                EvidenceKind.MsixPackage,
                                "MSIX installed location",
                                installedPath,
                                Directory.Exists(installedPath)));
                        }
                    }
                    catch (Exception exception) when (exception is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                    {
                        candidate.Evidence.Add(new ApplicationEvidence(
                            EvidenceKind.MsixPackage,
                            "MSIX installed location",
                            $"Protected by Windows ({exception.GetType().Name})"));
                    }
                    results.Add(candidate);
                }
                catch (Exception exception)
                {
                    issues.Add(new InventoryIssue("MSIX packages", exception.Message, exception.GetType().Name));
                }
            }
        }
        catch (Exception exception)
        {
            issues.Add(new InventoryIssue("MSIX packages", exception.Message, exception.GetType().Name));
        }
        return results;
    }

    private static string ReadDisplayName(Package package, string? installedPath)
    {
        try
        {
            foreach (var entry in package.GetAppListEntriesAsync().AsTask().GetAwaiter().GetResult())
            {
                var displayName = entry.DisplayInfo.DisplayName;
                if (IsFriendlyDisplayName(displayName))
                {
                    return displayName.Trim();
                }
            }
        }
        catch
        {
            // Internal packages can intentionally omit app-list entries.
        }

        var manifestDisplayName = ReadManifestDisplayName(installedPath);
        if (IsFriendlyDisplayName(manifestDisplayName))
        {
            return manifestDisplayName!.Trim();
        }

        try
        {
            var displayName = package.DisplayName;
            if (IsFriendlyDisplayName(displayName))
            {
                return displayName.Trim();
            }
        }
        catch
        {
            // Continue through package-identity fallbacks below.
        }

        var identifier = package.Id.Name;
        if (!string.IsNullOrWhiteSpace(installedPath))
        {
            var directoryName = Path.GetFileName(installedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var publisherSuffix = $"_{package.Id.PublisherId}";
            if (directoryName.EndsWith(publisherSuffix, StringComparison.OrdinalIgnoreCase))
            {
                directoryName = directoryName[..^publisherSuffix.Length];
            }
            if (!string.IsNullOrWhiteSpace(directoryName) && !Guid.TryParse(directoryName, out _))
            {
                identifier = directoryName;
            }
        }
        return HumanizeIdentifier(identifier);
    }

    private static string? ReadManifestDisplayName(string? installedPath)
    {
        if (string.IsNullOrWhiteSpace(installedPath))
        {
            return null;
        }
        try
        {
            var manifestPath = Path.Combine(installedPath, "AppxManifest.xml");
            if (!File.Exists(manifestPath))
            {
                return null;
            }
            var document = System.Xml.Linq.XDocument.Load(manifestPath, System.Xml.Linq.LoadOptions.None);
            return document.Root?
                .Elements()
                .FirstOrDefault(static element => element.Name.LocalName == "Properties")?
                .Elements()
                .FirstOrDefault(static element => element.Name.LocalName == "DisplayName")?
                .Value;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    private static bool IsFriendlyDisplayName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) &&
        !Guid.TryParse(value, out _);

    internal static string HumanizeIdentifier(string value)
    {
        if (Guid.TryParse(value, out _))
        {
            return $"Windows component ({value})";
        }
        var separated = Regex.Replace(value, @"[._-]+", " ");
        separated = Regex.Replace(separated, @"([a-z0-9])([A-Z])", "$1 $2");
        separated = Regex.Replace(separated, @"([A-Z]+)([A-Z][a-z])", "$1 $2");
        return Regex.Replace(separated, @"\s+", " ").Trim();
    }

    private static string? ReadInstalledPath(Package package)
    {
        try
        {
            return package.InstalledLocation?.Path;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadPublisher(Package package)
    {
        try
        {
            return string.IsNullOrWhiteSpace(package.PublisherDisplayName)
                ? package.Id.Publisher
                : package.PublisherDisplayName;
        }
        catch
        {
            return package.Id.Publisher;
        }
    }

    private static string FormatVersion(PackageVersion version) => $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";

    private static DateTimeOffset? ReadInstalledDate(Package package)
    {
        try
        {
            var installedDate = package.InstalledDate;
            return installedDate.Year >= 2000 ? installedDate : null;
        }
        catch
        {
            return null;
        }
    }
}
