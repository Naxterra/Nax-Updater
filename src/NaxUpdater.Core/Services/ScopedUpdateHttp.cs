using System.Net;
using System.Net.Sockets;

namespace NaxUpdater.Core.Services;

internal static class ScopedUpdateHttp
{
    internal static HttpClient Client { get; } = new(CreateHandler()) { Timeout = TimeSpan.FromSeconds(30) };
    internal static readonly string[] GitHubAssetHosts =
        ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com", "github-releases.githubusercontent.com"];

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = ConnectPublicAsync
    };

    internal static bool IsPublicHttps(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        uri.UserInfo.Length == 0 && uri.HostNameType == UriHostNameType.Dns && uri.IdnHost.Contains('.') &&
        !uri.IdnHost.TrimEnd('.').EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) &&
        !uri.IdnHost.TrimEnd('.').EndsWith(".local", StringComparison.OrdinalIgnoreCase);

    internal static async Task<HttpResponseMessage> GetAsync(
        HttpClient client, Uri uri, IReadOnlySet<string> hosts, CancellationToken token)
    {
        for (int redirect = 0; ; redirect++)
        {
            if (!IsPublicHttps(uri) || !hosts.Contains(uri.IdnHost))
                throw new HttpRequestException("The update metadata URL is outside its configured HTTPS origins.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("NaxUpdater");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                return response;
            using (response)
            {
                if (redirect == 7 || response.Headers.Location is not { } location ||
                    !Uri.TryCreate(uri, location, out var next))
                    throw new HttpRequestException("The update metadata returned an invalid redirect.");
                uri = next;
            }
        }
    }

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var endpoint = context.DnsEndPoint;
        SocketException? failure = null;
        foreach (var address in await Dns.GetHostAddressesAsync(endpoint.Host, token))
        {
            if (!IsPublicAddress(address)) continue;
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException exception)
            {
                socket.Dispose();
                failure = exception;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new HttpRequestException("The update host did not resolve to a reachable public address.", failure);
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            int first = bytes[0], second = bytes[1];
            return first is not (0 or 10 or 127) && first < 224 &&
                !(first == 100 && second is >= 64 and <= 127) &&
                !(first == 169 && second == 254) &&
                !(first == 172 && second is >= 16 and <= 31) &&
                !(first == 192 && second == 168) &&
                !(first == 198 && second is 18 or 19);
        }
        // Global unicast only, excluding IPv4 transition mechanisms.
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xE0) == 0x20 &&
            !(bytes[0] == 0x20 && (bytes[1] == 0x02 || bytes[1] == 0x01 && bytes[2] == 0 && bytes[3] == 0));
    }
}
