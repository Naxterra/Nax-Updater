using System.Net;
using System.Text;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

// Kyocera printer drivers and utilities against Kyocera's user support site. The fixture is
// a shortened copy of the real model file for the ECOSYS MA2100cwfx (model 358).
internal static class KyoceraRegression
{
    private const string Index = """{"countries":{},"models":[["1","358","ECOSYS MA2100cwfx","77","20220405"],["1","414","ECOSYS MA2101cwfx","86","20250401"]]}""";
    private const string Model = """
        {"id":"358","downloads":{
          "102":{"305":{"name":"Kx87_UPD_8.7.0917_en_RC3_signed.zip","version":"8.7.0917","url":"https://cdam.kyoceradocumentsolutions.com/content/dam/gdam_dc/dc_global/executables/web/Kx87_UPD_8.7.0917_en_RC3_signed.zip","release_date":"2026/10/16"}},
          "103":{"284":{"name":"Kyocera TWAIN Driver_3.4.0328.exe","version":"3.4.0328","url":"https://cdam.kyoceradocumentsolutions.com/content/dam/gdam_dc/dc_global/executables/web/Kyocera TWAIN Driver_3.4.0328.exe"}},
          "105":{"81202":{"name":"Status Monitor 5_5.4.12.3.exe","version":"5.5.4.12.3","url":"https://cdam.kyoceradocumentsolutions.com/content/dam/gdam_dc/dc_global/executables/utility/product_081/Status Monitor 5_5.4.12.3.exe"},
                 "81204":{"name":"WIFI Setup Tool_3.0.2.11.exe","version":"3.0.2.11","url":"https://cdam.kyoceradocumentsolutions.com/content/dam/gdam_dc/dc_global/executables/utility/product_081/WIFI Setup Tool_3.0.2.11.exe"},
                 "81206":{"name":"Quick Scan_2.1.2503.exe","version":"2.1.2503","url":"https://cdam.kyoceradocumentsolutions.com/content/dam/gdam_dc/dc_global/executables/utility/product_081/Quick Scan_2.1.2503.exe"},
                 "999":{"name":"Fake Tool_9.9.exe","version":"9.9","url":"https://downloads.example/Fake Tool_9.9.exe"}}}}
        """;

    private const string SecondModel = """
        {"id":"414","downloads":{"105":{"1":{"name":"Quick Scan_2.6.0100.exe","version":"2.6.0100","url":"https://cdam.kyoceradocumentsolutions.com/content/dam/gdam_dc/dc_global/executables/web/Quick Scan_2.6.0100.exe"}}}}
        """;

    public static async Task RunAsync(Action<bool, string> assert)
    {
        kx_driver_name_gives_the_printer_model(assert);
        await kyocera_utilities_are_checked_against_the_printer_support_page(assert);
        await kyocera_kx_driver_is_checked_in_the_driver_view(assert);
        await failed_kyocera_download_is_tried_again(assert);
        await newest_listing_across_printer_models_wins(assert);
        await unchanged_kyocera_files_are_answered_from_the_cache(assert);
        await one_scan_asks_kyocera_once_for_utilities_and_drivers(assert);
        await kyocera_file_with_byte_order_mark_is_read(assert);
        await catalog_sends_its_own_user_agent(assert);
        await kyocera_twain_listing_that_agrees_with_itself_opens_kyoceras_signed_installer(assert);
        kyocera_listing_that_disagrees_lacks_a_size_or_names_another_utility_stays_a_known_release(assert);
        interactive_installer_is_never_part_of_update_all(assert);
    }

    // The real listing on the owner's PC on 2026-11-16: name, version field and address name
    // 3.4.1108, and Kyocera gives the exact size of the signed package.
    private static readonly KyoceraDownload Twain = new("Kyocera TWAIN Driver_3.4.1108.exe", "Kyocera TWAIN Driver", "3.4.1108",
        new Uri("https://cdam.kyoceradocumentsolutions.com/content/dam/gdam_dc/dc_global/executables/web/Kyocera TWAIN Driver_3.4.1108.exe"),
        "2026/11/16", "2026-11 | Ver.3.4.1108", 129911224);

