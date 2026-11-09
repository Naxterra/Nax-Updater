using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;
using System.Net;

internal static class DriverCoverageRegression
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        SummaryCountsEvidenceAndFailures(assert);
        ReferenceIsNotCurrent(assert);
        await FailedAndSuccessfulCatalogsAsync(assert);
    }

    private static InstalledHardwareDriver Driver(string name = "Fixture") => new("fixture", name, "System", "Fixture", "Fixture", "1.0", null, null, null);
    private static ManufacturerDriverResult Result(ManufacturerDriverStatus status, ManufacturerCheckEvidence evidence) =>
        new(Driver(), status, "2.0", "Fixture", new Uri("https://fixture.test/"), "fixture", null, evidence);

    private static void SummaryCountsEvidenceAndFailures(Action<bool, string> assert)
    {
        var snapshot = new ManufacturerDriverSnapshot(DateTimeOffset.UtcNow, [
            Result(ManufacturerDriverStatus.Current, ManufacturerCheckEvidence.LiveCatalog),
            Result(ManufacturerDriverStatus.Available, ManufacturerCheckEvidence.LiveCatalog),
            Result(ManufacturerDriverStatus.OfficialSourceOnly, ManufacturerCheckEvidence.LiveCatalog),
            Result(ManufacturerDriverStatus.OfficialSourceOnly, ManufacturerCheckEvidence.ReferenceRelease),
            Result(ManufacturerDriverStatus.Current, ManufacturerCheckEvidence.None),
            Result(ManufacturerDriverStatus.NoUpdateRequired, ManufacturerCheckEvidence.SupportGuidance),
            Result(ManufacturerDriverStatus.Error, ManufacturerCheckEvidence.None),
            Result(ManufacturerDriverStatus.VendorSoftwareManaged, ManufacturerCheckEvidence.ReferenceRelease) with { LiveCheckFailed = true }
        ], ["Inventory read failed"]);
        assert(snapshot.CheckedSourceCount == 3, "The summary excluded Current/Available live checks or counted references as fresh checks.");
        assert(snapshot.CheckIssueCount == 3, "The summary hid a failed source or counted only inventory issues.");
    }

    private static void ReferenceIsNotCurrent(Action<bool, string> assert)
    {
        var dell = ManufacturerDriverService.CheckDellAw3423Dw(Driver("Dell AW3423DW") with { DeviceClass = "Monitor", InstalledVersion = "9.9.9.9" });
        assert(dell.Status != ManufacturerDriverStatus.Current && !dell.IsLiveChecked && dell.AvailableVersion == "1.1.0.0",
            "Dell's available version echoed the installed version and falsely established currency.");
        var rst = ManufacturerDriverService.CheckIntelRst(Driver() with { InstalledVersion = "18.37.6.1010", HardwareId = @"PCI\VEN_8086&DEV_06D6" });
        assert(rst.Status != ManufacturerDriverStatus.Current && !rst.IsLiveChecked,
            "An RST version comparison against a fixed package was counted as a fresh catalog check.");
    }

    private static async Task FailedAndSuccessfulCatalogsAsync(Action<bool, string> assert)
    {
        var chipset = Driver() with { Identity = "group:intel:chipset" };
        using var denied = new HttpClient(new Page(null));
        var blocked = await new ManufacturerDriverService(denied).CheckIntelChipsetPackageAsync(chipset, CancellationToken.None, "10.1.20658.8883");
        assert(blocked.LiveCheckFailed && blocked.Status != ManufacturerDriverStatus.Current && !blocked.IsLiveChecked,
            "An Intel 403 was hidden behind an installed version equal to an archived release.");
        var oldInstalled = await new ManufacturerDriverService(denied).CheckIntelChipsetPackageAsync(chipset, CancellationToken.None, "10.1.1.1");
        assert(oldInstalled.Status != ManufacturerDriverStatus.Available && oldInstalled.ExecutableUpdate is null,
            "A blocked live check turned a historical chipset reference into a fresh offer.");
        using var live = new HttpClient(new Page(DriverSourceRegression.IntelPage("19347", "10.1.20658.8883")));
        var verified = await new ManufacturerDriverService(live).CheckIntelChipsetPackageAsync(chipset, CancellationToken.None, "10.1.20658.8883");
        assert(verified.Status == ManufacturerDriverStatus.Current && verified.IsLiveChecked && !verified.HasCheckIssue,
            "A genuine successful live comparison was no longer recognised as Current.");
        using var challenge = new HttpClient(new Page("Please verify your browser before continuing"));
        var razer = Driver() with { Identity = "group:razer:suite", GroupMembers = ["Razer Kiyo Pro"] };
        var incomplete = await new ManufacturerDriverService(challenge).CheckRazerSuiteAsync(razer, "4.0.896", CancellationToken.None);
        assert(incomplete.HasCheckIssue && !incomplete.IsLiveChecked && incomplete.Status == ManufacturerDriverStatus.VendorSoftwareManaged,
            "An HTTP-200 challenge page was accepted as a checked firmware catalog or lost Synapse ownership.");
        using var firmware = new HttpClient(new Page("<a href='https://mysupport.razer.com/app/answers/detail/a_id/4582'>Kiyo Pro Firmware Updater v1.5.0.1_r1 Guide</a>"));
        var confirmed = await new ManufacturerDriverService(firmware).CheckRazerSuiteAsync(razer, "4.0.896", CancellationToken.None);
        assert(confirmed.IsLiveChecked && !confirmed.HasCheckIssue, "A real firmware catalog match was not counted as checked.");
    }

    private sealed class Page(string? body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(
            new HttpResponseMessage(body is null ? HttpStatusCode.Forbidden : HttpStatusCode.OK) { Content = new StringContent(body ?? "Forbidden"), RequestMessage = request });
    }
}
