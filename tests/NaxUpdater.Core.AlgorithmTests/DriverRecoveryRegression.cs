using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

// The driver details show what a device runs and what Windows' driver store still holds for it.
// The fixture is pnputil's XML output for the I219 after its update to 12.19.2.64.
// Each scenario is one method.
internal static class DriverRecoveryRegression
{
    private const string InstanceId = @"PCI\VEN_8086&DEV_15BC&SUBSYS_7C801462&REV_00\3&11583659&0&FE";

    private const string I219 = """
        <?xml version="1.0" encoding="utf-8"?>
        <PnpUtil Version="10.0.26300" Command="/enum-devices /drivers /format xml">
            <Device InstanceId="PCI\VEN_8086&amp;DEV_15BC&amp;SUBSYS_7C801462&amp;REV_00\3&amp;11583659&amp;0&amp;FE">
                <DeviceDescription>Intel(R) Ethernet Connection (7) I219-V</DeviceDescription>
                <ClassName>Net</ClassName>
                <Status>Started</Status>
                <DriverName>oem30.inf</DriverName>
                <MatchingDrivers>
                    <DriverName DriverName="oem30.inf">
                        <OriginalName>e1d.inf</OriginalName>
                        <ProviderName>Intel</ProviderName>
                        <DriverVersion>05/07/2025 12.19.2.64</DriverVersion>
                        <Status>BestRanked/Installed</Status>
                    </DriverName>
                    <DriverName DriverName="net1ic64.inf">
                        <ProviderName>Microsoft</ProviderName>
                        <DriverVersion>08/03/2015 12.19.1.32</DriverVersion>
                        <Status>Outranked</Status>
                    </DriverName>
                    <DriverName DriverName="oem22.inf">
                        <OriginalName>e1d.inf</OriginalName>
                        <ProviderName>Intel</ProviderName>
                        <DriverVersion>01/30/2022 12.19.2.45</DriverVersion>
                        <Status>Outranked</Status>
                    </DriverName>
                </MatchingDrivers>
            </Device>
        </PnpUtil>
        """;

    public static void Run(Action<bool, string> assert)
    {
        earlier_package_of_the_same_provider_is_found(assert);
        staged_driver_is_shown_as_waiting_for_a_restart(assert);
        driver_row_names_its_connected_devices(assert);
    }

    private static void earlier_package_of_the_same_provider_is_found(Action<bool, string> assert)
    {
        var details = DriverRecovery.Parse(I219, InstanceId);
        var earlier = details?.EarlierPackages() ?? [];
        // Windows' own inbox driver is another provider's package, not a way back to Intel's.
        assert(details is { AwaitingRestart: false, Installed: { InfName: "oem30.inf", Version: "12.19.2.64", Date: "05/07/2025" } } &&
               earlier.Count == 1 && earlier[0] is { InfName: "oem22.inf", OriginalName: "e1d.inf", Version: "12.19.2.45", Provider: "Intel" },
            $"The active driver or the earlier Intel package was not read from pnputil's list: {details?.Installed?.InfName} · {string.Join(", ", earlier.Select(static p => p.InfName))}");
        assert(DriverRecovery.Parse(I219, @"PCI\VEN_8086&DEV_0000\other") is null, "A different device's entry was taken for the selected one.");
        // A package that matches two hardware IDs of the device is listed twice by pnputil.
        var twice = I219.Replace("</MatchingDrivers>", """
            <DriverName DriverName="oem22.inf"><OriginalName>e1d.inf</OriginalName><ProviderName>Intel</ProviderName>
            <DriverVersion>01/30/2022 12.19.2.45</DriverVersion><Status>Outranked</Status></DriverName></MatchingDrivers>
            """, StringComparison.Ordinal);
        assert(DriverRecovery.Parse(twice, InstanceId)?.EarlierPackages().Count == 1, "An earlier package that matches two hardware IDs was listed twice.");
    }

    private static void staged_driver_is_shown_as_waiting_for_a_restart(Action<bool, string> assert)
    {
        var staged = I219.Replace("<Status>Started</Status>",
            """<Status>Problem</Status><ProblemCode Problem="CM_PROB_NEED_RESTART">14</ProblemCode>""", StringComparison.Ordinal);
        var details = DriverRecovery.Parse(staged, InstanceId);
        assert(details is { AwaitingRestart: true, ProblemCode: 14 } && DriverRecovery.IsBootCritical("SCSIAdapter") && !DriverRecovery.IsBootCritical("Net"),
            $"A driver waiting for a restart was not recognized, or storage drivers were not marked boot critical: {details?.ProblemCode}");
    }

    private static void driver_row_names_its_connected_devices(Action<bool, string> assert)
    {
        var live = new InstalledHardwareDriver($"pnp:{InstanceId}", "Intel(R) Ethernet Connection (7) I219-V", "Net", "Intel", "Intel",
            "12.19.2.64", null, @"PCI\VEN_8086&DEV_15BC&SUBSYS_7C801462&REV_00", "oem30.inf");
        var retained = live with { Identity = @"driver-registration:{4d36e972-e325-11ce-bfc1-08002be10318}:0007", IsPresent = false };
        var rows = ManufacturerDriverService.AggregateDriverPackages([live, retained]);
        var row = rows.Single(static driver => driver.IsPresent);
        assert(row.DeviceInstanceIds is [InstanceId], $"The driver row did not carry exactly its connected device: {string.Join(", ", row.DeviceInstanceIds ?? [])}");
    }
}
