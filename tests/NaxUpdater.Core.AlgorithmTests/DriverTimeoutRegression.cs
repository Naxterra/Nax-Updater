using System.Diagnostics;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

internal static class DriverTimeoutRegression
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        // One unresponsive manufacturer site fails only its own driver, quickly.
        using var http = new HttpClient(new Stalled()) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new ManufacturerDriverService(http) { RequestTimeout = TimeSpan.FromMilliseconds(200) };
        var driver = new InstalledHardwareDriver("pci:realtek", "Realtek PCIe 2.5GbE Family Controller", "Net", "Realtek", "Realtek", "10.80.50.407", null, null, null);
        var started = Stopwatch.StartNew();
        var result = await service.CheckRealtekEthernetAsync(driver, CancellationToken.None);
        assert(started.Elapsed < TimeSpan.FromSeconds(5) && result.Status == ManufacturerDriverStatus.Error &&
               result.Message?.Contains("did not respond") == true,
            "An unresponsive manufacturer site held its driver check past the per-request limit.");
    }

    private sealed class Stalled : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        }
    }
}
