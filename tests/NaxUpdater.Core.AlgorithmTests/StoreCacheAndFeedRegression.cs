using System.Net;
using NaxUpdater.Core.Services;

internal static class StoreCacheAndFeedRegression
{
    public static async Task RunAsync(Action<bool, string> assert, string fixture)
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-10-12T10:00:00Z"));
        var path = Path.Combine(fixture, "store-cache-lifetime.json");
        var cache = new StoreSearchCache(path, clock);
        cache.RecordNoUpdate("family|1.0|2.0");
        clock.Now += TimeSpan.FromHours(5);
        var reloaded = new StoreSearchCache(path, clock);
        assert(cache.FindNoUpdate("family|1.0|2.0") is not null && reloaded.FindNoUpdate("family|1.0|2.0") is not null,
            "A recent Store answer was not reused, or the cache was not persisted.");
        clock.Now += TimeSpan.FromHours(1);
        assert(reloaded.FindNoUpdate("family|1.0|2.0") is null, "A Store answer was reused past 6 hours.");

        // One stalled feed connection must not fail the lookup: the retry uses a fresh connection.
        var feed = """<?xml version="1.0" encoding="utf-8"?><feed xmlns="http://www.w3.org/2005/Atom" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata"><entry><title>nodejs</title><m:properties><d:Version>26.11.1</d:Version><d:Dependencies></d:Dependencies></m:properties></entry></feed>""";
        using var stalled = new HttpClient(new Respond(stall: true, feed));
        using var fresh = new HttpClient(new Respond(stall: false, feed));
        var started = System.Diagnostics.Stopwatch.StartNew();
        var found = await ChocolateyPackageService.QueryCommunityFeedAsync(stalled, fresh, "https://community.chocolatey.org/api/v2/Packages()", TimeSpan.FromMilliseconds(200), CancellationToken.None);
        assert(found.Count == 1 && found[0].Version == "26.11.1" && started.Elapsed < TimeSpan.FromSeconds(5),
            "A stalled Chocolatey feed connection was not retried on a fresh connection.");
        try
        {
            await ChocolateyPackageService.QueryCommunityFeedAsync(stalled, stalled, "https://community.chocolatey.org/api/v2/Packages()", TimeSpan.FromMilliseconds(100), CancellationToken.None);
            assert(false, "A feed that stalled twice was reported as answered.");
        }
        catch (TimeoutException exception) { assert(exception.Message.Contains("twice"), "The double feed timeout was not explained."); }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Respond(bool stall, string body) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (stall) await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
