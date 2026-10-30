using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

// Kyocera's printer utilities (TWAIN driver, Status Monitor, Quick Scan, Wi-Fi Setup Tool)
// are listed on the support page of the printer model they belong to. The model comes from
// the installed KX printer driver; the utility is matched by its product name. Kyocera
// publishes no installer hashes, so a newer release is reported with its download page and
// not installed automatically.
public sealed class KyoceraUtilityUpdateProvider(KyoceraSupportCatalog catalog, Func<IReadOnlyList<string>>? installedModels = null) : IUpdateProvider
{
    private readonly Lazy<IReadOnlyList<string>> _models = new(() => installedModels?.Invoke() ??
        KyoceraSupportCatalog.InstalledKxDrivers().Select(static driver => driver.Model).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

    public string Id => "kyocera-support";
    public UpdateProviderDescriptor Descriptor { get; } = new(UpdateProviderAuthority.ProducerRelease, 100,
        "Kyocera's user support page for the installed printer model",
        [ManagementMode.Unmanaged, ManagementMode.Registry, ManagementMode.WindowsInstaller]);

    public bool CanHandle(InstalledApplication application) =>
        application.Publisher?.Contains("KYOCERA Document Solutions", StringComparison.OrdinalIgnoreCase) == true;

    public async Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        if (_models.Value.Count == 0)
            return Result(application, null, UpdateStatus.Unsupported, null,
                "No Kyocera KX printer driver is installed, so the printer model whose support page lists this application is unknown.");
        var key = KyoceraSupportCatalog.ProductKey(application.DisplayName);
        KyoceraModel? listedOn = null;
        foreach (var modelName in _models.Value)
        {
            var model = await catalog.FindModelAsync(modelName, cancellationToken);
            if (model is null) continue;
            listedOn ??= model;
            var download = model.Downloads
                .Where(item => item.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                               KyoceraSupportCatalog.ProductKey(item.Product) == key)
                .OrderByDescending(static item => item.Version, Comparer<string>.Create(VersionOrder.Compare))
                .FirstOrDefault();
            if (download is null) continue;
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
