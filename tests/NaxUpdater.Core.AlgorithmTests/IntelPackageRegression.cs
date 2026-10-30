using System.Net;
using System.Text;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

// The Intel chipset and RST rows only showed Intel's package version and claimed nothing.
// Each scenario is one method.
internal static class IntelPackageRegression
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        await intel_chipset_package_is_compared_with_the_installed_utility(assert);
        await intel_chipset_fallback_version_is_named_as_last_verified(assert);
        intel_rst_package_is_offered_over_the_inbox_driver(assert);
        intel_rst_claims_nothing_for_unknown_controllers_or_versions(assert);
    }

    private static readonly InstalledHardwareDriver Chipset =
        new("group:intel:chipset", "Intel Z490 Chipset Device Software", "System", "Intel", "Intel", "10.1.18.1", null, null, null);

    private static async Task intel_chipset_package_is_compared_with_the_installed_utility(Action<bool, string> assert)
    {
        using var http = new HttpClient(new Page("Chipset INF utility version 10.1.20658.8883 for Windows"));
        var service = new ManufacturerDriverService(http);
        var current = await service.CheckIntelChipsetPackageAsync(Chipset, CancellationToken.None, "10.1.20658.8883");
        assert(current is { Status: ManufacturerDriverStatus.Current, Driver.InstalledVersion: "10.1.20658.8883" },
            $"The installed Chipset INF Utility equal to Intel's package was not current: {current.Status} · {current.Driver.InstalledVersion}");
        var older = await service.CheckIntelChipsetPackageAsync(Chipset, CancellationToken.None, "10.1.18793.8276");
        assert(older is { Status: ManufacturerDriverStatus.Available, AvailableVersion: "10.1.20658.8883", ExecutableUpdate: null },
            $"An older Chipset INF Utility was not shown as a newer Intel package: {older.Status}");
    }

    // When Intel's page cannot be read, the last verified release could be stale, so the
    // result says that it is the last verified release.
    private static async Task intel_chipset_fallback_version_is_named_as_last_verified(Action<bool, string> assert)
    {
        using var http = new HttpClient(new Page(null));
        var result = await new ManufacturerDriverService(http).CheckIntelChipsetPackageAsync(Chipset, CancellationToken.None, "10.1.20658.8883");
        assert(result.Status == ManufacturerDriverStatus.Current && result.Message.Contains("last release Nax-Updater verified", StringComparison.Ordinal),
            $"A fallback chipset version was presented as if Intel had just published it: {result.Status} · {result.Message}");
    }

    private static readonly InstalledHardwareDriver Rst = new("group:intel:rst", "Intel Rapid Storage Technology", "SCSIAdapter", "Intel",
        "Intel Corporation", "15.44.0.1015", null, @"PCI\VEN_8086&DEV_06D6&SUBSYS_7C801462&REV_00", "iastorav.inf");

    private static void intel_rst_package_is_offered_over_the_inbox_driver(Action<bool, string> assert)
    {
        // The owner accepted Intel's EULA for RST, so the silent install with -accepteula is offered,
        // bound to Intel's published SHA-256 and signature; -silent must be the last flag.
        var offer = ManufacturerDriverService.CheckIntelRst(Rst);
        var plan = offer.ExecutableUpdate?.ExecutionPlan;
        assert(offer is { Status: ManufacturerDriverStatus.Available, AvailableVersion: "18.7.6.1010.3" } &&
               plan is { Kind: UpdateExecutionKind.DownloadedExe, Sha256: "A2B2E20D6D8100E9EE344746F80849524C64490B90686A13C09268CADB976B37", ExpectedSigner: "Intel Corporation", RequireAuthenticode: true, RequiresElevation: true } &&
               plan.Arguments.SequenceEqual(["-onlydriver", "-accepteula", "-silent"]) && offer.ExecutableUpdate!.AvailableVersion == "18.7.6.1010",
            $"Intel's RST 18.7.6 was not offered as Intel's verified silent installer over the Windows inbox driver 15.44: {offer.Status}");
        var bound = offer.ExecutableUpdate! with
        {
            ExecutionPlan = plan! with { CheckGenerationId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15), InstalledVersionPrecondition = Rst.InstalledVersion },
            CorrelationKey = "fixture"
        };
        assert(UpdatePlanValidator.Validate(bound, DateTimeOffset.UtcNow) is null, $"The RST plan was rejected: {UpdatePlanValidator.Validate(bound, DateTimeOffset.UtcNow)}");
        var installed = ManufacturerDriverService.CheckIntelRst(Rst with { InstalledVersion = "18.7.6.1010" });
        assert(installed.Status == ManufacturerDriverStatus.Current, "The installed RST 18.7.6 driver was not current.");
    }

    private static void intel_rst_claims_nothing_for_unknown_controllers_or_versions(Action<bool, string> assert)
    {
        var unreadable = ManufacturerDriverService.CheckIntelRst(Rst with { InstalledVersion = "—" });
        var otherPlatform = ManufacturerDriverService.CheckIntelRst(Rst with { HardwareId = @"PCI\VEN_8086&DEV_7AE2&SUBSYS_00000000" });
        assert(unreadable.Status == ManufacturerDriverStatus.OfficialSourceOnly && otherPlatform.Status == ManufacturerDriverStatus.OfficialSourceOnly,
            $"RST was claimed current or newer for an unreadable version or another platform's controller: {unreadable.Status} · {otherPlatform.Status}");
    }

    private sealed class Page(string? body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = request }
                : new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "text/html") });
    }
}
