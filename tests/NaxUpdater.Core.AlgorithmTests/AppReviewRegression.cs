using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

internal static class AppReviewRegression
{
    public static async Task RunAsync(Action<bool, string> assert, string fixture)
    {
        await RedirectsAsync(assert, fixture);
        await PartialResultsAndRetryAsync(assert);
        await CancellationAsync(assert, fixture);
        await ScanOwnershipAsync(assert);
        RemovalAndPreferences(assert, fixture);
    }

    private static async Task RedirectsAsync(Action<bool, string> assert, string fixture)
    {
        // Real TLS/HTTP and the production transport factory, with only test TLS
        // trust and DNS replaced. Every socket connects exclusively to loopback.
        foreach (var scenario in new[] { "allowed", "blocked", "http", "mirror" })
        {
            await using var server = new RedirectServer(scenario);
            using var http = server.Client();
            var plan = new UpdateExecutionPlan(UpdateExecutionKind.DownloadedExe, server.Uri("allowed.test", "/start"),
                "fixture.exe", Convert.ToHexString(SHA256.HashData(RedirectServer.Payload)), null, null, [], false,
                ["allowed.test"], [], RequireAuthenticode: false, AllowHashVerifiedRedirects: scenario == "mirror");
            var update = Result(App("redirect"), UpdateStatus.Available) with { AvailableVersion = "2.0", ExecutionPlan = plan };
            Exception? error = null;
            try
            {
                using var downloaded = await new UpdatePackageDownloader(http, new NeverSign())
                    .DownloadAndVerifyAsync(update, Path.Combine(fixture, "redirect-" + scenario));
                assert(File.ReadAllBytes(downloaded.Path).SequenceEqual(RedirectServer.Payload), "The permitted redirect changed the verified payload.");
            }
            catch (Exception exception) { error = exception; }
            var permitted = scenario is "allowed" or "mirror";
            assert(permitted ? error is null : error is InvalidOperationException,
                $"Incorrect redirect policy for {scenario}: {error}");
            assert(server.Hosts.Count == (permitted ? 3 : 1),
                $"The downloader sent a request to a forbidden redirect, or lost a permitted redirect: {scenario} / {string.Join(",", server.Hosts)}");
            if (!permitted) assert(!server.Hosts.Any(host => host.StartsWith("blocked.test")), "A blocked host received a request before validation.");
        }
    }

    private static async Task PartialResultsAndRetryAsync(Action<bool, string> assert)
    {
        var quick = App("quick");
        var slow = App("slow");
        var inventory = new InventorySnapshot(DateTimeOffset.UtcNow, [quick, slow], [], []);
        var partialArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var counts = new ConcurrentDictionary<string, int>();
        var provider = new Provider(async (app, token) =>
        {
            var attempt = counts.AddOrUpdate(app.Identity, 1, (_, n) => n + 1);
            if (app.Identity == slow.Identity && attempt == 1)
            {
                await releaseSlow.Task.WaitAsync(token);
                return Result(app, UpdateStatus.Error);
            }
            return Result(app, UpdateStatus.Current);
        });
        var progress = new CallbackProgress(p =>
        {
            if (p.Result?.ApplicationIdentity == quick.Identity) partialArrived.TrySetResult();
        });
        var check = new UpdateCheckService([provider]).CheckAsync(inventory, progress: progress);
        await partialArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        assert(!check.IsCompleted, "The fast application's result was withheld until the slow check finished.");
        releaseSlow.SetResult();
        var first = await check;
        var retried = await new UpdateCheckService([provider]).RetryFailedAsync(inventory, first);
        assert(counts[quick.Identity] == 1 && counts[slow.Identity] == 2,
            "Retry repeated a successful check or skipped a failed application.");
        assert(retried.Results.Count == 2 && retried.Results.All(r => r.Status == UpdateStatus.Current),
            "Retry lost a successful assessment or failed to replace its old error.");
        var incomplete = first with { Results = first.Results.Where(r => r.ApplicationIdentity == quick.Identity).ToArray() };
        await new UpdateCheckService([provider]).RetryFailedAsync(inventory, incomplete);
        assert(counts[quick.Identity] == 1 && counts[slow.Identity] == 3, "Retry skipped an unfinished application or repeated a completed one.");
    }

