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
