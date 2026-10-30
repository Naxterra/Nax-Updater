using System.Net;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

// The Intel chipset and RST rows only showed Intel's package version and claimed nothing.
// Each scenario is one method.
internal static class IntelPackageRegression
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        await intel_chipset_package_is_compared_with_the_installed_utility(assert);
        intel_rst_package_is_offered_over_the_inbox_driver(assert);
    }

    private static async Task intel_chipset_package_is_compared_with_the_installed_utility(Action<bool, string> assert)
    {
        // Intel's page is not reachable here, so the last verified release 10.1.20658.8883 applies.
        using var http = new HttpClient(new NotFound());
        var service = new ManufacturerDriverService(http);
        var chipset = new InstalledHardwareDriver("group:intel:chipset", "Intel Z490 Chipset Device Software", "System", "Intel", "Intel", "10.1.18.1", null, null, null);
        var current = await service.CheckIntelChipsetPackageAsync(chipset, CancellationToken.None, "10.1.20658.8883");
        assert(current is { Status: ManufacturerDriverStatus.Current, Driver.InstalledVersion: "10.1.20658.8883" },
            $"The installed Chipset INF Utility equal to Intel's package was not current: {current.Status} · {current.Driver.InstalledVersion}");
        var older = await service.CheckIntelChipsetPackageAsync(chipset, CancellationToken.None, "10.1.18793.8276");
        assert(older is { Status: ManufacturerDriverStatus.Available, AvailableVersion: "10.1.20658.8883", ExecutableUpdate: null },
            $"An older Chipset INF Utility was not shown as a newer Intel package: {older.Status}");
    }

    private static void intel_rst_package_is_offered_over_the_inbox_driver(Action<bool, string> assert)
    {
        var inbox = new InstalledHardwareDriver("group:intel:rst", "Intel Rapid Storage Technology", "SCSIAdapter", "Intel", "Intel Corporation", "15.44.0.1015", null, null, "iastorav.inf");
        var offer = ManufacturerDriverService.CheckIntelRst(inbox);
        assert(offer is { Status: ManufacturerDriverStatus.Available, AvailableVersion: "18.7.6.1010.3", ExecutableUpdate: null },
            $"Intel's RST 18.7.6 was not offered over the Windows inbox driver 15.44: {offer.Status}");
        var installed = ManufacturerDriverService.CheckIntelRst(inbox with { InstalledVersion = "18.7.6.1010" });
        assert(installed.Status == ManufacturerDriverStatus.Current, "The installed RST 18.7.6 driver was not current.");
    }

    private sealed class NotFound : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
    }
}
