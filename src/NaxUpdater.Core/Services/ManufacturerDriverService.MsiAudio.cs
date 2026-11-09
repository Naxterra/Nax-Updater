using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

public sealed partial class ManufacturerDriverService
{
    internal const string MsiAudioSha256 = "4EDE1406F08E29394A202FCBDFD960E018CAEE6073DD9B8D2839AEC0771F0B13";

    internal static UpdateCheckResult? MsiAudioOffer(InstalledHardwareDriver driver, MsiDriverRelease release)
    {
        // This exact release was inspected: HDXMSI.inf covers the board's codec,
        // setup.exe is signed by Realtek, and the supplied response file disables
        // reboot. A changed archive needs its own reviewed installer recipe.
        if (!DriverUpdateIdentity.IsMsiAudioDevice(driver) ||
            !(IsMicrosoftProvider(driver.Provider) || driver.Provider.Equals("Realtek Semiconductor Corp.", StringComparison.OrdinalIgnoreCase)) ||
            release.Title != "Realtek HD Universal Driver" || release.Version != "6.0.10007.1" ||
            !release.Sha256.Equals(MsiAudioSha256, StringComparison.OrdinalIgnoreCase) ||
            release.Download.AbsoluteUri != "https://download.msi.com/dvr_exe/mb/realtek_audio_R.zip") return null;
        var plan = new UpdateExecutionPlan(UpdateExecutionKind.DownloadedZipExe,
            release.Download, "realtek_audio_R.zip", release.Sha256, "=Realtek Semiconductor Corp.", null,
            ["/s"], true, ["download.msi.com", "download-2.msi.com"], [],
            NestedInstallerRelativePath: @"Realtek HD Universal Driver\setup.exe",
            ExpectedHardwareId: DriverUpdateIdentity.MsiAudioHardwareId,
            ExtractInstallerDirectory: true);
        return new(driver.Identity, driver.DeviceName, driver.InstalledVersion, release.Version,
            UpdateStatus.Available, DriverUpdateIdentity.MsiAudioProviderId, "MSI MAG Z490 TOMAHAWK audio",
            "neutral", "MSI Realtek multi-language audio installer", "x64", "stable", MsiBoardSupport.AbsoluteUri,
            "The complete MSI audio package is protected by MSI's published SHA-256 and Realtek's exact Authenticode publisher. Its supplied silent response file disables automatic restart. Windows must report the Realtek driver on the same audio device before installation is confirmed.",
            plan, UpdateProviderAuthority.ProducerRelease, CorrelationKey: $"driver:{driver.Identity}");
    }
}
