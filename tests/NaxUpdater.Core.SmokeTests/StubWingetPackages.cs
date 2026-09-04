using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

internal sealed class StubWingetPackages : IWingetPackageService
{
    public Task<WingetPackageOffer> AssessAsync(InstalledApplication application, string id, string version, CancellationToken token) =>
        Task.FromResult(new WingetPackageOffer(new WingetUpdateTarget(
            id, WingetPackageService.OfficialSourceId, version, application.NormalizedVersion!,
            ["{11111111-2222-3333-4444-555555555555}"], "X64", "Msi", "",
            application.Scope, Path.GetDirectoryName(application.PrimaryInstallPath)), null));
    public Task<PreparedCatalogUpdate> PrepareAsync(UpdateCheckResult update, CancellationToken token) =>
        throw new InvalidOperationException("The smoke check must not install packages.");
}
