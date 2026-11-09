using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

internal static class MsiAudioPackageRegression
{
    public static async Task RunAsync(ManufacturerDriverSnapshot snapshot, Action<bool, string> assert)
    {
        var row = snapshot.Results.FirstOrDefault(r => DriverUpdateIdentity.IsMsiAudioDevice(r.Driver));
        if (row is null || row.Status != ManufacturerDriverStatus.Available) return;
        var update = row.ExecutableUpdate;
        assert(update?.IsInstallable == true && update.ProviderId == DriverUpdateIdentity.MsiAudioProviderId,
            "The live MSI audio update has no executable in-app offer.");
        var signer = new NativeAuthenticodeVerifier();
        var downloader = new UpdatePackageDownloader(signer);
        var cache = Path.Combine(Path.GetTempPath(), "NaxUpdater-Smoke-Audio");
        using var installer = await downloader.DownloadAndVerifyAsync(update!, cache, null, default);
        var executor = new UpdateExecutionService(signer);
        var prepared = await executor.PrepareAsync(update!, installer);
        try
        {
            var directory = prepared.WorkingDirectory!;
            var inf = File.ReadAllText(Path.Combine(directory, "Win64", "HDXMSI.inf"));
            var infVersion = UpdateExecutionService.ReadInfDriverVersion(inf);
            assert(infVersion == update!.AvailableVersion, $"MSI audio INF version {infVersion} differs from catalog {update.AvailableVersion}.");
            assert(inf.Contains(DriverUpdateIdentity.MsiAudioHardwareId, StringComparison.OrdinalIgnoreCase), "MSI audio INF lacks the exact codec/subsystem.");
            assert(File.ReadAllText(Path.Combine(directory, "setup.iss")).Contains("BootOption=0", StringComparison.Ordinal), "MSI audio response file permits automatic restart.");
            assert(System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(Path.Combine(directory, "setup.ini")), @"(?im)^SilentReboot\s*=\s*0\s*$"), "MSI audio configuration permits silent restart.");
            assert(File.Exists(Path.Combine(directory, "data1.cab")) && Directory.Exists(Path.Combine(directory, "Config")), "MSI audio preparation lost companion files.");
            var signature = signer.Verify(prepared.ExecutablePath!, "=Realtek Semiconductor Corp.");
            assert(signature.IsValid, $"MSI audio publisher verification failed: {signature.Error}");
            Console.WriteLine($"MSI audio {update.AvailableVersion}: real release hash, Realtek signature and complete payload verified; installer was not run.");
        }
        finally { executor.DiscardPrepared(prepared); }
    }
}
