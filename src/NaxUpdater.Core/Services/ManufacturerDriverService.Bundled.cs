using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NaxUpdater.Core.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using Windows.Management.Deployment;

namespace NaxUpdater.Core.Services;

public sealed partial class ManufacturerDriverService
{
    private readonly ConcurrentDictionary<string, Task<ManufacturerDriverResult>> _nvidiaChecks = new();
    private readonly ConcurrentDictionary<string, Uri> _nvidiaNotes = new();
    private Lazy<Task<string>>? _vmwareCatalog;

    internal static Uri? NvidiaAudioNotesUri(string json, string id, string version)
    {
        using var document = JsonDocument.Parse(json);
        foreach (var entry in document.RootElement.GetProperty("IDS").EnumerateArray())
        {
            if (!entry.TryGetProperty("downloadInfo", out var info) ||
                !info.TryGetProperty("ID", out var recordId) || recordId.GetString() != id ||
                !info.TryGetProperty("Version", out var recordVersion) || recordVersion.GetString() != version) continue;
            var html = string.Join(" ", new[] { "ReleaseNotes", "OtherNotes", "InstallationNotes" }
                .Select(key => info.TryGetProperty(key, out var notes) && notes.ValueKind == JsonValueKind.String
                    ? WebUtility.HtmlDecode(Uri.UnescapeDataString(notes.GetString() ?? "")) : ""));
            var match = Regex.Match(html, @"https://us\.download\.nvidia\.com/Windows/" + Regex.Escape(version) + @"/[a-zA-Z0-9._-]+release-notes\.pdf");
            if (match.Success) return new Uri(match.Value);
        }
        return null;
    }

    internal static string? NvidiaAudioVersion(string text, string gpuVersion)
    {
        if (!Regex.IsMatch(text, @"\bVersion\s+" + Regex.Escape(gpuVersion) + @"\b", RegexOptions.IgnoreCase)) return null;
        var module = Regex.Match(text, @"\bHD\s+Audio\s+Driver\s*[-—–:]?\s*(?<version>\d+(?:\.\d+){3})\b", RegexOptions.IgnoreCase);
        return module.Success ? module.Groups["version"].Value : null;
    }

    internal static string? NvidiaAudioVersion(byte[] bytes, string gpuVersion, CancellationToken token)
    {
        using var pdf = PdfDocument.Open(bytes);
        if (pdf.NumberOfPages > 200) throw new InvalidDataException("NVIDIA's release notes exceeded the page limit.");
        for (var index = 1; index <= Math.Min(30, pdf.NumberOfPages); index++)
        {
            token.ThrowIfCancellationRequested();
            var audio = NvidiaAudioVersion(ContentOrderTextExtractor.GetText(pdf.GetPage(index)), gpuVersion);
            if (audio is not null) return audio;
        }
        return null;
    }

