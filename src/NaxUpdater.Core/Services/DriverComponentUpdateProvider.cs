using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

internal sealed class DriverComponentUpdateProvider(HttpClient client) : IUpdateProvider, IUpdateProviderSourceRefresher
{
    private readonly object _gate = new();
    private Task<ManufacturerDriverSnapshot>? _scan;
    public string Id => "driver-component";
    public UpdateProviderDescriptor Descriptor { get; } = new(UpdateProviderAuthority.ProducerRelease, 200,
        "Device-specific manufacturer driver checks; package versions are not compared with INF versions",
        [ManagementMode.Registry, ManagementMode.WindowsInstaller, ManagementMode.NativeSelfUpdater]);
    public bool CanHandle(InstalledApplication app) => app.Evidence.Any(e =>
        e.Label == ExternalManagementClassifier.OwnerEvidenceLabel && e.Value == "NaxUpdater manufacturer-driver view");
    public Task RefreshSourceAsync(CancellationToken token) { lock (_gate) _scan = null; return Task.CompletedTask; }
    public async Task<UpdateCheckResult> CheckAsync(InstalledApplication app, CancellationToken token)
    {
        Task<ManufacturerDriverSnapshot> scan;
        lock (_gate) scan = _scan ??= Task.Run(() => new ManufacturerDriverService(client).CheckAsync());
        var snapshot = await scan.WaitAsync(token);
        var matches = snapshot.Results.Where(r => Matches(app.DisplayName, r.Driver)).ToArray();
        var status = matches.Length == 0 ? UpdateStatus.ManagedExternally :
            matches.Any(r => r.Status == ManufacturerDriverStatus.Error) ? UpdateStatus.Error :
            matches.Any(r => r.Status == ManufacturerDriverStatus.Available) ? UpdateStatus.NewerReleaseKnown :
            matches.All(r => r.Status is ManufacturerDriverStatus.Current or ManufacturerDriverStatus.NoUpdateRequired) ? UpdateStatus.Current : UpdateStatus.ManagedExternally;
        // The driver version is shown only when it uses the package's own version
        // scheme (NVIDIA 616.92 -> 617.42); Intel and Realtek package and INF
        // versions are not comparable.
        var available = status == UpdateStatus.NewerReleaseKnown
            ? matches.Where(r => r.Status == ManufacturerDriverStatus.Available).Select(r => r.AvailableVersion)
                .Where(v => v is not null && app.NormalizedVersion is { } installed &&
                            v.Split('.').Length == installed.Split('.').Length && VersionOrder.Compare(v, installed) > 0)
                .OrderByDescending(v => v!, Comparer<string>.Create(VersionOrder.Compare)).FirstOrDefault()
            : null;
        return new(app.Identity, app.DisplayName, app.NormalizedVersion, available, status, Id, "Official manufacturer driver checks",
            "application-managed", "Driver package managed by manufacturer", "provider-selected", "stable", matches.FirstOrDefault()?.SourceUri?.AbsoluteUri,
            matches.Length == 0 ? "No installed device could be correlated with this driver package; the package was not marked current."
                : "The underlying device drivers were checked against manufacturer sources. Package and INF versions are not interchangeable. " +
                    string.Join(" | ", matches.Select(r => $"{r.Driver.DeviceName}: {r.Status}. {r.Message}")) +
                    (status == UpdateStatus.NewerReleaseKnown ? " Apply the device update in the Drivers view." : ""), null,
            Applicability: status == UpdateStatus.Current ? UpdateApplicability.NotRequired : UpdateApplicability.Unknown,
            AvailabilityReason: status == UpdateStatus.NewerReleaseKnown ? UpdateAvailabilityReason.DriverUpdateInDriversView : UpdateAvailabilityReason.None);
    }
    private static bool Matches(string app, InstalledHardwareDriver driver) => app switch
    {
        "Intel(R) Network Connections Drivers" => driver.DeviceClass == "Net" && driver.Provider.Contains("Intel", StringComparison.OrdinalIgnoreCase),
        "Realtek Ethernet Controller Driver" => driver.DeviceClass == "Net" && driver.Provider.Contains("Realtek", StringComparison.OrdinalIgnoreCase),
        "SetupChipset" => driver.Identity.EndsWith(":intel:chipset", StringComparison.OrdinalIgnoreCase),
        _ when app.StartsWith("TP-Link Archer TBE400UH", StringComparison.OrdinalIgnoreCase) => driver.DeviceName.Contains("TP-Link", StringComparison.OrdinalIgnoreCase),
        _ when app.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) && (app.Contains("HD-Audio", StringComparison.OrdinalIgnoreCase) || app.Contains("HD Audio", StringComparison.OrdinalIgnoreCase)) => driver.DeviceClass.Equals("MEDIA", StringComparison.OrdinalIgnoreCase) && driver.Provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase),
        _ when app.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) => driver.DeviceClass == "Display" && driver.Provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase),
        _ => false
    };
}
