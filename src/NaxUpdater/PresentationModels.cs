using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NaxUpdater.Core.Models;
using NaxUpdater.Services;

namespace NaxUpdater;

public sealed class ApplicationRow
{
    public ApplicationRow(InstalledApplication source)
    {
        Source = source;
    }

    public InstalledApplication Source { get; }
    public string Name => Source.DisplayName;
    public string Publisher => string.IsNullOrWhiteSpace(Source.Publisher) ? LocalizationService.Get("PublisherNotReported") : Source.Publisher;
    public string Version => string.IsNullOrWhiteSpace(Source.NormalizedVersion) ? LocalizationService.Get("Unknown") : Source.NormalizedVersion;
    public string VersionDetail => string.IsNullOrWhiteSpace(Source.InstalledVersion)
        ? LocalizationService.Get("NotReported")
        : string.Equals(Source.InstalledVersion, Source.NormalizedVersion, StringComparison.Ordinal)
            ? LocalizationService.Format("VersionWithSource", Source.InstalledVersion, LocalizeSource(Source.VersionSource))
            : LocalizationService.Format("VersionNormalized", Source.InstalledVersion, Source.NormalizedVersion, LocalizeSource(Source.VersionSource));
    public string Provider => Source.ManagementMode switch
    {
        ManagementMode.WindowsInstaller => LocalizationService.Get("ProviderWindowsInstaller"),
        ManagementMode.Msix => LocalizationService.Get("ProviderMsix"),
        ManagementMode.ZeroInstall => LocalizationService.Get("ProviderZeroInstallShort"),
        ManagementMode.NativeSelfUpdater => LocalizationService.Get("ProviderNativeShort"),
        ManagementMode.DirectVendor => LocalizationService.Get("ProviderDirectVendor"),
        ManagementMode.Registry => LocalizationService.Get("ProviderRegistry"),
        _ => LocalizationService.Get("ProviderUnmanaged")
    };
    public string InstallationDate => Source.InstalledOn?.ToLocalTime().ToString("d", System.Globalization.CultureInfo.CurrentCulture)
        ?? LocalizationService.Get("Unknown");
    public string InstallationDateDetail => Source.InstalledOn.HasValue
        ? LocalizationService.Format(
            "InstallDateWithSource",
            Source.InstalledOn.Value.ToLocalTime().ToString("D", System.Globalization.CultureInfo.CurrentCulture),
            LocalizeSource(Source.InstallDateSource))
        : LocalizationService.Get("InstallDateUnknown");
    public DateTimeOffset? InstallationDateSortValue => Source.InstalledOn;
    public bool HasInstallationDate => Source.InstalledOn.HasValue;
    public string Path => string.IsNullOrWhiteSpace(Source.PrimaryInstallPath) ? LocalizationService.Get("NotResolved") : Source.PrimaryInstallPath;
    public string PathDetail => string.IsNullOrWhiteSpace(Source.PrimaryInstallPath)
        ? LocalizationService.Get("NoVerifiedPath")
        : LocalizationService.Format("PathWithSource", Source.PrimaryInstallPath, LocalizeSource(Source.PathSource));
    public string Scope => Source.Scope switch
    {
        InstallScope.CurrentUser => LocalizationService.Get("ScopeCurrentUser"),
        InstallScope.Machine => LocalizationService.Get("ScopeMachine"),
        _ => LocalizationService.Get("ScopeUnknown")
    };
    public string BlockedProviders => Source.BlockedProviders.Count == 0
        ? LocalizationService.Get("None")
        : string.Join(", ", Source.BlockedProviders);
    public bool IsProtected => Source.BlockedProviders.Count > 0;
    public bool IsSystemComponent => Source.IsSystemComponent;

    private static string LocalizeSource(string? source)
    {
        if (source?.EndsWith(" executable", StringComparison.Ordinal) == true)
        {
            return LocalizationService.Format("SourceExecutableFormat", LocalizeSource(source[..^11]));
        }
        return source switch
        {
            "Native provider" => LocalizationService.Get("SourceNativeProvider"),
            "Executable metadata" => LocalizationService.Get("SourceExecutableMetadata"),
            "Uninstall registry" => LocalizationService.Get("SourceUninstallRegistry"),
            "Windows shortcut" => LocalizationService.Get("SourceWindowsShortcut"),
            "Registry display icon" => LocalizationService.Get("SourceRegistryDisplayIcon"),
            "Registry install location" => LocalizationService.Get("SourceRegistryInstallLocation"),
            "MSIX installed location" => LocalizationService.Get("SourceMsixLocation"),
            "MSIX package installed or updated date" => LocalizationService.Get("SourceMsixInstalledDate"),
            "Installation folder modified date" => LocalizationService.Get("SourceInstallFolderModifiedDate"),
            null => LocalizationService.Get("Unknown"),
            _ => source
        };
    }
}

