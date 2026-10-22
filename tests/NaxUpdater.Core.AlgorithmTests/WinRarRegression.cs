using System.Net;
using System.Text;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

// RARLAB's page as of 2026-10-21: labels "English (64-bit x64)", the beta table
// (7.30 beta 1) before the release table (7.23).
internal static class WinRarRegression
{
    private static string Row(string file, string label, string version) =>
        $"<tr>\n  <td><a href=\"/rar/{file}\">\n  <b>{label}</b></a></td>\n  <td align=\"center\">{version}</td>\n</tr>\n";

    public static async Task RunAsync(Action<bool, string> assert)
    {
        var betaOnly = Row("winrar-x64-730b1.exe", "English (64-bit x64)", "7.30 beta 1") +
                       Row("winrar-arm64-730b1.exe", "English (ARM64)", "7.30 beta 1") +
                       Row("winrar-x64-730b1d.exe", "German (64-bit x64)", "7.30 beta 1");
        var releases = Row("winrar-x64-723.exe", "English (64-bit x64)", "7.23") +
                       Row("winrar-x64-723d.exe", "German (64-bit x64)", "7.23") +
                       Row("winrar-x64-723nl.exe", "Dutch (64-bit x64)", "7.23");
        var page = "<h2>Latest English WinRAR and RAR beta versions</h2><table>" + betaOnly + "</table><table>" + releases + "</table>";

        assert(WinRarUpdateProvider.SelectRelease(page, "en", includeBeta: false) is { Version: "7.23.0", Label: "7.23" } final &&
               final.DownloadUri.AbsolutePath == "/rar/winrar-x64-723.exe",
            "A final WinRAR installation was not matched to the newest x64 release under RARLAB's \"English (64-bit x64)\" labels.");
        assert(WinRarUpdateProvider.SelectRelease(page, "en", includeBeta: true) is { Version: "7.30.1", Label: "7.30 beta 1" } &&
               WinRarUpdateProvider.SelectRelease(page, "de", includeBeta: true)?.DownloadUri.AbsolutePath == "/rar/winrar-x64-730b1d.exe",
            "A beta installation did not follow the beta line, or the German x64 beta was not chosen.");
        assert(WinRarUpdateProvider.SelectRelease(Row("winrar-x64-723.exe", "English", "7.23"), "en", false)?.Version == "7.23.0",
            "The earlier plain \"English\" label is no longer recognized.");
        // RARLAB's layout of 2026-10-22: the language is a separate cell, every link
        // says "64-bit version", and the English builds are "WinRAR 64-bit 7.30 beta 1".
        string Cell(string language, string file, string version)
        {
            var arm = file.Replace("x64", "arm64");
            return $"<tr><td>{language}</td><td><a href=\"/rar/{file}\">64-bit version</a></td><td><a href=\"/rar/{arm}\">ARM64 version</a></td><td>{version}</td></tr>";
        }
        var layout3 = "<tr><td><a href=\"/rar/winrar-x64-730b1.exe\"><b>WinRAR 64-bit 7.30 beta 1</b></a></td></tr>" +
                      "<tr><td><a href=\"/rar/winrar-x64-723.exe\"><b>WinRAR 64-bit 7.23</b></a></td></tr>" +
                      Cell("German", "winrar-x64-730b1d.exe", "7.30 beta 1") + Cell("Danish", "winrar-x64-723dk.exe", "7.23") +
                      Cell("Brazilian Portuguese", "winrar-x64-723br.exe", "7.23") + Cell("German", "winrar-x64-723d.exe", "7.23");
        assert(WinRarUpdateProvider.SelectRelease(layout3, "de", includeBeta: false) is { Version: "7.23.0" } germanFinal &&
               germanFinal.DownloadUri.AbsolutePath == "/rar/winrar-x64-723d.exe" &&
               WinRarUpdateProvider.SelectRelease(layout3, "de", includeBeta: true)?.DownloadUri.AbsolutePath == "/rar/winrar-x64-730b1d.exe" &&
               WinRarUpdateProvider.SelectRelease(layout3, "en", includeBeta: false)?.DownloadUri.AbsolutePath == "/rar/winrar-x64-723.exe" &&
               WinRarUpdateProvider.SelectRelease(layout3, "en", includeBeta: true)?.Version == "7.30.1",
            "RARLAB's layout with a separate language cell was not read from the installer file names (German d, not Danish dk or Brazilian br).");
        assert(WinRarUpdateProvider.InstalledBeta("WinRAR 7.30 Beta 1 (64-Bit)") == 1 && WinRarUpdateProvider.InstalledBeta("WinRAR 7.23 (64-bit)") is null,
            "The installed beta number was misread from the display name.");
        var beta1 = WinRarUpdateProvider.Rank("7.30.1", 1)!.Value;
        assert(WinRarUpdateProvider.Rank("7.30.0", null)!.Value.CompareTo(beta1) > 0 &&
               WinRarUpdateProvider.Rank("7.30.2", 2)!.Value.CompareTo(beta1) > 0 &&
               beta1.CompareTo(WinRarUpdateProvider.Rank("7.23.0", null)!.Value) > 0,
            "WinRAR releases, betas and older releases are not ordered as RARLAB numbers them.");

        // The installed 7.30 beta 1 is current against this page; once 7.30 is released,
        // it is offered although its registered number (7.30.0) is below the beta's (7.30.1).
        var installer = Encoding.UTF8.GetBytes("fixture installer");
        var html = page;
        using var http = new HttpClient(new Respond(() => html, installer));
        var provider = new WinRarUpdateProvider(http);
        var installedBeta = new InstalledApplication("registry:winrar", "WinRAR 7.30 Beta 1 (64-Bit)", "win.rar GmbH", "7.30.1", "7.30.1", "fixture",
            null, "fixture", null, null, InstallScope.Machine, ManagementMode.Registry, ConfidenceLevel.High, false, [], null, []);
        var current = await provider.CheckAsync(installedBeta, CancellationToken.None);
        assert(current is { Status: UpdateStatus.Current, ExecutionPlan: null, AvailableVersion: "7.30.1" },
            $"The installed WinRAR 7.30 beta 1 was not current against RARLAB's page: {current.Status} · {current.Message}");
        // The language comes from the UI culture here (no install path), so list both.
        html = Row("winrar-x64-730.exe", "English (64-bit x64)", "7.30") + Row("winrar-x64-730d.exe", "German (64-bit x64)", "7.30") + page;
        var released = await provider.CheckAsync(installedBeta, CancellationToken.None);
        assert(released is { Status: UpdateStatus.Available, AvailableVersion: "7.30.0" } && released.ExecutionPlan?.DownloadUri?.AbsolutePath.StartsWith("/rar/winrar-x64-730") == true && released.ExecutionPlan.DownloadUri.AbsolutePath.Contains("730b") == false,
            $"The final 7.30 release was not offered over 7.30 beta 1: {released.Status} · {released.AvailableVersion}");
        var finalInstalled = installedBeta with { DisplayName = "WinRAR 7.23 (64-bit)", NormalizedVersion = "7.23.0", InstalledVersion = "7.23.0" };
        html = page;
        assert((await provider.CheckAsync(finalInstalled, CancellationToken.None)).Status == UpdateStatus.Current,
            "A final WinRAR installation was offered a beta.");
    }

    private sealed class Respond(Func<string> page, byte[] installer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = request.RequestUri!.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? new ByteArrayContent(installer)
                    : new StringContent(page())
            });
    }
}
