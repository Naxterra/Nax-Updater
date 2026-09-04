using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NaxUpdater.Core.Models;
using NaxUpdater.Services;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NaxUpdater;

public sealed class ApplicationRow : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public ApplicationRow(InstalledApplication source)
    {
        Source = source;
    }

    public InstalledApplication Source { get; }
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            OnPropertyChanged();
        }
    }
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
        : string.Join(", ", Source.BlockedProviders.Select(LocalizationService.ProviderPolicyName));
    public bool IsProtected => Source.BlockedProviders.Count > 0;
    public bool IsSystemComponent => Source.IsSystemComponent;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

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
            : LocalizationService.Format("BlockedProvidersFormat", string.Join(", ", source.BlockedProviders.Select(LocalizationService.ProviderPolicyName)));
        Reason = LocalizationService.PolicyReason(source.Id, source.Reason ?? LocalizationService.Get("PersistentPolicy"));
    }

    public string Name { get; }
    public string Id { get; }
    public string BlockedProviders { get; }
    public string Reason { get; }
}

public sealed class UpdateRow
{
    public UpdateRow(UpdateCheckResult source, ApplicationRow? application = null)
    {
        Source = source;
        Application = application;
    }

    public UpdateCheckResult Source { get; }
    public ApplicationRow? Application { get; }
    public string Name => Source.DisplayName;
    public string Installed => Source.InstalledVersion ?? "Unknown";
    public string Available => Source.AvailableVersion ?? "—";
    public string Language => LocalizationService.LanguageName(Source.Language);
    public string Provider => LocalizationService.ProviderName(Source);
    public string Status => Source.Status switch
    {
        UpdateStatus.Available => LocalizationService.Get("StatusUpdateAvailable"),
        UpdateStatus.NewerReleaseKnown when Source.AvailabilityReason == UpdateAvailabilityReason.AwaitingStorePublication =>
            LocalizationService.Get("StatusStorePublicationPending"),
        UpdateStatus.NewerReleaseKnown => LocalizationService.Get("StatusNewerReleaseKnown"),
        UpdateStatus.Current => LocalizationService.Get("StatusCurrent"),
        UpdateStatus.ManagedExternally when Source.ProviderId == "msix-store" => LocalizationService.Get("StatusStoreManaged"),
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
        : Source.ExecutionPlan.Kind == UpdateExecutionKind.StorePackage
            ? LocalizationService.Get("SecurityMicrosoftStoreIdentity")
        : Source.ExecutionPlan.Kind == UpdateExecutionKind.WingetPackage
            ? LocalizationService.Get("SecurityWingetProvider")
        : Source.ExecutionPlan.Kind == UpdateExecutionKind.NativeCommand
            ? LocalizationService.Get("SecurityNativeProvider")
            : !Source.ExecutionPlan.RequireAuthenticode
                ? LocalizationService.Get("SecurityHashOnly")
                : LocalizationService.Format(
                    "SecurityHashSigner",
                    Source.ExecutionPlan.Sha512 is null ? "SHA-256" : "SHA-512",
                    Source.ExecutionPlan.ExpectedSigners is { Count: > 0 }
                        ? string.Join(" / ", Source.ExecutionPlan.ExpectedSigners)
                        : Source.ExecutionPlan.ExpectedSigner);
    public string Message => LocalizationService.ProviderMessage(Source);
    public string ReleaseNotes => Source.ReleaseNotesUrl ?? LocalizationService.Get("NotProvided");
    public bool CanInstall => Source.IsInstallable;
    public string UpdateActionText => Source.ProviderId == "gog-galaxy-native"
        ? LocalizationService.Get("UpdateShort")
        : Source.ExecutionPlan?.Kind == UpdateExecutionKind.NativeCommand
        ? LocalizationService.Get("RunUpdateShort")
        : LocalizationService.Get("UpdateShort");
    public Visibility UpdateActionVisibility => CanInstall ? Visibility.Visible : Visibility.Collapsed;
    public Brush StatusForeground => PresentationBrushes.Get(Source.Status switch
    {
        UpdateStatus.Available => "NaxOrangeBrush",
        UpdateStatus.NewerReleaseKnown => "NaxBlueBrush",
        UpdateStatus.Current => "NaxGreenBrush",
        UpdateStatus.ManagedExternally => "NaxBlueBrush",
        UpdateStatus.Unsupported => "NaxPurpleBrush",
        _ => "NaxPinkBrush"
    });
    public Brush StatusBackground => PresentationBrushes.Get(Source.Status switch
    {
        UpdateStatus.Available => "NaxOrangeCardBrush",
        UpdateStatus.NewerReleaseKnown => "NaxBlueCardBrush",
        UpdateStatus.Current => "NaxGreenCardBrush",
        UpdateStatus.ManagedExternally => "NaxBlueCardBrush",
        UpdateStatus.Unsupported => "NaxPurpleCardBrush",
        _ => "NaxPinkCardBrush"
    });
}

public sealed class DriverColumnLayout : INotifyPropertyChanged
{
    private GridLength _nameWidth = new(300);
    private GridLength _installedWidth = new(125);
    private GridLength _availableWidth = new(175);
    private GridLength _sourceWidth = new(200);
    private GridLength _statusWidth = new(240);
    private GridLength _actionWidth = new(165);

