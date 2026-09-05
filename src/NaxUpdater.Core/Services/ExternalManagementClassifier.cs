using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

internal static class ExternalManagementClassifier
{
    internal const string OwnerEvidenceLabel = "External update owner";
    internal const string SourceEvidenceLabel = "External update source";

    public static InstalledApplication Classify(InstalledApplication application)
    {
        if (application.IsSystemComponent ||
            application.ManagementMode is not (ManagementMode.Registry or ManagementMode.WindowsInstaller))
        {
            return application;
        }

        var owner = DetectOwner(application);
        if (owner is null)
        {
            return application;
        }

        var evidence = application.Evidence.ToList();
        evidence.Add(new ApplicationEvidence(EvidenceKind.Policy, OwnerEvidenceLabel, owner.Name, true));
        if (owner.Source is not null)
        {
            evidence.Add(new ApplicationEvidence(EvidenceKind.Policy, SourceEvidenceLabel, owner.Source.AbsoluteUri, true));
        }
        var blocked = application.BlockedProviders
            .Append("winget-fallback")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return application with
        {
            BlockedProviders = blocked,
            Evidence = evidence
        };
    }

    private static ExternalOwner? DetectOwner(InstalledApplication application)
    {
        if (application.Evidence.Any(static evidence =>
                evidence.Label == "Uninstall registry" &&
                RegistryKey(evidence.Value).StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase)))
        {
            return new("Steam", new Uri("https://store.steampowered.com/about/"));
        }

        var fileName = Path.GetFileName(application.PrimaryInstallPath);
        if (!string.IsNullOrWhiteSpace(fileName) && fileName.StartsWith("goggame-", StringComparison.OrdinalIgnoreCase))
        {
            return new("GOG Galaxy", new Uri("https://www.gog.com/galaxy"));
        }
        if (HasAncestorFile(application.PrimaryInstallPath, ".build.info", 4))
        {
            return new("Battle.net", new Uri("https://download.battle.net/"));
        }

        if (IsManufacturerDriverComponent(application))
        {
            return new("NaxUpdater manufacturer-driver view", null);
        }

        if (application.DisplayName.Equals("Mozilla Maintenance Service", StringComparison.OrdinalIgnoreCase))
        {
            return new("Mozilla Firefox updater", new Uri("https://www.mozilla.org/firefox/"));
        }
        if (application.DisplayName.Equals("Bitdefender Endpoint Security Tools", StringComparison.OrdinalIgnoreCase) &&
            application.Publisher?.Contains("Bitdefender", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new("Bitdefender GravityZone policy", new Uri("https://www.bitdefender.com/business/support/"));
        }
        if (application.DisplayName is "Razer Chroma" or "Razer Synapse")
        {
            return new("Razer Synapse", new Uri("https://www.razer.com/synapse-4"));
        }
        if (application.DisplayName.Equals("Macrium Reflect Home", StringComparison.OrdinalIgnoreCase) &&
            HasSiblingFile(application.PrimaryInstallPath, "ReflectUpdater.exe"))
        {
            return new("Macrium Reflect updater", new Uri("https://www.macrium.com/product-support"));
        }
        if (application.DisplayName.StartsWith("Fastmail Beta", StringComparison.OrdinalIgnoreCase) &&
            HasSiblingFile(application.PrimaryInstallPath, Path.Combine("resources", "app-update.yml")))
        {
            return new("Fastmail desktop updater", new Uri("https://www.fastmail.help/hc/en-us/categories/360000092174"));
        }
        if (application.DisplayName.Equals("UltraCompare", StringComparison.OrdinalIgnoreCase) &&
            HasSiblingFile(application.PrimaryInstallPath, "IDMUpdate.exe"))
        {
            return new("IDM application updater", new Uri("https://www.ultraedit.com/products/ultracompare/"));
        }
        return null;
    }

    private static bool IsManufacturerDriverComponent(InstalledApplication application)
    {
        var name = application.DisplayName;
        return name.Equals("Intel(R) Network Connections Drivers", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Realtek Ethernet Controller Driver", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("SetupChipset", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("TP-Link Archer TBE400UH Driver", StringComparison.OrdinalIgnoreCase) ||
               application.Publisher?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true &&
               (name.Contains("Grafiktreiber", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Graphics Driver", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("HD-Audiotreiber", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("HD Audio Driver", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasAncestorFile(string? path, string fileName, int levels)
    {
        try
        {
            var directory = string.IsNullOrWhiteSpace(path)
                ? null
                : Directory.Exists(path)
                    ? new DirectoryInfo(path)
                    : new FileInfo(path).Directory;
            for (var level = 0; directory is not null && level <= levels; level++, directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, fileName)))
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Classification remains optional when a path cannot be inspected.
        }
        return false;
    }

    private static bool HasSiblingFile(string? path, string relativePath)
    {
        try
        {
            var directory = string.IsNullOrWhiteSpace(path)
                ? null
                : Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            return !string.IsNullOrWhiteSpace(directory) && File.Exists(Path.Combine(directory, relativePath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static string RegistryKey(string value)
    {
        var separator = value.LastIndexOf(" · ", StringComparison.Ordinal);
        return separator >= 0 ? value[(separator + 3)..].Trim() : value.Trim();
    }

    private sealed record ExternalOwner(string Name, Uri? Source);
}
