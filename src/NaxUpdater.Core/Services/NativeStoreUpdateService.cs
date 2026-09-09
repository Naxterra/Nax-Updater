using NaxUpdater.Core.Models;
using Windows.ApplicationModel.Store.Preview.InstallControl;

namespace NaxUpdater.Core.Services;

public sealed record NativeStoreOffer(bool IsAvailable, string? Error, bool CheckFailed = false, NativeStoreQueueEntry? QueueEntry = null);
public sealed record NativeStoreQueueEntry(string ProductId, string PackageFamilyName, AppInstallState State, int ErrorCode = 0, bool MayAffectOtherItems = false);
public sealed record NativeStoreItemState(AppInstallState State, int ErrorCode = 0, double? Fraction = null);

public interface INativeStoreUpdateItem
{
    string ProductId { get; }
    string PackageFamilyName { get; }
    NativeStoreItemState Status();
    bool MayAffectOtherItems => false;
    void Restart() => throw new NotSupportedException("This Store item cannot be resumed through this client.");
}

public interface INativeStoreUpdateClient
{
    Task<NativeStoreQueueEntry?> ReadQueueAsync(string family, CancellationToken token) => Task.FromResult<NativeStoreQueueEntry?>(null);
    Task<INativeStoreUpdateItem?> GetQueueItemAsync(string family, CancellationToken token) => Task.FromResult<INativeStoreUpdateItem?>(null);
    Task<INativeStoreUpdateItem?> FindPausedUpdateAsync(StoreProductIdentity package, CancellationToken token);
    Task<INativeStoreUpdateItem?> StartUpdateAsync(PublishedStorePackage package, CancellationToken token);
}

public interface INativeStoreUpdateService
{
    Task<PreparedStoreQueueUpdate> PrepareQueueAsync(StoreQueueTarget target, CancellationToken token) => throw new NotSupportedException();
    Task<NativeStoreQueueEntry?> ReadQueueAsync(string family, CancellationToken token) => Task.FromResult<NativeStoreQueueEntry?>(null);
    Task<NativeStoreOffer> CheckIdentityAsync(StoreProductIdentity identity, CancellationToken token);
    Task<NativeStoreOffer> CheckAsync(PublishedStorePackage package, CancellationToken token);
    Task<PreparedNativeStoreUpdate> PrepareAsync(PublishedStorePackage package, CancellationToken token);
}

public sealed record PreparedStoreQueueUpdate(StoreQueueTarget Target,
    Func<IProgress<double>?, CancellationToken, Task<UpdateExecutionResult>> Apply);

public sealed class PreparedNativeStoreUpdate(
    PublishedStorePackage target,
    Func<CancellationToken, Task<UpdateExecutionResult>> apply)
{
    public PublishedStorePackage Target { get; } = target;
    public Task<UpdateExecutionResult> ApplyAsync(CancellationToken token) => apply(token);
}

public sealed class NativeStoreUpdateService : INativeStoreUpdateService
{
    internal const int PackageIdentityConflictCode = unchecked((int)0x80073CFB);
    internal const string PackageIdentityConflictMessage = "Windows rejected the Store package because this package identity is already installed with different contents (0x80073CFB). The installed application was preserved. No uninstall, downgrade, or repeated retry is performed; a corrected/newer publisher package or an explicitly approved repair is required.";
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly INativeStoreUpdateClient _client;
    private readonly Func<PublishedStorePackage, CancellationToken, Task<PublishedStorePackage?>> _refresh;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _installTimeout;

    public NativeStoreUpdateService(HttpClient? client = null)
    {
        _client = new NativeStoreUpdateClient();
        var metadata = new MicrosoftStoreProductMetadataClient(client ?? SharedClient);
        _refresh = (target, token) => metadata.GetPublishedPackageAsync(
            target.ProductId, target.PackageFamilyName, target.Architecture, target.Version, token, target.SkuId);
        _delay = Task.Delay;
        _installTimeout = TimeSpan.FromMinutes(20);
    }

    internal NativeStoreUpdateService(INativeStoreUpdateClient client,
        Func<PublishedStorePackage, CancellationToken, Task<PublishedStorePackage?>> refresh,
        Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? installTimeout = null)
    {
        _client = client;
        _refresh = refresh;
        _delay = delay ?? Task.Delay;
        _installTimeout = installTimeout ?? TimeSpan.FromMinutes(20);
    }