    public GridLength NameWidth { get => _nameWidth; private set => Set(ref _nameWidth, value); }
    public GridLength InstalledWidth { get => _installedWidth; private set => Set(ref _installedWidth, value); }
    public GridLength AvailableWidth { get => _availableWidth; private set => Set(ref _availableWidth, value); }
    public GridLength SourceWidth { get => _sourceWidth; private set => Set(ref _sourceWidth, value); }
    public GridLength StatusWidth { get => _statusWidth; private set => Set(ref _statusWidth, value); }
    public GridLength ActionWidth { get => _actionWidth; private set => Set(ref _actionWidth, value); }

    public void Synchronize(double name, double installed, double available, double source, double status, double action)
    {
        NameWidth = new GridLength(name);
        InstalledWidth = new GridLength(installed);
        AvailableWidth = new GridLength(available);
        SourceWidth = new GridLength(source);
        StatusWidth = new GridLength(status);
        ActionWidth = new GridLength(action);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref GridLength field, GridLength value, [CallerMemberName] string? propertyName = null)
    {
        if (field.Equals(value))
        {
            return;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class ManufacturerDriverRow(ManufacturerDriverResult source, DriverColumnLayout layout)
{
    public ManufacturerDriverResult Source { get; } = source;
    public DriverColumnLayout Layout { get; } = layout;
    public string Name => Source.Driver.DeviceName;
    private bool IsRazerSuite => Source.Driver.Identity.EndsWith(":razer:suite", StringComparison.OrdinalIgnoreCase);
    private bool IsIntelPackageGroup => Source.Driver.Identity.EndsWith(":intel:chipset", StringComparison.OrdinalIgnoreCase) ||
                                        Source.Driver.Identity.EndsWith(":intel:management-engine", StringComparison.OrdinalIgnoreCase) ||
                                        Source.Driver.Identity.EndsWith(":intel:rst", StringComparison.OrdinalIgnoreCase);
    public string Detail => IsRazerSuite
        ? LocalizationService.Format("DriverGroupedProducts", Source.Driver.DeviceCount)
        : IsIntelPackageGroup
            ? LocalizationService.Format("DriverGroupedComponents", Source.Driver.DeviceCount)
            : string.Join(" · ", new[]
            {
                Source.Driver.DeviceClass,
                Source.Driver.Provider,
                Source.Driver.DeviceCount > 1 ? LocalizationService.Format("DriverDeviceCount", Source.Driver.DeviceCount) : null,
                !Source.Driver.IsPresent ? LocalizationService.Get("DriverNotConnected") : null
            }.Where(static value => !string.IsNullOrWhiteSpace(value)));
    public string Installed => IsRazerSuite
        ? LocalizationService.Format("DriverInstalledSynapse", Source.Driver.InstalledVersion)
        : IsIntelPackageGroup
            ? LocalizationService.Format("DriverInstalledComponentSet", Source.Driver.DeviceCount)
            : Source.Driver.InstalledVersion;
    public string Available => IsRazerSuite
        ? LocalizationService.Format("DriverFirmwareChecksAvailable", Source.AvailableVersion ?? "0")
        : Source.Status == ManufacturerDriverStatus.NoUpdateRequired && Source.SourceName.StartsWith("WD Elements", StringComparison.OrdinalIgnoreCase)
            ? LocalizationService.Get("DriverWdOptionsAvailable")
            : Source.AvailableVersion ?? "—";
    public string Provider => Source.SourceName;
    public string Status => Source.Status switch
    {
        ManufacturerDriverStatus.Available when CanUpdate => LocalizationService.Get("DriverStatusAvailable"),
        ManufacturerDriverStatus.Available => LocalizationService.Get("DriverStatusManufacturerPackage"),
        ManufacturerDriverStatus.Current => LocalizationService.Get("DriverStatusCurrent"),
        ManufacturerDriverStatus.VendorSoftwareManaged => LocalizationService.Get("DriverStatusSynapseChecked"),
        ManufacturerDriverStatus.OfficialSourceOnly when Source.AvailableVersion is not null => LocalizationService.Get("DriverStatusOfficialPackageChecked"),
        ManufacturerDriverStatus.OfficialSourceOnly => LocalizationService.Get("DriverStatusSupportPageOnly"),
        ManufacturerDriverStatus.NoUpdateRequired when Source.SourceName.StartsWith("WD Elements", StringComparison.OrdinalIgnoreCase) => LocalizationService.Get("DriverStatusWdCurrent"),
        ManufacturerDriverStatus.NoUpdateRequired => LocalizationService.Get("DriverStatusNoUpdateRequired"),
        ManufacturerDriverStatus.NoVerifiedCatalog => LocalizationService.Get("DriverStatusNoCatalog"),
        _ => LocalizationService.Get("DriverStatusError")
    };
    public bool CanUpdate => Source.ExecutableUpdate?.IsInstallable == true;
    public bool CanOpenSource => Source.SourceUri is not null &&
        Source.Status is ManufacturerDriverStatus.Available or ManufacturerDriverStatus.Current or ManufacturerDriverStatus.NoUpdateRequired or
            ManufacturerDriverStatus.VendorSoftwareManaged or ManufacturerDriverStatus.OfficialSourceOnly;
    public bool HasAction => CanUpdate || CanOpenSource;
    public Visibility ActionVisibility => HasAction ? Visibility.Visible : Visibility.Collapsed;
    public string ActionText => CanUpdate
        ? LocalizationService.Get("UpdateShort")
        : Source.Status == ManufacturerDriverStatus.Available
            ? LocalizationService.Get("OpenExactDownload")
            : Source.Status == ManufacturerDriverStatus.VendorSoftwareManaged
                ? LocalizationService.Get("OpenRazerChecks")
                : Source.Status == ManufacturerDriverStatus.NoUpdateRequired && Source.SourceName.StartsWith("WD Elements", StringComparison.OrdinalIgnoreCase)
                    ? LocalizationService.Get("OpenWdSupport")
                    : Source.Status == ManufacturerDriverStatus.OfficialSourceOnly && Source.AvailableVersion is not null
                        ? LocalizationService.Get("OpenOfficialPackage")
                        : LocalizationService.Get("OpenSupportPage");
    public Brush StatusForeground => PresentationBrushes.Get(Source.Status switch
    {
        ManufacturerDriverStatus.Available => "NaxOrangeBrush",
        ManufacturerDriverStatus.Current => "NaxGreenBrush",
        ManufacturerDriverStatus.NoUpdateRequired => "NaxGreenBrush",
        ManufacturerDriverStatus.VendorSoftwareManaged => "NaxBlueBrush",
        ManufacturerDriverStatus.OfficialSourceOnly => "NaxBlueBrush",
        ManufacturerDriverStatus.NoVerifiedCatalog => "NaxPurpleBrush",
        _ => "NaxPinkBrush"
    });
    public Brush StatusBackground => PresentationBrushes.Get(Source.Status switch
    {
        ManufacturerDriverStatus.Available => "NaxOrangeCardBrush",
        ManufacturerDriverStatus.Current => "NaxGreenCardBrush",
        ManufacturerDriverStatus.NoUpdateRequired => "NaxGreenCardBrush",
        ManufacturerDriverStatus.VendorSoftwareManaged => "NaxBlueCardBrush",
        ManufacturerDriverStatus.OfficialSourceOnly => "NaxBlueCardBrush",
        ManufacturerDriverStatus.NoVerifiedCatalog => "NaxPurpleCardBrush",
        _ => "NaxPinkCardBrush"
    });
}

internal static class PresentationBrushes
{
    public static Brush Get(string key) => (Brush)Application.Current.Resources[key];
}