    internal async Task<ManufacturerDriverResult> CheckNvidiaAudioAsync(InstalledHardwareDriver driver, CancellationToken token)
    {
        try
        {
            var displays = await Task.WhenAll(_nvidiaChecks.Values).WaitAsync(token);
            var gpu = displays.Where(r => r.IsLiveChecked && r.AvailableVersion is not null)
                .OrderByDescending(r => r.AvailableVersion!, Comparer<string>.Create(VersionOrder.Compare)).FirstOrDefault()
                ?? throw new InvalidDataException("No applicable live NVIDIA display package was available for its bundled audio check.");
            var id = gpu.SourceUri?.AbsolutePath.TrimEnd('/').Split('/').Last() ?? "";
            if (!_nvidiaNotes.TryGetValue(id, out var source)) throw new InvalidDataException("NVIDIA's selected driver did not identify its official release notes.");
            var bytes = await ReadDriverMetadataBytesAsync(source, 8 * 1024 * 1024, token);
            var audio = NvidiaAudioVersion(bytes, gpu.AvailableVersion!, token);
            if (audio is null) throw new InvalidDataException("NVIDIA's release notes did not identify the selected package's HD Audio module.");
            return new(driver, VersionOrder.Compare(audio, driver.InstalledVersion) > 0 ? ManufacturerDriverStatus.Available : ManufacturerDriverStatus.Current,
                audio, "NVIDIA HD Audio", gpu.SourceUri,
                $"NVIDIA's live Game Ready package {gpu.AvailableVersion} includes HD Audio driver {audio}; installed is {driver.InstalledVersion}. Audio is serviced by NVIDIA's graphics package, not a separate installer.",
                null, ManufacturerCheckEvidence.LiveCatalog);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { return Error(driver, "NVIDIA HD Audio", NvidiaDriverHomeEn, exception.Message); }
    }

    private async Task<byte[]> ReadDriverMetadataBytesAsync(Uri uri, int maximum, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(RequestTimeout);
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("The driver metadata response exceeded its size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var memory = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(buffer, deadline.Token)) != 0)
        {
            if (memory.Length + count > maximum) throw new InvalidDataException("The driver metadata response exceeded its size limit.");
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }

    internal static string? VmwareWorkstationVersion(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
        return XDocument.Load(reader).Descendants("productList").Elements("product")
            .Where(e => e.Element("productId")?.Value == "ws-windows" && e.Element("arch")?.Value == "x86_64")
            .Select(e => e.Element("version")?.Value).Where(v => v is not null && NumericVersionRegex().IsMatch(v))
            .OrderByDescending(v => v!, Comparer<string>.Create(VersionOrder.Compare)).FirstOrDefault();
    }

    private async Task<string> ReadVmwareCatalogAsync(CancellationToken token)
    {
        var index = await GetStringAsync(new Uri("https://softwareupdate.vmware.com/cds/vmw-desktop/ws-windows.xml"), token);
        using var reader = XmlReader.Create(new StringReader(index), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 100_000 });
        var relative = XDocument.Load(reader).Descendants("metadata")
            .Where(e => e.Element("productId")?.Value == "ws-windows").Select(e => e.Element("url")?.Value)
            .FirstOrDefault(v => v is not null && Regex.IsMatch(v, @"^info-only/ws-windows/[0-9.]+/metadata\.xml\.gz$"))
            ?? throw new InvalidDataException("VMware's feed did not identify its Windows Workstation catalog.");
        var data = await ReadDriverMetadataBytesAsync(new Uri("https://softwareupdate.vmware.com/cds/vmw-desktop/" + relative), 1024 * 1024, token);
        using var gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var text = new StreamReader(gzip);
        var xml = new char[2_000_001];
        var length = await text.ReadBlockAsync(xml.AsMemory(), token);
        if (length > 2_000_000) throw new InvalidDataException("VMware's catalog exceeded its size limit.");
        return VmwareWorkstationVersion(new string(xml, 0, length))
            ?? throw new InvalidDataException("VMware's catalog did not identify an x64 Windows Workstation release.");
    }

    internal async Task<ManufacturerDriverResult> CheckVmwareDriverAsync(InstalledHardwareDriver driver, CancellationToken token)
    {
        var source = new Uri("https://softwareupdate.vmware.com/cds/vmw-desktop/ws-windows.xml");
        try
        {
            var latest = await (_vmwareCatalog ??= new Lazy<Task<string>>(() => ReadVmwareCatalogAsync(token))).Value.WaitAsync(token);
            var installed = ReadInstalledProgramVersion("VMware Workstation") ?? "—";
            return new(driver, ManufacturerDriverStatus.VendorSoftwareManaged, latest, "VMware Workstation", source,
                $"VMware's live Workstation catalog lists {latest}; installed Workstation is {installed}. These virtual drivers are serviced with Workstation; their INF versions are not compared with the application version.",
                null, ManufacturerCheckEvidence.LiveCatalog);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { return Error(driver, "VMware Workstation", source, exception.Message); }
    }

    internal async Task<ManufacturerDriverResult> CheckWdSupportAsync(InstalledHardwareDriver driver, CancellationToken token)
    {
        try
        {
            var page = await GetStringAsync(WdExternalDriverSupport, token);
            if (!page.Contains("SES driver will auto install", StringComparison.OrdinalIgnoreCase) ||
                !page.Contains("Windows 10 and higher", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("WD's current support page did not confirm inbox servicing and the legacy SES restriction.");
            return NoWdDriverRequired(driver);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { return Error(driver, "WD Elements support", WdExternalDriverSupport, exception.Message); }
    }
}