    private static async Task CancellationAsync(Action<bool, string> assert, string fixture)
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var inventoryCanceled = false;
        try { await new ApplicationInventoryService(Path.Combine(fixture, "no-policies.json")).ScanAsync(canceled.Token); }
        catch (OperationCanceledException) { inventoryCanceled = true; }
        assert(inventoryCanceled, "Inventory cancellation was swallowed as a successful read.");
        using var http = new HttpClient();
        var driversCanceled = false;
        try { await new ManufacturerDriverService(http).CheckAsync(canceled.Token); }
        catch (OperationCanceledException) { driversCanceled = true; }
        assert(driversCanceled, "Driver cancellation was swallowed as a successful read.");

        using var cancellation = new CancellationTokenSource();
        var partial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new ConcurrentBag<UpdateCheckResult>();
        var provider = new Provider(async (app, token) =>
        {
            if (app.Identity == "slow") await Task.Delay(Timeout.Infinite, token);
            return Result(app, UpdateStatus.Current);
        });
        var task = new UpdateCheckService([provider]).CheckAsync(new(DateTimeOffset.UtcNow, [App("quick"), App("slow")], [], []),
            cancellation.Token, new CallbackProgress(p => { if (p.Result is { } r) { received.Add(r); partial.TrySetResult(); } }));
        await partial.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var checkCanceled = false;
        try { await task; } catch (OperationCanceledException) { checkCanceled = true; }
        assert(checkCanceled && received.Any(r => r.ApplicationIdentity == "quick") && !received.Any(r => r.ApplicationIdentity == "slow"),
            "Cancellation lost the finished check or fabricated a completed slow result.");
    }

    private static void RemovalAndPreferences(Action<bool, string> assert, string fixture)
    {
        var app = App("removed");
        var empty = new InventorySnapshot(DateTimeOffset.UtcNow, [], [], []);
        assert(RemovalVerification.IsStillPresent(app, null) is null, "A failed scan was accepted as proof of removal.");
        assert(RemovalVerification.IsStillPresent(app, empty with { Issues = [new("Registry", "Access denied")] }) is null,
            "An incomplete scan was accepted as proof of removal.");
        assert(RemovalVerification.IsStillPresent(app, empty) == false, "A complete inventory could not confirm absence.");
        assert(RemovalVerification.IsStillPresent(app, empty with { Applications = [app] }) == true,
            "An application still installed was reported removed.");
        var path = Path.Combine(fixture, "view.json");
        var store = new ViewPreferencesStore(path);
        store.Save(new(1550, 950, true, new() { ["applications"] = [320, 120, 140, 150] }));
        var saved = store.Load();
        assert(saved.Width == 1550 && saved.Height == 950 && saved.Maximized && saved.Columns!["applications"][0] == 320,
            "Window dimensions or column widths did not survive saving and reloading.");
        File.WriteAllText(path, "{bad-json");
        assert(store.Load() == new ViewPreferences(), "Corrupt view preferences prevented use of defaults.");
    }

    private static async Task ScanOwnershipAsync(Action<bool, string> assert)
    {
        var scans = new ScanController();
        using var first = scans.TryBegin()!;
        assert(first is not null && scans.IsActive && scans.TryBegin() is null,
            "A second scan was allowed while inventory/driver reading still owned the scan.");
        await scans.CancelAsync();
        assert(first!.Token.IsCancellationRequested && scans.IsActive,
            "Canceling unlocked the scan before its owner stopped publishing results.");
        first.Dispose();
        using var next = scans.TryBegin()!;
        first.Dispose(); // Late cleanup from the previous run must not unlock this one.
        assert(next is not null && scans.IsActive && !next.Token.IsCancellationRequested && scans.TryBegin() is null,
            "An old scan's cleanup released or canceled the replacement scan.");
        next!.Dispose();
        assert(!scans.IsActive, "Completing the scan did not release ownership.");
    }

    private static InstalledApplication App(string identity) => new(identity, identity, "Fixture", "1.0", "1.0", "fixture",
        null, null, null, null, InstallScope.CurrentUser, ManagementMode.Registry, ConfidenceLevel.High, false, [], null, []);
    private static UpdateCheckResult Result(InstalledApplication app, UpdateStatus status) => new(app.Identity, app.DisplayName,
        app.NormalizedVersion, null, status, "fixture", "Fixture", "neutral", "fixture", "x64", "stable", null, "fixture", null);
    private sealed class Provider(Func<InstalledApplication, CancellationToken, Task<UpdateCheckResult>> check) : IUpdateProvider
    {
        public string Id => "fixture";
        public UpdateProviderDescriptor Descriptor { get; } = new(UpdateProviderAuthority.ProducerRelease, 100, "fixture");
        public bool CanHandle(InstalledApplication app) => true;
        public Task<UpdateCheckResult> CheckAsync(InstalledApplication app, CancellationToken token) => check(app, token);
    }
    private sealed class CallbackProgress(Action<UpdateCheckProgress> callback) : IProgress<UpdateCheckProgress>
    { public void Report(UpdateCheckProgress value) => callback(value); }
    private sealed class NeverSign : IAuthenticodeVerifier
    { public AuthenticodeVerificationResult Verify(string path, string signer) => throw new InvalidOperationException("The hash-only fixture requested a signature."); }

    private sealed class RedirectServer : IAsyncDisposable
    {
        public static readonly byte[] Payload = Encoding.UTF8.GetBytes("Redirect fixture; never executed.");
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
        private readonly X509Certificate2 _cert;
        private readonly Task _server;
        private readonly string _scenario;
        private readonly int _port;
        public ConcurrentQueue<string> Hosts { get; } = new();
        public RedirectServer(string scenario)
        {
            _scenario = scenario;
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=allowed.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            _cert = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.DefaultKeySet);
            _listener.Start();
            _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _server = RunAsync();
        }
        public Uri Uri(string host, string path) => new($"https://{host}:{_port}{path}");
        public HttpClient Client() => UpdatePackageDownloader.CreateDownloadClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true, // The production factory must turn this off.
            UseProxy = false,
            SslOptions = new() { RemoteCertificateValidationCallback = (_, _, _, _) => true },
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(IPAddress.Loopback, _port, token); return new NetworkStream(socket, true); }
                catch { socket.Dispose(); throw; }
            }
        });
        private async Task RunAsync()
        {
            try
            {
                while (true)
                {
                    using var peer = await _listener.AcceptTcpClientAsync(_stop.Token);
                    using var tls = new SslStream(peer.GetStream());
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _cert }, _stop.Token);
                    using var reader = new StreamReader(tls, Encoding.ASCII, false, 4096, true);
                    var request = await reader.ReadLineAsync(_stop.Token);
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_stop.Token)))
                        if (line.StartsWith("Host:")) Hosts.Enqueue(line[5..].Trim());
                    var final = request?.Contains("/final ") == true;
                    var location = request?.Contains("/start ") == true
                        ? _scenario == "http" ? $"http://blocked.test:{_port}/middle"
                            : Uri(_scenario is "blocked" or "mirror" ? "blocked.test" : "allowed.test", "/middle").AbsoluteUri
                        : "/final";
                    var header = final ? $"HTTP/1.1 200 OK\r\nContent-Length: {Payload.Length}\r\nConnection: close\r\n\r\n"
                        : $"HTTP/1.1 302 Found\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    await tls.WriteAsync(Encoding.ASCII.GetBytes(header), _stop.Token);
                    if (final) await tls.WriteAsync(Payload, _stop.Token);
                    await tls.FlushAsync(_stop.Token);
                }
            }
            catch (Exception ex) when (_stop.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop();
            try { await _server; } finally { _cert.Dispose(); _stop.Dispose(); }
        }
    }
}
