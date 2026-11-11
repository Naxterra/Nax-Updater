using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace NaxUpdater.Core.Services;

public sealed partial class ManufacturerDriverService
{
    internal static (Uri Url, string Hash, bool Sha512)? IntelEthernetArchive(string html, string version)
    {
        var urls = Regex.Matches(WebUtility.HtmlDecode(html), @"https://downloadmirror\.intel\.com/\d+/Wired_driver_[0-9.]+_x64\.zip")
            .Cast<Match>().Select(m => new Uri(m.Value)).Distinct().ToArray();
        if (urls.Length != 1 || urls[0].Segments[^1] != $"Wired_driver_{version}_x64.zip") return null;
        var text = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));
        var hash = Regex.Match(text, @"SHA[- ]?(?<bits>256|512)\s*[:：]?\s*(?<hash>[A-Fa-f0-9]{64,128})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!hash.Success) return null;
        var sha512 = hash.Groups["bits"].Value == "512";
        var value = hash.Groups["hash"].Value;
        return value.Length == (sha512 ? 128 : 64) ? (urls[0], value, sha512) : null;
    }

    internal static (string Version, string HardwareId)? IntelI219Inf(string inf, string? hardwareId)
    {
        if (hardwareId?.Contains(@"PCI\VEN_8086&DEV_15BC", StringComparison.OrdinalIgnoreCase) != true ||
            !Regex.IsMatch(inf, @"PCI\\VEN_8086&DEV_15BC(?:&|\s|,|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return null;
        var version = Regex.Match(inf, @"^\s*DriverVer\s*=\s*[^,\r\n]+,\s*(?<version>\d+(?:\.\d+){3})\s*(?:;.*)?$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return version.Success ? (version.Groups["version"].Value, @"PCI\VEN_8086&DEV_15BC") : null;
    }

    private async Task<IntelDriverPayload> ReadIntelEthernetArchiveAsync(IntelRelease release, string? hardwareId, CancellationToken token)
    {
        var archive = IntelEthernetArchive(release.Html, release.Version)
            ?? throw new InvalidDataException("Intel's current Ethernet archive or published SHA-256/SHA-512 was not verified.");
        var directory = Path.Combine(kyoceraCacheDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NaxUpdater", "SourceCache"), "IntelInf");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, archive.Hash.ToUpperInvariant() + ".zip");
        if (!File.Exists(path) || !await MatchesIntelArchiveHashAsync(path, archive.Hash, archive.Sha512, token))
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(RequestTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, archive.Url);
                request.Headers.UserAgent.ParseAdd("NaxUpdater/0.17.76");
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                response.EnsureSuccessStatusCode();
                const long maximumSize = 80L * 1024 * 1024;
                if (response.Content.Headers.ContentLength > maximumSize) throw new InvalidDataException("Intel's driver metadata archive exceeded its size limit.");
                await using (var source = await response.Content.ReadAsStreamAsync(deadline.Token))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    var buffer = new byte[65536];
                    long total = 0;
                    int count;
                    while ((count = await source.ReadAsync(buffer, deadline.Token)) != 0)
                    {
                        total += count;
                        if (total > maximumSize) throw new InvalidDataException("Intel's driver metadata archive exceeded its size limit.");
                        await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
                    }
                }
                if (!await MatchesIntelArchiveHashAsync(temporary, archive.Hash, archive.Sha512, token))
                    throw new InvalidDataException("Intel's Ethernet archive did not match its published hash.");
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        const string infPath = "PRO1000/Winx64/W11/e1d.inf";
        using var zip = ZipFile.OpenRead(path);
        var entries = zip.Entries.Where(e => e.FullName.Replace('\\', '/').Equals(infPath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Length != 1 || entries[0].Length > 2 * 1024 * 1024) throw new InvalidDataException("Intel's exact Windows 11 I219 INF was absent or ambiguous.");
        using var reader = new StreamReader(entries[0].Open(), detectEncodingFromByteOrderMarks: true);
        var inf = IntelI219Inf(await reader.ReadToEndAsync(token), hardwareId)
            ?? throw new InvalidDataException("Intel's Windows 11 INF did not confirm the exact installed Ethernet hardware.");
        await using var archiveStream = File.OpenRead(path);
        var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(archiveStream, token));
        return new(inf.Version, archive.Url, archive.Url.Segments[^1], sha256, infPath.Replace('/', '\\'));
    }

    private static async Task<bool> MatchesIntelArchiveHashAsync(string path, string hash, bool sha512, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        var actual = sha512 ? await SHA512.HashDataAsync(stream, token) : await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(actual).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }
}
