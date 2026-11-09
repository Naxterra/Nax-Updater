using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

internal static class DriverSourceRegression
{
    internal static string IntelPage(string id, string version) =>
        $"<meta content='https://www.intel.cn/content/www/cn/zh/download/{id}/fixture.html' property='og:url'><meta content='{version}' name='DownloadVersion'>";
    private static InstalledHardwareDriver Driver(string hardware = @"PCI\VEN_8086&DEV_15BC") =>
        new("fixture", "Fixture", "System", "Intel", "Intel", "12.19.2.63", null, hardware, null);
    private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html) };

    public static async Task RunAsync(Action<bool, string> assert, string fixture)
    {
        await IntelFallbackAsync(assert);
        await EthernetArchiveAsync(assert, fixture);
        await BrowserAndMsiAsync(assert);
        await DellAndRazerAsync(assert);
        BundledMetadata(assert);
    }

    private static async Task IntelFallbackAsync(Action<bool, string> assert)
    {
        var requests = new List<Uri>();
        using var http = new HttpClient(new Handler((r, _) =>
        {
            requests.Add(r.RequestUri!);
            return Task.FromResult(r.RequestUri!.Host == "www.intel.com" ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Html(IntelPage("19347", "10.1.20658.8883")));
        }));
        var current = await new ManufacturerDriverService(http).CheckIntelChipsetPackageAsync(Driver(), default, "10.1.20658.8883");
        assert(current.IsLiveChecked && current.Status == ManufacturerDriverStatus.Current && requests.Select(r => r.Host).SequenceEqual(["www.intel.com", "www.intel.cn"]),
            "A blocked Intel page did not use and validate Intel's official alternate-language release.");
        assert(ManufacturerDriverService.IntelReleaseVersion(IntelPage("682431", "2618.9.30.0"), "19347") is null &&
               ManufacturerDriverService.IntelReleaseVersion("Verify your browser 10.1.20658.8883", "19347") is null,
            "An unrelated Intel download or challenge page established the requested release.");
        using var mismatch = new HttpClient(new Handler((_, _) => Task.FromResult(Html(IntelPage("99999", "99.0.0.0")))));
        var denied = await new ManufacturerDriverService(mismatch).CheckIntelChipsetPackageAsync(Driver(), default, "1.0.0.0");
        assert(denied.HasCheckIssue && !denied.IsLiveChecked && denied.ExecutableUpdate is null, "Wrong-product fallback metadata created a driver offer.");
        using var blocked = new HttpClient(new Handler((request, _) =>
            request.RequestUri!.Host == "www.intel.cn"
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("Certificate chain rejected"))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));
        var browserSources = new List<Uri>();
        var browserService = new ManufacturerDriverService(blocked)
        {
            BrowserReader = (uri, _) => { browserSources.Add(uri); return Task.FromResult(IntelPage("19347", "10.1.20658.8883")); }
        };
        var browserCurrent = await browserService.CheckIntelChipsetPackageAsync(Driver(), default, "10.1.20658.8883");
        assert(browserCurrent.IsLiveChecked && browserCurrent.Status == ManufacturerDriverStatus.Current &&
            browserSources.Count == 1 && browserSources[0].Host == "www.intel.com" && ManufacturerSourceQuery.IsAllowed(browserSources[0]) &&
            !ManufacturerSourceQuery.IsAllowed(new Uri(browserSources[0].AbsoluteUri + "?other=1")) &&
            !ManufacturerSourceQuery.IsAllowed(new Uri("https://www.intel.com/content/www/us/en/download/99999/other.html")),
            "A blocked regional TLS connection did not fall back to the exact allowlisted primary catalog.");
        var wrongBrowser = new ManufacturerDriverService(blocked) { BrowserReader = (_, _) => Task.FromResult(IntelPage("99999", "99.0.0.0")) };
        assert((await wrongBrowser.CheckIntelChipsetPackageAsync(Driver(), default, "1.0.0.0")).HasCheckIssue,
            "An unrelated browser response overrode the exact Intel download identity.");
        using var me = new HttpClient(new Handler((_, _) => Task.FromResult(Html(IntelPage("682431", "2618.9.30.0")))));
        var components = await new ManufacturerDriverService(me).CheckIntelManagementEngineAsync(Driver(), default);
        assert(components.IsLiveChecked && components.Status == ManufacturerDriverStatus.OfficialSourceOnly && components.ExecutableUpdate is null,
            "A live ME umbrella version was compared with component INF versions.");
        using var future = new HttpClient(new Handler((_, _) => Task.FromResult(Html(IntelPage("19512", "19.9.9.9")))));
        var unknown = await new ManufacturerDriverService(future).CheckIntelRstAsync(Driver(@"PCI\VEN_8086&DEV_06D6"), default);
        assert(unknown.IsLiveChecked && unknown.Status == ManufacturerDriverStatus.OfficialSourceOnly && unknown.ExecutableUpdate is null,
            "An unmapped RST release inferred storage-controller applicability or an installer.");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        try { await new ManufacturerDriverService(http).CheckIntelManagementEngineAsync(Driver(), cancel.Token); assert(false, "A canceled Intel check continued."); }
        catch (OperationCanceledException) { assert(true, "Intel cancellation propagated."); }
    }

    private static async Task EthernetArchiveAsync(Action<bool, string> assert, string fixture)
    {
        static byte[] Archive(string device = @"PCI\VEN_8086&DEV_15BC")
        {
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            using (var writer = new StreamWriter(zip.CreateEntry("PRO1000/Winx64/W11/e1d.inf").Open()))
                writer.Write($"[Version]\nDriverVer=07/10/2026,12.19.2.64\n[Models]\nDevice=Install,{device}\n");
            return memory.ToArray();
        }
        var bytes = Archive();
        var sha512 = Convert.ToHexString(SHA512.HashData(bytes));
        var downloads = 0;
        var pageReads = 0;
        string Page(string hash) => IntelPage("727998", "31.3") + $"<a href='https://downloadmirror.intel.com/931390/Wired_driver_31.3_x64.zip'>Download</a><li>SHA512：{hash}</li>";
        using var http = new HttpClient(new Handler((r, _) =>
        {
            if (r.RequestUri!.AbsolutePath.EndsWith(".zip")) { downloads++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); }
            pageReads++;
            return Task.FromResult(Html(Page(sha512)));
        }));
        var cache = Path.Combine(fixture, "intel-archive");
        var service = new ManufacturerDriverService(http, kyoceraCacheDirectory: cache);
        var offer = await service.CheckIntelI219Async(Driver(), default);
        assert(offer.IsLiveChecked && offer.Status == ManufacturerDriverStatus.Available && offer.AvailableVersion == "Intel 31.3 · e1d.inf 12.19.2.64" &&
               offer.ExecutableUpdate?.ExecutionPlan is { ExpectedHardwareId: @"PCI\VEN_8086&DEV_15BC", NestedInstallerRelativePath: @"PRO1000\Winx64\W11\e1d.inf" } plan &&
               plan.Sha256 == Convert.ToHexString(SHA256.HashData(bytes)),
            "The current producer-verified archive did not produce the exact applicable INF plan.");
        var current = await service.CheckIntelI219Async(Driver() with { InstalledVersion = "12.19.2.64" }, default);
        assert(current.IsLiveChecked && current.Status == ManufacturerDriverStatus.Current && downloads == 1 && pageReads == 2,
            "Ethernet checks either reused a stale catalog or downloaded the unchanged verified archive again.");
        var cachedZip = Directory.GetFiles(Path.Combine(cache, "IntelInf"), "*.zip").Single();
        File.WriteAllText(cachedZip, "tampered archive");
        var repaired = await service.CheckIntelI219Async(Driver(), default);
        assert(repaired.Status == ManufacturerDriverStatus.Available && downloads == 2, "A corrupt cached archive was trusted without checking the producer's hash.");
        assert(ManufacturerDriverService.IntelI219Inf("DriverVer=1/1/2026,12.19.2.65\nPCI\\VEN_8086&DEV_15BD", Driver().HardwareId) is null,
            "An INF for another Ethernet device supplied the installed device's version.");
        using var badHash = new HttpClient(new Handler((r, _) => Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith(".zip")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } : Html(Page(new string('F', 128))))));
        var invalid = await new ManufacturerDriverService(badHash, kyoceraCacheDirectory: Path.Combine(fixture, "bad-archive")).CheckIntelI219Async(Driver(), default);
        assert(invalid.HasCheckIssue && invalid.ExecutableUpdate is null && !invalid.IsLiveChecked, "A producer hash mismatch became a current result or executable offer.");
    }

    private static string MsiPage(string title = "MAG Z490 TOMAHAWK", string os = "Win11 64") => JsonSerializer.Serialize(new
    {
        status = new { code = 200 }, result = new { title, category = "mb", downloads = new Dictionary<string, object>
        { ["Audio"] = new[] { new { download_title = "Realtek HD Universal Driver", download_version = "6.0.10007.1", os,
            download_sha256 = ManufacturerDriverService.MsiAudioSha256, download_url = "https://download.msi.com/dvr_exe/mb/realtek_audio_R.zip" } } } }
    });

    private static async Task BrowserAndMsiAsync(Action<bool, string> assert)
    {
        assert(ManufacturerSourceQuery.IsAllowed(ManufacturerSourceQuery.MsiDrivers) &&
               !ManufacturerSourceQuery.IsAllowed(new Uri("https://www.msi.com/other")) &&
               !ManufacturerSourceQuery.IsAllowed(new Uri("http://www.msi.com/api/v1/product/support/panel")) &&
               !ManufacturerSourceQuery.IsAllowed(new Uri("https://www.msi.com.evil.test/")), "Browser queries were not restricted to exact public manufacturer sources.");
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Html("<html>Please verify your browser</html>"))));
        var calls = 0;
        var service = new ManufacturerDriverService(http) { BrowserReader = async (_, token) => { Interlocked.Increment(ref calls); await Task.Delay(10, token); return "<pre>" + WebUtility.HtmlEncode(MsiPage()) + "</pre>"; } };
        var audio = Driver(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0B00&SUBSYS_1462EC80") with { DeviceClass = "MEDIA", Provider = "Microsoft", InstalledVersion = "10.0.26100.9818" };
        var rows = await Task.WhenAll(service.CheckMsiBoardDriverAsync(audio, default, true), service.CheckMsiBoardDriverAsync(Driver(), default, true));
        assert(calls == 1 && rows[0].IsLiveChecked && rows[0].Status == ManufacturerDriverStatus.Available && rows[0].ExecutableUpdate?.IsInstallable == true,
            "MSI rows did not share one browser request or the inbox/OEM version schemes were incorrectly compared.");
        assert(rows[1].IsLiveChecked && rows[1].Status == ManufacturerDriverStatus.OfficialSourceOnly,
            "A successful catalog without an exact component package claimed that component was current.");
        var unrelated = await service.CheckMsiBoardDriverAsync(audio, default, false);
        assert(!unrelated.IsLiveChecked && unrelated.Status != ManufacturerDriverStatus.Available && calls == 1, "The motherboard package was offered without the exact board identity.");
        foreach (var invalid in new[] { MsiPage("OTHER BOARD"), MsiPage(os: "Win10 64"), "<pre>challenge</pre>" })
        {
            var rejected = false;
            try { ManufacturerDriverService.MsiDriverReleases(invalid); }
            catch (Exception) { rejected = true; }
            assert(rejected, "Unrelated or malformed MSI metadata was accepted.");
        }
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try { await service.CheckMsiBoardDriverAsync(audio, canceled.Token, true); assert(false, "Canceled browser-source work continued."); }
        catch (OperationCanceledException) { assert(true, "Browser cancellation propagated."); }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var stalled = new ManufacturerDriverService(http)
        {
            RequestTimeout = TimeSpan.FromMilliseconds(100),
            BrowserReader = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return ""; }
        };
        var expired = await stalled.CheckMsiBoardDriverAsync(audio, default, true);
        assert(expired.HasCheckIssue && !expired.IsLiveChecked && clock.Elapsed < TimeSpan.FromSeconds(2),
            "A browser transport fallback escaped the manufacturer's whole-check deadline.");
    }

    private static async Task DellAndRazerAsync(Action<bool, string> assert)
    {
        const string dell = "AW3423DW <a id='download-0-PRODUCT-M46J9' href='https://dl.dell.com/FOLDER/1/ALIENWARE_AW3423DW-MONITOR_A00-00_DRVR_YW4YR.exe'>Driver</a><a href='https://dl.dell.com/FOLDER/1/Alienware_MonitorFirmwareUpdateUtility_W_AW3423DW_M0B205.zip' id='download-0-PRODUCT-ABCDE'>Firmware</a>";
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Html(dell))));
        var service = new ManufacturerDriverService(http);
        var monitor = await service.CheckDellAw3423DwAsync(Driver() with { DeviceClass = "Monitor", InstalledVersion = "1.1.0.0" }, default);
        var hid = await service.CheckDellAw3423DwAsync(Driver() with { DeviceClass = "HIDClass", InstalledVersion = "2.0.1.7" }, default);
        assert(monitor.IsLiveChecked && monitor.Status == ManufacturerDriverStatus.Current && monitor.AvailableVersion == "1.1.0.0", "Dell's live INF release was not compared with the monitor driver.");
        assert(hid.IsLiveChecked && hid.Status == ManufacturerDriverStatus.OfficialSourceOnly && hid.AvailableVersion == "M0B205" && hid.ExecutableUpdate is null,
            "Dell monitor firmware was compared with a HID driver or offered as an automatic install.");
        assert(ManufacturerDriverService.DellDownloads(dell.Replace("AW3423DW", "OTHER-MODEL")).Count == 0, "Another monitor's catalog was accepted.");
        const string razer = "<a href='https://mysupport.razer.com/app/answers/detail/a_id/20396'>Huntsman V3 Pro 8 kHz Firmware Updater Firmware v1.3.0_r2 Guide</a><a href='https://mysupport.razer.com/app/answers/detail/a_id/9777'>Kiyo Pro Ultra Firmware Updater v9.9.9.9 Guide</a>";
        var releases = ManufacturerDriverService.RazerFirmwareVersions(razer, ["Razer Huntsman V3 Pro 8KHz", "Razer Kiyo Pro"]);
        assert(releases.Count == 1 && releases[0].Version == "v1.3.0_r2", "Razer reused a historical version or matched the Kiyo Pro Ultra to a Kiyo Pro.");
        using var firmware = new HttpClient(new Handler((_, _) => Task.FromResult(Html(razer))));
        var current = await new ManufacturerDriverService(firmware).CheckRazerSuiteAsync(Driver() with { GroupMembers = ["Razer Huntsman V3 Pro 8KHz"] }, "4.0.896", default);
        assert(current.IsLiveChecked && current.Message.Contains("v1.3.0_r2") && !current.Message.Contains("v1.02.00_r1"), "The live Razer result discarded a newly published device-specific firmware version.");
    }

    private static void BundledMetadata(Action<bool, string> assert)
    {
        var notes = JsonSerializer.Serialize(new { IDS = new[] { new { downloadInfo = new { ID = "1", Version = "621.14", OtherNotes = Uri.EscapeDataString("<a href='https://us.download.nvidia.com/Windows/621.14/621.14-geforce-win11-x64-release-notes.pdf'>Release notes</a>") } } } });
        assert(ManufacturerDriverService.NvidiaAudioNotesUri(notes, "1", "621.14") is not null && ManufacturerDriverService.NvidiaAudioNotesUri(notes, "1", "621.15") is null,
            "NVIDIA notes were not bound to the exact current catalog record and version.");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.A4).AddText("Release 620 Driver for Windows, Version 621.14 HD Audio Driver - 1.4.6.6", 12, new PdfPoint(20, 700), font);
        var pdf = builder.Build();
        assert(ManufacturerDriverService.NvidiaAudioVersion(pdf, "621.14", default) == "1.4.6.6" && ManufacturerDriverService.NvidiaAudioVersion(pdf, "621.15", default) is null,
            "The real PDF decoder confused the HD Audio module with the graphics version or a different release.");
        const string vmware = "<metadataResponse><version>3.5</version><bulletin><visibleTo><product><version>99.0</version></product></visibleTo><productList><product><version>26.0.1</version><arch>x86_64</arch><productId>ws-windows</productId></product><product><version>99.0</version><arch>x86_64</arch><productId>ws-linux</productId></product></productList></bulletin></metadataResponse>";
        assert(ManufacturerDriverService.VmwareWorkstationVersion(vmware) == "26.0.1", "VMware's schema version, visibility floor or Linux package overrode the actual Windows release.");
        assert(ManufacturerDriverService.InfDriverVersion("DriverVer=10/8/2026,18.14.58.767\nSYNTHETICGamepad", "SYNTHETICGamepad") == "18.14.58.767" &&
               ManufacturerDriverService.InfDriverVersion("DriverVer=10/8/2026,18.14.58.767\nOTHER", "SYNTHETICGamepad") is null,
            "Bundled-driver version evidence was accepted without the exact hardware identifier.");
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
}