    public Task<NativeStoreOffer> CheckAsync(PublishedStorePackage package, CancellationToken token) =>
        CheckIdentityAsync(StoreProductIdentity.From(package), token);

    public async Task<PreparedStoreQueueUpdate> PrepareQueueAsync(StoreQueueTarget target, CancellationToken token)
    {
        var item = await _client.GetQueueItemAsync(target.PackageFamilyName, token)
            ?? throw new InvalidOperationException("The previously observed Store queue item is no longer present. Scan again.");
        var identity = new StoreProductIdentity(target.ProductId, "", target.PackageFamilyName);
        ValidateIdentity(item, identity);
        return new(target, async (progress, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            // Reacquire the existing OS-owned item; never create another install.
            var current = await _client.GetQueueItemAsync(target.PackageFamilyName, cancellation)
                ?? throw new InvalidOperationException("The Store queue changed before execution. Scan again.");
            ValidateIdentity(current, identity);
            var initial = current.Status();
            if (initial.State == AppInstallState.Error && initial.ErrorCode == PackageIdentityConflictCode)
                return new(PackageIdentityConflictCode, false, PackageIdentityConflictMessage);
            if (initial.State == AppInstallState.Canceled) return new(1223, false, "The Store operation was canceled.");
            if (initial.State is AppInstallState.ReadyToDownload or AppInstallState.Paused or AppInstallState.Error)
            {
                if (current.MayAffectOtherItems)
                    return new(-1, false, "Windows reports that restarting this item would affect other packages; the operation was not started.");
                current.Restart(); // Only after the user's Update/Update All action.
            }
            var deadline = DateTimeOffset.UtcNow + _installTimeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellation.ThrowIfCancellationRequested(); // Stop observing, not the Store deployment.
                var state = current.Status();
                if (state.Fraction is double fraction) progress?.Report(Math.Clamp(fraction, 0, 1));
                if (state.State == AppInstallState.Completed) return new(0, true, null);
                if (state.State == AppInstallState.Error) return new(state.ErrorCode == 0 ? -1 : state.ErrorCode, false,
                    state.ErrorCode == PackageIdentityConflictCode ? PackageIdentityConflictMessage : $"Store deployment failed (0x{state.ErrorCode:X8}).");
                if (state.State == AppInstallState.Canceled) return new(1223, false, "The Store operation was canceled.");
                await _delay(TimeSpan.FromSeconds(1), cancellation);
            }
            return new(-1, false, "Windows has not completed this Store operation. Its deployment was left running; success has not been claimed.");
        });
    }

    public async Task<NativeStoreQueueEntry?> ReadQueueAsync(string family, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var entry = await _client.ReadQueueAsync(family, token);
        if (entry is not null && !entry.PackageFamilyName.Equals(family, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Store queue returned a different package family.");
        return entry is { State: AppInstallState.Completed or AppInstallState.Canceled } ? null : entry;
    }

    public async Task<NativeStoreOffer> CheckIdentityAsync(StoreProductIdentity package, CancellationToken token)
    {
        try
        {
            var queued = await ReadQueueAsync(package.PackageFamilyName, token);
            if (queued is not null) return new(false, null, QueueEntry: queued);
            var item = await _client.FindPausedUpdateAsync(package, token);
            // An existing deployment can make SearchForUpdates return null.
            // Re-read the queue after the query to cover that race.
            if (item is null)
            {
                queued = await ReadQueueAsync(package.PackageFamilyName, token);
                if (queued is not null) return new(false, null, QueueEntry: queued);
            }
            if (item is null) return new(false, "Windows Store has not returned an applicable update for this installation.");
            ValidateIdentity(item, package);
            var status = item.Status();
            if (status.State is not (AppInstallState.ReadyToDownload or AppInstallState.Completed or AppInstallState.Canceled or AppInstallState.Error))
                return new(false, null, QueueEntry: new(item.ProductId, item.PackageFamilyName, status.State, status.ErrorCode));
            return status.State is AppInstallState.Error or AppInstallState.Canceled or AppInstallState.Completed
                ? new(false, $"Windows Store update state: {status.State} (0x{status.ErrorCode:X8}).", status.State == AppInstallState.Error)
                : new(true, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { return new(false, $"Native Store API: {exception.Message} (0x{exception.HResult:X8})", true); }
    }

    public async Task<PreparedNativeStoreUpdate> PrepareAsync(PublishedStorePackage package, CancellationToken token)
    {
        if (await ReadQueueAsync(package.PackageFamilyName, token) is { State: not AppInstallState.ReadyToDownload })
            throw new InvalidOperationException("This package is already in the Microsoft Store queue. Finish it there before retrying.");
        var refreshed = await _refresh(package, token);
        if (refreshed != package)
            throw new InvalidOperationException("The published Store package changed after approval. Check for updates again.");
        var item = await _client.FindPausedUpdateAsync(StoreProductIdentity.From(package), token)
            ?? throw new InvalidOperationException("Windows Store no longer returns the approved update.");
        ValidateIdentity(item, package);
        return new(package, async cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ReadQueueAsync(package.PackageFamilyName, cancellationToken) is { State: not AppInstallState.ReadyToDownload })
                throw new InvalidOperationException("Microsoft Store already owns a queued deployment for this package.");
            ValidateIdentity(item, package);
            // The automatic flag is enabled only by Apply, after user approval
            // and process shutdown. All detection/preparation queries keep it off.
            var active = await _client.StartUpdateAsync(package, cancellationToken)
                ?? throw new InvalidOperationException("Windows Store no longer returns the approved update.");
            ValidateIdentity(active, package);
            var deadline = DateTimeOffset.UtcNow + _installTimeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var state = active.Status();
                if (state.State == AppInstallState.Completed) return new(0, true, null);
                if (state.State == AppInstallState.Error)
                    return new(state.ErrorCode == 0 ? -1 : state.ErrorCode, false, $"Windows Store update failed (0x{state.ErrorCode:X8}).");
                if (state.State == AppInstallState.Canceled) return new(1223, false, "The Store update was canceled.");
                // Windows owns an active deployment; do not cancel or terminate it
                // merely because NaxUpdater's wait was canceled.
                await _delay(TimeSpan.FromSeconds(1), CancellationToken.None);
            }
            return new(-1, false, "Windows Store has not confirmed completion. Check its download queue before retrying.");
        });
    }

    private static void ValidateIdentity(INativeStoreUpdateItem item, PublishedStorePackage package)
        => ValidateIdentity(item, StoreProductIdentity.From(package));

    private static void ValidateIdentity(INativeStoreUpdateItem item, StoreProductIdentity package)
    {
        if (!item.ProductId.Equals(package.ProductId, StringComparison.OrdinalIgnoreCase) ||
            !item.PackageFamilyName.Equals(package.PackageFamilyName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Windows Store returned a different product or package family.");
    }

    internal static AppUpdateOptions QueryOptions(bool startApprovedUpdate = false) => new()
    {
        AutomaticallyDownloadAndInstallUpdateIfFound = startApprovedUpdate,
        AllowForcedAppRestart = false
    };
}

internal sealed class NativeStoreUpdateClient : INativeStoreUpdateClient
{
    private static readonly SemaphoreSlim QuerySlots = new(8, 8);
    private readonly Lazy<AppInstallManager> _manager = new(() => new AppInstallManager());
    private readonly object _queueGate = new();
    private Task<INativeStoreUpdateItem[]>? _queueSnapshot;
    private long _queueSnapshotAt;
    private volatile bool _queueUnresponsive;
    private volatile bool _searchUnresponsive;
    private readonly Func<INativeStoreUpdateItem[]> _readQueue;
    private readonly TimeSpan _queueTimeout;
    internal NativeStoreUpdateClient(Func<INativeStoreUpdateItem[]>? readQueue = null, TimeSpan? queueTimeout = null)
    {
        _readQueue = readQueue ?? ReadQueueItems;
        _queueTimeout = queueTimeout ?? TimeSpan.FromSeconds(5);
    }
    public async Task<NativeStoreQueueEntry?> ReadQueueAsync(string family, CancellationToken token)
    {
        var item = FindQueueItem(await QueueSnapshotAsync(false, token), family);
        if (item is null)
        {
            if (_searchUnresponsive) throw new TimeoutException("The shared Microsoft Store update service timed out earlier in this scan. This app was not declared current; retry the scan after the service recovers.");
            return null;
        }
        var status = item.Status();
        return new(item.ProductId, item.PackageFamilyName, status.State, status.ErrorCode, item.MayAffectOtherItems);
    }
    public async Task<INativeStoreUpdateItem?> GetQueueItemAsync(string family, CancellationToken token) =>
        FindQueueItem(await QueueSnapshotAsync(true, token), family);

    private static INativeStoreUpdateItem? FindQueueItem(IEnumerable<INativeStoreUpdateItem> items, string family) =>
        items.FirstOrDefault(item => item.PackageFamilyName.Equals(family, StringComparison.OrdinalIgnoreCase) &&
            item.Status().State is not (AppInstallState.Completed or AppInstallState.Canceled));

    private async Task<INativeStoreUpdateItem[]> QueueSnapshotAsync(bool fresh, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Task<INativeStoreUpdateItem[]> snapshot;
        lock (_queueGate)
        {
            if (_queueUnresponsive && _queueSnapshot is { IsCompleted: false })
                throw new TimeoutException("The shared Microsoft Store queue service is not responding. Retry the scan after it recovers.");
            if (_queueSnapshot is null || _queueSnapshot.IsCompleted &&
                (fresh || System.Diagnostics.Stopwatch.GetElapsedTime(_queueSnapshotAt) > TimeSpan.FromSeconds(1)))
            {
                _queueUnresponsive = false;
                _queueSnapshotAt = System.Diagnostics.Stopwatch.GetTimestamp();
                // Queue access is synchronous COM. Keep a hung broker call off
                // the common worker pool, and share one read between families.
                _queueSnapshot = Task.Factory.StartNew(_readQueue, CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            snapshot = _queueSnapshot;
        }
        try { return await snapshot.WaitAsync(_queueTimeout, token); }
        catch (TimeoutException) { _queueUnresponsive = true; throw new TimeoutException("The shared Microsoft Store queue service timed out. Other sources remain available."); }
    }

    private INativeStoreUpdateItem[] ReadQueueItems()
    {
        var result = new List<INativeStoreUpdateItem>();
        // Use indexed WinRT access: some projections do not expose IIterable.
        void Read(IReadOnlyList<AppInstallItem> items, int depth)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (result.Count >= 1024) throw new InvalidOperationException("The Store queue exceeded the supported snapshot size.");
                var item = items[i];
                result.Add(new Item(item, _manager.Value));
                if (depth < 4) Read(item.Children, depth + 1);
            }
        }
        Read(_manager.Value.AppInstallItems, 0);
        return result.ToArray();
    }
    public async Task<INativeStoreUpdateItem?> FindPausedUpdateAsync(StoreProductIdentity package, CancellationToken token)
        => await QueryAsync(package, false, token);

    public async Task<INativeStoreUpdateItem?> StartUpdateAsync(PublishedStorePackage package, CancellationToken token)
        => await QueryAsync(StoreProductIdentity.From(package), true, token);

    private async Task<INativeStoreUpdateItem?> QueryAsync(StoreProductIdentity package, bool apply, CancellationToken token)
    {
        if (!apply && _searchUnresponsive) throw new TimeoutException("The shared Microsoft Store update service timed out earlier in this scan. Retry the scan after it recovers.");
        await QuerySlots.WaitAsync(token);
        try
        {
            if (!apply && _searchUnresponsive) throw new TimeoutException("The shared Microsoft Store update service is not responding.");
            var manager = _manager.Value;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            var query = Task.Factory.StartNew(() => manager.SearchForUpdatesAsync(
                package.ProductId, package.SkuId, "", "", NativeStoreUpdateService.QueryOptions(apply)).AsTask(deadline.Token),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            AppInstallItem? item;
            try { item = apply ? await query : await query.WaitAsync(TimeSpan.FromSeconds(20), token); }
            catch (TimeoutException)
            {
                if (!apply) { _searchUnresponsive = true; deadline.Cancel(); }
                _ = query.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                throw new TimeoutException("The native Microsoft Store update query exceeded 20 seconds. The scan will not repeat the same stalled service request for every app.");
            }
            finally { lock (_queueGate) { _queueSnapshotAt = 0; } } // Recheck queue races after a query.
            return item is null ? null : new Item(item, manager);
        }
        finally { QuerySlots.Release(); }
    }

    private sealed class Item(AppInstallItem item, AppInstallManager manager) : INativeStoreUpdateItem
    {
        public bool MayAffectOtherItems => item.ItemOperationsMightAffectOtherItems;
        public void Restart() => item.Restart();
        public string ProductId => item.ProductId;
        public string PackageFamilyName => item.PackageFamilyName;
        public NativeStoreItemState Status()
        {
            var status = item.GetCurrentStatus();
            var result = new NativeStoreItemState(status.InstallState, status.ErrorCode?.HResult ?? 0, status.PercentComplete / 100);
            GC.KeepAlive(manager);
            return result;
        }
    }
}