public sealed class EvidenceRow
{
    public EvidenceRow(ApplicationEvidence source)
    {
        Kind = source.Kind switch
        {
            EvidenceKind.MsixPackage => LocalizationService.Get("EvidenceMsix"),
            EvidenceKind.ZeroInstall => LocalizationService.Get("EvidenceZeroInstall"),
            EvidenceKind.FileSystem => LocalizationService.Get("EvidenceFileSystem"),
            EvidenceKind.Registry => LocalizationService.Get("EvidenceRegistry"),
            EvidenceKind.Shortcut => LocalizationService.Get("EvidenceShortcut"),
            EvidenceKind.Executable => LocalizationService.Get("EvidenceExecutable"),
            EvidenceKind.Policy => LocalizationService.Get("EvidencePolicy"),
            _ => source.Kind.ToString()
        };
        Label = LocalizationService.EvidenceLabel(source.Label);
        Value = LocalizationService.EvidenceValue(source.Label, source.Value);
        Verification = source.Verified ? LocalizationService.Get("Verified") : LocalizationService.Get("Reported");
        AccentBrush = PresentationBrushes.Get(source.Kind switch
        {
            EvidenceKind.Registry => "NaxBlueBrush",
            EvidenceKind.MsixPackage => "NaxPurpleBrush",
            EvidenceKind.ZeroInstall => "NaxGreenBrush",
            EvidenceKind.Policy => "NaxPinkBrush",
            _ => "NaxOrangeBrush"
        });
    }

    public string Kind { get; }
    public string Label { get; }
    public string Value { get; }
    public string Verification { get; }
    public Brush AccentBrush { get; }
}

public sealed class PolicyRow
{
    public PolicyRow(ApplicationPolicy source)
    {
        Name = source.DisplayName;
        Id = source.Id;
        BlockedProviders = source.BlockedProviders.Count == 0
            ? LocalizationService.Get("NoBlockedProviders")
            : LocalizationService.Format("BlockedProvidersFormat", string.Join(", ", source.BlockedProviders));
        Reason = LocalizationService.PolicyReason(source.Id, source.Reason ?? LocalizationService.Get("PersistentPolicy"));
    }

    public string Name { get; }
    public string Id { get; }
    public string BlockedProviders { get; }
    public string Reason { get; }
}

public sealed class UpdateRow
{
    public UpdateRow(UpdateCheckResult source)
    {
        Source = source;
    }

    public UpdateCheckResult Source { get; }
    public string Name => Source.DisplayName;
    public string Installed => Source.InstalledVersion ?? "Unknown";
    public string Available => Source.AvailableVersion ?? "—";
    public string Language => LocalizationService.LanguageName(Source.Language);
    public string Provider => LocalizationService.ProviderName(Source);
    public string Status => Source.Status switch
    {
        UpdateStatus.Available => LocalizationService.Get("StatusUpdateAvailable"),
        UpdateStatus.Current => LocalizationService.Get("StatusCurrent"),
        UpdateStatus.ManagedExternally => LocalizationService.Get("StatusNativeUpdater"),
        UpdateStatus.Unsupported => LocalizationService.Get("StatusUnsupported"),
        UpdateStatus.Error => LocalizationService.Get("StatusCheckFailed"),
        _ => Source.Status.ToString()
    };
    public string VersionChange => Source.AvailableVersion is null
        ? LocalizationService.Format("InstalledFormat", Installed)
        : LocalizationService.Format("VersionChangeFormat", Installed, Available);
    public string LanguageDetail => LocalizationService.Format("LanguageDetailFormat", LocalizationService.LanguageName(Source.Language), LocalizationService.LanguageSource(Source.LanguageSource));
    public string PlatformDetail => LocalizationService.Format("PlatformDetailFormat", LocalizationService.PlatformValue(Source.Architecture), LocalizationService.PlatformValue(Source.Channel));
    public string SecurityDetail => Source.ExecutionPlan is null
        ? LocalizationService.Get("SecurityNoExternalInstaller")
        : Source.ExecutionPlan.Kind == UpdateExecutionKind.NativeCommand
            ? LocalizationService.Get("SecurityNativeProvider")
            : LocalizationService.Format(
                "SecurityHashSigner",
                Source.ExecutionPlan.Sha512 is null ? "SHA-256" : "SHA-512",
                Source.ExecutionPlan.ExpectedSigner);
    public string Message => LocalizationService.ProviderMessage(Source);
    public string ReleaseNotes => Source.ReleaseNotesUrl ?? LocalizationService.Get("NotProvided");
    public bool CanInstall => Source.IsInstallable;
    public string UpdateActionText => Source.ExecutionPlan?.Kind == UpdateExecutionKind.NativeCommand
        ? LocalizationService.Get("RunUpdateShort")
        : LocalizationService.Get("UpdateShort");
    public Visibility UpdateActionVisibility => CanInstall ? Visibility.Visible : Visibility.Collapsed;
    public Brush StatusForeground => PresentationBrushes.Get(Source.Status switch
    {
        UpdateStatus.Available => "NaxOrangeBrush",
        UpdateStatus.Current => "NaxGreenBrush",
        UpdateStatus.ManagedExternally => "NaxBlueBrush",
        UpdateStatus.Unsupported => "NaxPurpleBrush",
        _ => "NaxPinkBrush"
    });
    public Brush StatusBackground => PresentationBrushes.Get(Source.Status switch
    {
        UpdateStatus.Available => "NaxOrangeCardBrush",
        UpdateStatus.Current => "NaxGreenCardBrush",
        UpdateStatus.ManagedExternally => "NaxBlueCardBrush",
        UpdateStatus.Unsupported => "NaxPurpleCardBrush",
        _ => "NaxPinkCardBrush"
    });
}

internal static class PresentationBrushes
{
    public static Brush Get(string key) => (Brush)Application.Current.Resources[key];
}
