using System.Net;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

internal static class NvidiaSeriesRegression
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        assert(ManufacturerDriverService.NvidiaSeriesName("NVIDIA GeForce RTX 5080") == "GeForce RTX 50 Series" &&
               ManufacturerDriverService.NvidiaSeriesName("NVIDIA GeForce RTX 3060 Ti") == "GeForce RTX 30 Series" &&
               ManufacturerDriverService.NvidiaSeriesName("NVIDIA GeForce RTX 5070 Laptop GPU") is null &&
               ManufacturerDriverService.NvidiaSeriesName("NVIDIA GeForce GTX 1080") is null,
            "A GeForce card was mapped to the wrong NVIDIA product series.");

        // NVIDIA's series list: 120 is the RTX 30 series, 131 the RTX 50 series. Their
        // driver lists differ (616.56 vs 617.42); an RTX 5080 must get the RTX 50 list.
        var hash = new string('c', 64);
        var pages = new Dictionary<string, string>
        {
            ["/Download/API/lookupValueSearch.aspx"] =
                "<LookupValue><Name>GeForce RTX 50 Series</Name><Value>131</Value></LookupValue>" +
                "<LookupValue><Name>GeForce RTX 30 Series</Name><Value>120</Value></LookupValue>",
            ["psid=120"] = Results("278153", "616.56"),
            ["psid=131"] = Results("280103", "617.42"),
            ["/Windows/617.42/617.42-desktop-win10-win11-64bit-international-dch-whql.exe.sha256"] =
                $"{hash}  617.42-desktop-win10-win11-64bit-international-dch-whql.exe\n"
        };
        using var http = new HttpClient(new Respond(pages));
        var driver = new InstalledHardwareDriver("pci:nvidia", "NVIDIA GeForce RTX 5080", "Display", "NVIDIA", "NVIDIA", "32.0.16.1692", null, null, null);
        var result = await new ManufacturerDriverService(http).CheckNvidiaAsync(driver, CancellationToken.None);
        assert(result.Status == ManufacturerDriverStatus.Available && result.AvailableVersion == "617.42" &&
               result.ExecutableUpdate?.ExecutionPlan?.Sha256 == hash,
            "An RTX 5080 was not checked against NVIDIA's RTX 50 series driver list.");
    }

    private static string Results(string id, string version) =>
        $"<tr><td><a href='driverResults.aspx/{id}/en-us'>GeForce Game Ready Driver</a></td><td class=\"gridItem\">{version}</td></tr>";

    private sealed class Respond(Dictionary<string, string> pages) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            var body = pages.FirstOrDefault(p => uri.AbsolutePath == p.Key || uri.Query.Contains(p.Key, StringComparison.Ordinal)).Value;
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body) });
        }
    }
}
