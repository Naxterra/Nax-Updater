using NaxUpdater.Core.Internal;
using NaxUpdater.Core.Models;
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

                    var displayName = ReadDisplayName(package);
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
                        var installedPath = package.InstalledLocation?.Path;
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

    private static string ReadDisplayName(Package package)
    {
        try
        {
            var displayName = package.DisplayName;
            return string.IsNullOrWhiteSpace(displayName) || displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)
                ? package.Id.Name
                : displayName;
        }
        catch
        {
            return package.Id.Name;
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
