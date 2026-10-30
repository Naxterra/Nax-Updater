using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

// Kyocera's printer utilities (TWAIN driver, Status Monitor, Quick Scan, Wi-Fi Setup Tool)
// are listed on the support page of the printer model they belong to. The model comes from
// the installed KX printer driver; the utility is matched by its product name. Kyocera
// publishes no installer hashes, so a newer release is reported with its download page and
// not installed automatically.
public sealed class KyoceraUtilityUpdateProvider(KyoceraSupportCatalog catalog, Func<IReadOnlyList<string>>? installedModels = null) : IUpdateProvider
{
    // The printer models, or why they could not be read (a stopped print spooler must not
    // read as "no Kyocera printer").
    private readonly Lazy<(IReadOnlyList<string> Models, string? Error)> _models = new(() =>
    {
        if (installedModels is not null) return (installedModels(), null);
        var issues = new List<string>();
        var models = KyoceraSupportCatalog.InstalledKxDrivers(issues).Select(static driver => driver.Model).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return (models, models.Length == 0 && issues.Count > 0 ? string.Join(" ", issues) : null);
    });

    public string Id => "kyocera-support";
    public UpdateProviderDescriptor Descriptor { get; } = new(UpdateProviderAuthority.ProducerRelease, 100,
        "Kyocera's user support page for the installed printer model",
        [ManagementMode.Unmanaged, ManagementMode.Registry, ManagementMode.WindowsInstaller]);

    public bool CanHandle(InstalledApplication application) =>
        application.Publisher?.Contains("KYOCERA Document Solutions", StringComparison.OrdinalIgnoreCase) == true;

    public async Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        var (models, readError) = _models.Value;
        if (readError is not null)
            return Result(application, null, UpdateStatus.Error, null, $"The installed printer drivers could not be read: {readError}");
        if (models.Count == 0)
            return Result(application, null, UpdateStatus.Unsupported, null,
                "No Kyocera KX printer driver is installed, so the printer model whose support page lists this application is unknown.");
        var key = KyoceraSupportCatalog.ProductKey(application.DisplayName);
        KyoceraModel? listedOn = null;
        (KyoceraModel Model, KyoceraDownload Download)? newest = null;
        // Every installed model counts, so one model's older listing cannot hide a newer
        // release that another model's page carries.
        foreach (var modelName in models)
        {
            var model = await catalog.FindModelAsync(modelName, cancellationToken);
            if (model is null) continue;
            listedOn ??= model;
            foreach (var download in model.Downloads.Where(item => item.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                                                                   KyoceraSupportCatalog.ProductKey(item.Product) == key))
                if (newest is null || VersionOrder.Compare(download.Version, newest.Value.Download.Version) > 0)
                    newest = (model, download);
        }
        if (newest is { } found)
        {
            var (model, download) = found;
            var comparison = VersionOrder.Compare(download.Version, application.NormalizedVersion);
            return comparison > 0
                ? Result(application, model, UpdateStatus.NewerReleaseKnown, download.Version,
                    $"Kyocera's support page for the {model.Name} lists {download.Version}. Kyocera publishes no installer hash, so the update is not installed automatically; open the support page to download it.")
                : Result(application, model, UpdateStatus.Current, download.Version, comparison < 0
                    ? $"Kyocera's support page for the {model.Name} lists {download.Version}; the installed version is newer."
                    : $"Kyocera's support page for the {model.Name} lists {download.Version} as the newest release; the installed version is current.");
        }
        return listedOn is null
            ? Result(application, null, UpdateStatus.Unsupported, null,
                "Kyocera's support site does not list the installed printer model.")
            : Result(application, listedOn, UpdateStatus.Unsupported, null,
                $"Kyocera's support page for the {listedOn.Name} does not list this application.");
    }

    private UpdateCheckResult Result(InstalledApplication application, KyoceraModel? model, UpdateStatus status, string? available, string message) =>
        new(application.Identity, application.DisplayName, application.NormalizedVersion, status == UpdateStatus.NewerReleaseKnown ? available : null,
            status, Id, model is null ? "Kyocera support page" : $"Kyocera support page · {model.Name}",
            "application-managed", "Preserved by the application's updater", InstalledApplicationMetadata.Architecture(application) ?? "unknown", "stable",
            model?.SupportPage.AbsoluteUri, message, null,
            Applicability: status == UpdateStatus.NewerReleaseKnown ? UpdateApplicability.NotApplicable : UpdateApplicability.NotRequired);
}
