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

        // NVIDIA's series list: 120 is the RTX 30 series, 131 the RTX 50 series. An RTX 5080
        // must get the driver list of its exact product in the RTX 50 series.
        // nvidia_driver_service_gives_the_newest_game_ready_driver: NVIDIA's old search page
        // stopped at 617.42 while its driver service listed 621.01; betas, Studio drivers
        // and other products must not count.
        var hash = new string('c', 64);
        var pages = new Dictionary<string, string>
        {
            ["TypeID=2"] =
                "<LookupValue><Name>GeForce RTX 50 Series</Name><Value>131</Value></LookupValue>" +
                "<LookupValue><Name>GeForce RTX 30 Series</Name><Value>120</Value></LookupValue>",
            ["TypeID=3&ParentID=131"] =
                "<LookupValue><Name>NVIDIA GeForce RTX 5090</Name><Value>1066</Value></LookupValue>" +
                "<LookupValue><Name>NVIDIA GeForce RTX 5080</Name><Value>1065</Value></LookupValue>",
            ["psid=120"] = Service(("278153", "616.56", "0", "0")),
            ["psid=131&pfid=1066"] = Service(("299999", "699.99", "0", "0")),
            ["psid=131&pfid=1065"] = Service(("282412", "621.01", "0", "0"), ("282500", "622.10", "1", "0"), ("282501", "622.20", "0", "1"), ("280103", "617.42", "0", "0")),
            ["/Windows/621.01/621.01-desktop-win10-win11-64bit-international-dch-whql.exe.sha256"] =
                $"{hash}  621.01-desktop-win10-win11-64bit-international-dch-whql.exe\n"
        };
        using var http = new HttpClient(new Respond(pages));
        var driver = new InstalledHardwareDriver("pci:nvidia", "NVIDIA GeForce RTX 5080", "Display", "NVIDIA", "NVIDIA", "32.0.16.1692", null, null, null);
        var result = await new ManufacturerDriverService(http).CheckNvidiaAsync(driver, CancellationToken.None);
        assert(result.Status == ManufacturerDriverStatus.Available && result.AvailableVersion == "621.01" &&
               result.ExecutableUpdate?.ExecutionPlan?.Sha256 == hash,
            $"An RTX 5080 was not checked against NVIDIA's newest Game Ready driver for its product: {result.Status} · {result.AvailableVersion} · {result.Message}");
        var current = await new ManufacturerDriverService(http).CheckNvidiaAsync(driver with { InstalledVersion = "32.0.16.2101" }, CancellationToken.None);
        assert(current is { Status: ManufacturerDriverStatus.Current, AvailableVersion: "621.01" },
            $"The installed 621.01 was not current against NVIDIA's 621.01: {current.Status} · {current.AvailableVersion}");
    }

    private static string Service(params (string Id, string Version, string Beta, string Studio)[] drivers) =>
        "{\"Success\":\"1\",\"IDS\":[" + string.Join(",", drivers.Select(static d =>
            $"{{\"downloadInfo\":{{\"ID\":\"{d.Id}\",\"Version\":\"{d.Version}\",\"IsWHQL\":\"1\",\"IsBeta\":\"{d.Beta}\",\"IsCRD\":\"{d.Studio}\",\"IsFeaturePreview\":\"0\",\"Name\":\"GeForce%20Game%20Ready%20Driver\"}}}}")) + "]}";

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
