using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

// TP-Link publishes no hash. The owner allowed packages from TP-Link's own server whose
// installer carries TP-Link's exact signature. Each scenario is one method.
internal static class TpLinkRegression
{
    private const string Page = """
        <a href="https://static.tp-link.com/upload/driver/2025/202502/20250214/Archer_TBE400UH(US_EU_CA_JP_TW)_V1_5002.24.117.4_Win10_Win11.zip">117.4</a>
        <a href="https://static.tp-link.com/upload/driver/2025/202504/20250430/[Beta Driver]TBE400UH_MLO_5002.24.126.4.zip">beta</a>
        <a href="https://static.tp-link.com/upload/driver/2025/202508/20250807/TBE400UH_V1.0_20250617,5002.24.126.4.zip">126.4</a>
        <a href="https://static.tp-link.com/upload/driver/2025/202509/20250926/TBE400UH_V1.0_20250617,5002.24.126.4.zip">126.4</a>
        """;

    public static void Run(Action<bool, string> assert)
    {
        tp_link_package_is_offered_as_its_signed_installer(assert);
        nested_exe_installer_is_extracted_from_its_archive(assert);
        same_day_tp_link_uploads_offer_nothing(assert);
    }

    // The extraction step accepted only MSI files, so every TP-Link install failed after the download.
    private static void nested_exe_installer_is_extracted_from_its_archive(Action<bool, string> assert)
    {
        var work = Path.Combine(Path.GetTempPath(), "naxupdater-zipexe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var archive = Path.Combine(work, "package.zip");
            Directory.CreateDirectory(Path.Combine(work, "exe"));
            Directory.CreateDirectory(Path.Combine(work, "msi"));
            using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
            using (var entry = zip.CreateEntry("SetupInstall.exe").Open())
                entry.Write("MZ fixture"u8);
            var extracted = UpdateExecutionService.ExtractNestedInstaller(archive, "SetupInstall.exe", Path.Combine(work, "exe"), ".exe");
            var ok = File.Exists(extracted.Path) && extracted.Path.EndsWith("SetupInstall.exe", StringComparison.OrdinalIgnoreCase);
            foreach (var hold in extracted.ContentLocks) hold.Dispose();
            Exception? msiOnly = null;
            try { UpdateExecutionService.ExtractNestedInstaller(archive, "SetupInstall.exe", Path.Combine(work, "msi")); }
            catch (InvalidDataException exception) { msiOnly = exception; }
            assert(ok && msiOnly is not null, "A nested EXE installer was not extracted, or an EXE was accepted where an MSI is expected.");
        }
        finally { Directory.Delete(work, true); }
    }

    // Without TP-Link's date folders, or with two different files on the newest date, the
    // right package cannot be told apart, so nothing is offered.
    private static void same_day_tp_link_uploads_offer_nothing(Action<bool, string> assert)
    {
        using var http = new HttpClient();
        var driver = new InstalledHardwareDriver("driver:tp-link", "TP-Link Wi-Fi 7 Wireless USB Adapter", "Net", "TP-Link", "TP-Link Systems Inc.", "5002.24.117.4", null, null, null);
        const string twins = """
            <a href="https://static.tp-link.com/upload/driver/2025/202509/20250926/A_5002.24.126.4.zip">a</a>
            <a href="https://static.tp-link.com/upload/driver/2025/202509/20250926/B_5002.24.126.4.zip">b</a>
            """;
        const string undated = """<a href="https://static.tp-link.com/upload/driver/latest/A_5002.24.126.4.zip">a</a>""";
        var service = new ManufacturerDriverService(http);
        assert(service.TpLinkOffer(driver, twins, "5002.24.126.4", "5002.24.126.4") is null &&
               service.TpLinkOffer(driver, undated, "5002.24.126.4", "5002.24.126.4") is null,
            "A TP-Link package was chosen although the newest upload could not be told apart.");
    }

    private static void tp_link_package_is_offered_as_its_signed_installer(Action<bool, string> assert)
    {
        using var http = new HttpClient();
        var driver = new InstalledHardwareDriver("driver:tp-link", "TP-Link Wi-Fi 7 Wireless USB Adapter", "Net", "TP-Link", "TP-Link Systems Inc.", "5002.24.117.4", null, null, null);
        var offer = new ManufacturerDriverService(http).TpLinkOffer(driver, Page, "5002.24.126.4", "5002.24.126.4");
        var plan = offer?.ExecutionPlan;
        assert(plan is { Kind: UpdateExecutionKind.DownloadedZipExe, SignatureOnly: true, RequireAuthenticode: true, ExpectedSigner: "TP-Link Systems Inc.", NestedInstallerRelativePath: "SetupInstall.exe" } &&
               plan.DownloadUri!.AbsoluteUri.Contains("20250926", StringComparison.Ordinal) && plan.AllowedDownloadHosts.SequenceEqual(["static.tp-link.com"]),
            $"TP-Link's newest non-beta package was not offered as its signed installer: {plan?.DownloadUri}");
        var bound = offer! with
        {
            ExecutionPlan = plan! with { CheckGenerationId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15), InstalledVersionPrecondition = offer.InstalledVersion },
            CorrelationKey = "fixture"
        };
        assert(UpdatePlanValidator.Validate(bound, DateTimeOffset.UtcNow) is null, $"The signed TP-Link plan was rejected: {UpdatePlanValidator.Validate(bound, DateTimeOffset.UtcNow)}");
        assert(UpdatePlanValidator.Validate(bound with { ExecutionPlan = bound.ExecutionPlan! with { RequireAuthenticode = false } }, DateTimeOffset.UtcNow) is not null,
            "A nested installer without a required signature was accepted.");
        assert(new ManufacturerDriverService(http).TpLinkOffer(driver, Page, "5002.24.126.9", "5002.24.126.9") is null,
            "A TP-Link package was offered although its version is not on the page.");
    }
}
