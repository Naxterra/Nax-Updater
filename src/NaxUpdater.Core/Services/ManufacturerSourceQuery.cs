using System.Text;
using System.Text.Json;

namespace NaxUpdater.Core.Services;

public sealed record ManufacturerPageResponse(string Url, string? Html, string? Error);

public static class ManufacturerSourceQuery
{
    public const int MaximumPageCharacters = 2_000_000;
    public static readonly Uri MsiDrivers = new("https://www.msi.com/api/v1/product/support/panel?product=MAG-Z490-TOMAHAWK&type=driver&os=Win11%2064");
    public static readonly Uri DellDrivers = new("https://www.dell.com/support/product-details/en-us/product/aw3423dw-monitor/drivers");
    public static readonly Uri RazerFirmware = new("https://mysupport.razer.com/app/answers/detail/a_id/4166");
    private static readonly HashSet<string> IntelDriverPages = new(StringComparer.Ordinal)
    {
        "https://www.intel.com/content/www/us/en/download/19347/chipset-inf-utility.html",
        "https://www.intel.com/content/www/us/en/download/682431/intel-management-engine-drivers-for-windows-10-and-windows-11.html",
        "https://www.intel.com/content/www/us/en/download/19512/intel-rapid-storage-technology-driver-installation-software-with-intel-optane-memory-10th-and-11th-gen-platforms.html",
        "https://www.intel.com/content/www/us/en/download/727998/intel-network-adapter-driver-for-microsoft-windows-11.html"
    };

    public static bool IsAllowed(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.AbsoluteUri == MsiDrivers.AbsoluteUri || uri.AbsoluteUri == DellDrivers.AbsoluteUri ||
         uri.AbsoluteUri == RazerFirmware.AbsoluteUri || IntelDriverPages.Contains(uri.AbsoluteUri) ||
         uri.Host == "www.dell.com" && System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath,
             @"^/support/product-details/[a-z]{2}-[a-z]{2}/product/aw3423dw-monitor/drivers/?$") && uri.Query.Length == 0);

    public static async Task<string> ReadAsync(Uri uri, string executable, TimeSpan timeout, CancellationToken token)
    {
        if (!IsAllowed(uri) || !File.Exists(executable)) throw new InvalidOperationException("The manufacturer browser query is unavailable.");
        var query = await new ProcessQueryRunner().RunAsync(executable, ["--manufacturer-source", uri.AbsoluteUri], timeout, token, new UTF8Encoding(false, true));
        if (query.ExitCode != 0 || !string.IsNullOrWhiteSpace(query.StandardError) || query.StandardOutput.Length > MaximumPageCharacters * 2)
            throw new InvalidDataException("The manufacturer browser query did not complete successfully.");
        var response = JsonSerializer.Deserialize<ManufacturerPageResponse>(query.StandardOutput);
        if (response?.Url != uri.AbsoluteUri || response.Error is not null || string.IsNullOrWhiteSpace(response.Html) || response.Html.Length > MaximumPageCharacters)
            throw new InvalidDataException("The manufacturer browser did not return a verified catalog response.");
        return response.Html;
    }
}