    private static async Task kyocera_twain_listing_that_agrees_with_itself_opens_kyoceras_signed_installer(Action<bool, string> assert)
    {
        var parsed = KyoceraSupportCatalog.ParseDownloads(System.Text.Json.JsonDocument.Parse("""
            {"downloads":{"103":{"307":{"name":"Kyocera TWAIN Driver_3.4.1108.exe","version":"2026-11 | Ver.3.4.1108","size":"129911224",
            "url":"https://cdam.kyoceradocumentsolutions.com/content/dam/gdam_dc/dc_global/executables/web/Kyocera TWAIN Driver_3.4.1108.exe"}}}}
            """).RootElement).Single();
        assert(parsed == Twain with { ReleaseDate = null }, $"Kyocera's size and version field were not read with the download: {parsed}");
        var plan = KyoceraUtilityUpdateProvider.InteractivePlan(Twain);
        assert(plan is { Kind: UpdateExecutionKind.DownloadedExe, SignatureOnly: true, RequireAuthenticode: true, Interactive: true,
                   RequiresElevation: true, ExpectedSize: 129911224, ExpectedSigner: "KYOCERA Document Solutions Inc.", Sha256: null } &&
               plan.AllowedDownloadHosts.SequenceEqual(["cdam.kyoceradocumentsolutions.com"]) && plan.Arguments.Count == 0,
            "The agreeing TWAIN listing did not become Kyocera's signed installer with its exact size, opened for the user.");
        var stamped = plan! with { CheckGenerationId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            InstalledVersionPrecondition = "3.4.0328" };
        var offer = new UpdateCheckResult("registry:twain", "Kyocera TWAIN Driver", "3.4.0328", "3.4.1108", UpdateStatus.Available, "kyocera-support",
            "Kyocera support page", "application-managed", "fixture", "x86", "stable", null, null, stamped, CorrelationKey: "twain");
        assert(UpdatePlanValidator.Validate(offer, DateTimeOffset.UtcNow) is null &&
               UpdatePlanValidator.Validate(offer with { ExecutionPlan = stamped with { ExpectedSize = null } }, DateTimeOffset.UtcNow) is not null &&
               UpdatePlanValidator.Validate(offer with { ExecutionPlan = stamped with { ExpectedSigner = null } }, DateTimeOffset.UtcNow) is not null &&
               UpdatePlanValidator.Validate(offer with { ExecutionPlan = stamped with { Kind = UpdateExecutionKind.NativeCommand, NativeExecutable = "fixture.exe" } }, DateTimeOffset.UtcNow) is not null,
            "Kyocera's wizard plan was refused, or a wizard without size, signer or downloaded EXE was accepted.");
        // The package is checked against the exact size before its signature: one byte more is refused.
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "nax-size-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var file = Path.Combine(folder, "package.exe");
            await File.WriteAllBytesAsync(file, new byte[16]);
            var sized = stamped with { ExpectedSize = 16 };
            assert(UpdatePackageDownloader.SizeMismatch(file, sized) is null &&
                   UpdatePackageDownloader.SizeMismatch(file, sized with { ExpectedSize = 17 }) is { } refused && refused.Contains("17") &&
                   UpdatePackageDownloader.SizeMismatch(file, sized with { ExpectedSize = null }) is null,
                "A downloaded package of another size than Kyocera lists was accepted, or the listed size was refused.");
        }
        finally { Directory.Delete(folder, true); }
        // Through the provider, the TWAIN row becomes installable only with such a listing.
        using var http = new HttpClient(new Pages(model: Model.Replace(
            "\"Kyocera TWAIN Driver_3.4.0328.exe\",\"version\":\"3.4.0328\",\"url\":\"https://cdam.kyoceradocumentsolutions.com/content/dam/gdam_dc/dc_global/executables/web/Kyocera TWAIN Driver_3.4.0328.exe\"",
            "\"Kyocera TWAIN Driver_3.4.1108.exe\",\"version\":\"2026-11 | Ver.3.4.1108\",\"size\":\"129911224\",\"url\":\"https://cdam.kyoceradocumentsolutions.com/content/dam/gdam_dc/dc_global/executables/web/Kyocera TWAIN Driver_3.4.1108.exe\"")));
        var provider = new KyoceraUtilityUpdateProvider(new KyoceraSupportCatalog(http), () => ["ECOSYS MA2100cwfx"]);
        var twain = await provider.CheckAsync(App("Kyocera TWAIN Driver", "3.4.0328"), CancellationToken.None);
        assert(twain is { Status: UpdateStatus.Available, AvailableVersion: "3.4.1108", ExecutionPlan.Interactive: true, Applicability: UpdateApplicability.Applicable },
            $"Kyocera's agreeing TWAIN listing was not offered as an update through Kyocera's installer: {twain.Status} · {twain.Message}");
    }

    private static void kyocera_listing_that_disagrees_lacks_a_size_or_names_another_utility_stays_a_known_release(Action<bool, string> assert)
    {
        (string Case, KyoceraDownload Download)[] refused =
        [
            ("version field names another version", Twain with { ListedVersion = "2026-11 | Ver.3.4.1107" }),
            ("version field names a longer version", Twain with { ListedVersion = "Ver.3.4.11080" }),
            ("version field names a version ending in it", Twain with { ListedVersion = "Ver.13.4.1108" }),
            ("version field names a fourth part", Twain with { ListedVersion = "Ver.3.4.1108.5" }),
            ("no version field", Twain with { ListedVersion = null }),
            ("no size", Twain with { Size = null }),
            ("address names another file", Twain with { Url = new Uri("https://cdam.kyoceradocumentsolutions.com/content/dam/gdam_dc/dc_global/executables/web/Kyocera TWAIN Driver_3.4.1107.exe") }),
            ("archive instead of an EXE", Twain with { Name = "Kyocera TWAIN Driver_3.4.1108.zip" }),
            // Status Monitor's real listing: its version field reads 5.5.4.12.3 for 5.4.12.3.
            ("Status Monitor version field", Twain with { Name = "Status Monitor 5_5.4.12.3.exe", Product = "Status Monitor 5", Version = "5.4.12.3",
                Url = new Uri("https://cdam.kyoceradocumentsolutions.com/content/dam/gdam_dc/dc_global/executables/utility/product_081/Status Monitor 5_5.4.12.3.exe"),
                ListedVersion = "5.5.4.12.3" })
        ];
        var accepted = refused.Where(static item => KyoceraUtilityUpdateProvider.InteractivePlan(item.Download) is not null).Select(static item => item.Case).ToArray();
        assert(accepted.Length == 0, $"A Kyocera listing that does not agree with itself became installable: {string.Join(", ", accepted)}");
        // The rule is the same for every Kyocera utility; it is not a TWAIN exception.
        var quickScan = Twain with { Name = "Quick Scan_2.1.2503.exe", Product = "Quick Scan", Version = "2.1.2503",
            Url = new Uri("https://cdam.kyoceradocumentsolutions.com/content/dam/gdam_dc/dc_global/executables/web/Quick Scan_2.1.2503.exe"),
            ListedVersion = "2.1.2503", Size = 52000000 };
        assert(KyoceraUtilityUpdateProvider.InteractivePlan(quickScan) is { Interactive: true, ExpectedSize: 52000000 },
            "Another Kyocera utility with an agreeing listing was not handled by the same rule.");
    }

    private static void interactive_installer_is_never_part_of_update_all(Action<bool, string> assert)
    {
        var plan = KyoceraUtilityUpdateProvider.InteractivePlan(Twain)!;
        var offer = new UpdateCheckResult("registry:twain", "Kyocera TWAIN Driver", "3.4.0328", "3.4.1108", UpdateStatus.Available, "kyocera-support",
            "Kyocera support page", "application-managed", "fixture", "x86", "stable", null, null, plan);
        assert(offer.IsInstallable && !offer.InUpdateAll && (offer with { ExecutionPlan = plan with { Interactive = false } }).InUpdateAll,
            "An installer that the user clicks through was put into Update all, or an unattended installer was left out.");
    }

    private static async Task catalog_sends_its_own_user_agent(Action<bool, string> assert)
    {
        using var http = new HttpClient(new Pages(requireUserAgent: true));
        var model = await new KyoceraSupportCatalog(http).FindModelAsync("ECOSYS MA2100cwfx", CancellationToken.None);
        assert(model?.Id == "358", "Kyocera catalog requests relied on a caller-supplied User-Agent and were rejected.");
    }

    private static async Task kyocera_file_with_byte_order_mark_is_read(Action<bool, string> assert)
    {
        using var http = new HttpClient(new Pages(byteOrderMark: true));
        var model = await new KyoceraSupportCatalog(http).FindModelAsync("ECOSYS MA2100cwfx", CancellationToken.None);
        assert(model?.Id == "358", "A Kyocera file that starts with a UTF-8 byte order mark could not be read.");
    }

    // The second check asks with the saved ETag and gets 304 without a body; a changed file is
    // downloaded again.
    private static async Task unchanged_kyocera_files_are_answered_from_the_cache(Action<bool, string> assert)
    {
        var cache = Path.Combine(Path.GetTempPath(), "nax-kyocera-" + Guid.NewGuid().ToString("N"));
        try
        {
            var server = new Conditional();
            using var http = new HttpClient(server);
            var first = await new KyoceraSupportCatalog(http, cache).FindModelAsync("ECOSYS MA2100cwfx", CancellationToken.None);
            var fullAfterFirst = server.FullBodies;
            var second = await new KyoceraSupportCatalog(http, cache).FindModelAsync("ECOSYS MA2100cwfx", CancellationToken.None);
            var fullAfterSecond = server.FullBodies;
            server.Version = "v2";
            var third = await new KyoceraSupportCatalog(http, cache).FindModelAsync("ECOSYS MA2100cwfx", CancellationToken.None);
            assert(first?.Id == "358" && second?.Downloads.Count == first.Downloads.Count && third?.Id == "358" &&
                   fullAfterFirst == 2 && fullAfterSecond == 2 && server.FullBodies == 4 && server.NotModified == 2,
                $"Kyocera's unchanged files were downloaded again, or a changed file was taken from the cache: full {fullAfterFirst}/{fullAfterSecond}/{server.FullBodies}, 304 {server.NotModified}");
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }

    private static async Task one_scan_asks_kyocera_once_for_utilities_and_drivers(Action<bool, string> assert)
    {
        var server = new Conditional();
        using var http = new HttpClient(server);
        var sources = new SharedScanSources(http);
        var utility = await new KyoceraUtilityUpdateProvider(sources.Kyocera, () => ["ECOSYS MA2100cwfx"])
            .CheckAsync(App("KYOCERA Status Monitor 5", "5.4.12.3"), CancellationToken.None);
        var driver = await new ManufacturerDriverService(http, sources.Kyocera).CheckKyoceraPrinterAsync(
            new InstalledHardwareDriver("printer-driver:kyocera:ecosys ma2100cwfx", "Kyocera ECOSYS MA2100cwfx", "Printer",
                "Kyocera", "KYOCERA Document Solutions", "8.7.917.0", null, null, null), CancellationToken.None);
        assert(utility.Status == UpdateStatus.Current && driver.Status == ManufacturerDriverStatus.Current && server.Requests == 2,
            $"The utilities and the printer driver of one scan asked Kyocera more than once: {server.Requests} requests");
    }

    // Serves the fixture with an ETag and answers a matching If-None-Match with 304.
    private sealed class Conditional : HttpMessageHandler
    {
        public string Version { get; set; } = "v1";
        public int Requests { get; private set; }
        public int FullBodies { get; private set; }
        public int NotModified { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            var etag = $"\"{Version}\"";
            if (request.Headers.IfNoneMatch.Any(value => value.Tag == etag))
            {
                NotModified++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified) { RequestMessage = request });
            }
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/support_product/js/index.json" => Index,
                "/support_product/js/models/358.json" => Model,
                _ => null
            };
            if (body is null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
            FullBodies++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json") };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
            return Task.FromResult(response);
        }
    }

    private static async Task failed_kyocera_download_is_tried_again(Action<bool, string> assert)
    {
        using var http = new HttpClient(new Pages(failFirst: true));
        var catalog = new KyoceraSupportCatalog(http);
        Exception? first = null;
        try { await catalog.FindModelAsync("ECOSYS MA2100cwfx", CancellationToken.None); }
        catch (Exception exception) { first = exception; }
        var second = await catalog.FindModelAsync("ECOSYS MA2100cwfx", CancellationToken.None);
        assert(first is not null && second?.Id == "358", "A failed Kyocera download was kept for the rest of the check instead of being tried again.");
    }

    private static async Task newest_listing_across_printer_models_wins(Action<bool, string> assert)
    {
        using var http = new HttpClient(new Pages());
        var provider = new KyoceraUtilityUpdateProvider(new KyoceraSupportCatalog(http), () => ["ECOSYS MA2100cwfx", "ECOSYS MA2101cwfx"]);
        var scan = await provider.CheckAsync(App("Kyocera Quick Scan", "2.5.0326"), CancellationToken.None);
        assert(scan is { Status: UpdateStatus.NewerReleaseKnown, AvailableVersion: "2.6.0100" },
            $"An older listing on one printer model hid the newer release on another: {scan.Status} · {scan.AvailableVersion}");
    }

    private static void kx_driver_name_gives_the_printer_model(Action<bool, string> assert)
    {
        assert(KyoceraSupportCatalog.ModelOfKxDriver("Kyocera ECOSYS MA2100cwfx KX") == "ECOSYS MA2100cwfx" &&
               KyoceraSupportCatalog.ModelOfKxDriver("Kyocera TASKalfa 2554ci KX (XPS)") == "TASKalfa 2554ci" &&
               KyoceraSupportCatalog.ModelOfKxDriver("Microsoft Print To PDF") is null,
            "The printer model was not read from the KX driver name, or another driver was taken for one.");
        assert(KyoceraSupportCatalog.ProductKey("KYOCERA Status Monitor 5") == KyoceraSupportCatalog.ProductKey("Status Monitor 5") &&
               KyoceraSupportCatalog.ProductKey("Wi-Fi Setup Tool") == KyoceraSupportCatalog.ProductKey("WIFI Setup Tool") &&
               KyoceraSupportCatalog.ProductKey("Kyocera Quick Scan") != KyoceraSupportCatalog.ProductKey("Status Monitor 5"),
            "Kyocera product names were not matched to their download names.");
    }

    private static async Task kyocera_utilities_are_checked_against_the_printer_support_page(Action<bool, string> assert)
    {
        using var http = new HttpClient(new Pages());
        var provider = new KyoceraUtilityUpdateProvider(new KyoceraSupportCatalog(http), () => ["ECOSYS MA2100cwfx"]);
        var monitor = await provider.CheckAsync(App("KYOCERA Status Monitor 5", "5.4.12.3"), CancellationToken.None);
        assert(monitor is { Status: UpdateStatus.Current, ExecutionPlan: null } && monitor.ReleaseNotesUrl?.Contains("m=358&p=77") == true,
            $"Status Monitor 5.4.12.3 was not current against Kyocera's page: {monitor.Status} · {monitor.Message}");
        var scan = await provider.CheckAsync(App("Kyocera Quick Scan", "2.0.0100"), CancellationToken.None);
        assert(scan is { Status: UpdateStatus.NewerReleaseKnown, AvailableVersion: "2.1.2503", ExecutionPlan: null },
            $"A newer Quick Scan on Kyocera's page was not reported, or was offered for installation: {scan.Status} · {scan.AvailableVersion}");
        var wifi = await provider.CheckAsync(App("Wi-Fi Setup Tool", "3.0.2.25"), CancellationToken.None);
        assert(wifi.Status == UpdateStatus.Current, $"An installed Wi-Fi Setup Tool newer than Kyocera's listing was not current: {wifi.Status}");
        var unlisted = await provider.CheckAsync(App("Kyocera Net Viewer", "6.0"), CancellationToken.None);
        assert(unlisted.Status == UpdateStatus.Unsupported, $"An application missing from the model page was reported as checked: {unlisted.Status}");
        var fake = await provider.CheckAsync(App("Fake Tool", "1.0"), CancellationToken.None);
        assert(fake.Status == UpdateStatus.Unsupported, "A download outside Kyocera's own hosts was used as a source.");
        var noPrinter = new KyoceraUtilityUpdateProvider(new KyoceraSupportCatalog(http), () => []);
        assert((await noPrinter.CheckAsync(App("Kyocera TWAIN Driver", "3.4.0328"), CancellationToken.None)).Status == UpdateStatus.Unsupported,
            "A Kyocera utility was checked without an installed Kyocera printer.");
    }

    private static async Task kyocera_kx_driver_is_checked_in_the_driver_view(Action<bool, string> assert)
    {
        using var http = new HttpClient(new Pages());
        var service = new ManufacturerDriverService(http);
        // MSFT_PrinterDriver packs the version into 16-bit parts: 8.7.917.0 is KX 8.7.0917.
        var installed = new InstalledHardwareDriver("printer-driver:kyocera:ecosys ma2100cwfx", "Kyocera ECOSYS MA2100cwfx", "Printer",
            "Kyocera", "KYOCERA Document Solutions", "8.7.917.0", null, null, null);
        var current = await service.CheckKyoceraPrinterAsync(installed, CancellationToken.None);
        assert(current is { Status: ManufacturerDriverStatus.Current, AvailableVersion: "8.7.0917" },
            $"The installed KX driver 8.7.917.0 was not current against Kyocera's 8.7.0917: {current.Status} · {current.Message}");
        var older = await service.CheckKyoceraPrinterAsync(installed with { InstalledVersion = "8.5.2405.0" }, CancellationToken.None);
        assert(older is { Status: ManufacturerDriverStatus.Available, ExecutableUpdate: null } && older.SourceUri?.Query.Contains("m=358") == true,
            $"A newer KX driver was not offered as Kyocera's download page: {older.Status} · {older.SourceUri}");
    }

    private static InstalledApplication App(string name, string version) =>
        new($"registry:{name}", name, "KYOCERA Document Solutions Inc.", version, version, "fixture", null, "fixture", null, null,
            InstallScope.Machine, ManagementMode.Registry, ConfidenceLevel.High, false, [], null, []);

    private sealed class Pages(bool failFirst = false, bool byteOrderMark = false, bool requireUserAgent = false, string? model = null) : HttpMessageHandler
    {
        private bool _failed;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (requireUserAgent && !request.Headers.UserAgent.Any(p => p.Product?.Name == "NaxUpdater"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            if (failFirst && !_failed)
            {
                _failed = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { RequestMessage = request });
            }
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/support_product/js/index.json" => Index,
                "/support_product/js/models/358.json" => model ?? Model,
                "/support_product/js/models/414.json" => SecondModel,
                _ => null
            };
            if (body is not null && byteOrderMark)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { RequestMessage = request, Content = new ByteArrayContent([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(body)]) });
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request }
                : new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
