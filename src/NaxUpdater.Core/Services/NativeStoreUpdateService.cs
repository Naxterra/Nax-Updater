using NaxUpdater.Core.Models;
using Windows.ApplicationModel.Store.Preview.InstallControl;

namespace NaxUpdater.Core.Services;

public sealed record NativeStoreOffer(bool IsAvailable, string? Error, bool CheckFailed = false);
public sealed record NativeStoreItemState(AppInstallState State, int ErrorCode = 0);

public interface INativeStoreUpdateItem
{
    string ProductId { get; }
    string PackageFamilyName { get; }
    NativeStoreItemState Status();
}

public interface INativeStoreUpdateClient
{
    Task<INativeStoreUpdateItem?> FindPausedUpdateAsync(StoreProductIdentity package, CancellationToken token);
    Task<INativeStoreUpdateItem?> StartUpdateAsync(PublishedStorePackage package, CancellationToken token);
}

public interface INativeStoreUpdateService
{
    Task<NativeStoreOffer> CheckIdentityAsync(StoreProductIdentity identity, CancellationToken token);
    Task<NativeStoreOffer> CheckAsync(PublishedStorePackage package, CancellationToken token);
    Task<PreparedNativeStoreUpdate> PrepareAsync(PublishedStorePackage package, CancellationToken token);
}

public sealed class PreparedNativeStoreUpdate(
    PublishedStorePackage target,
    Func<CancellationToken, Task<UpdateExecutionResult>> apply)
{
    public PublishedStorePackage Target { get; } = target;
    public Task<UpdateExecutionResult> ApplyAsync(CancellationToken token) => apply(token);
}

public sealed class NativeStoreUpdateService : INativeStoreUpdateService
{
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

    public async Task<NativeStoreOffer> CheckIdentityAsync(StoreProductIdentity package, CancellationToken token)
    {
        try
        {
            var item = await _client.FindPausedUpdateAsync(package, token);
            if (item is null) return new(false, "Windows Store has not returned an applicable update for this installation.");
            ValidateIdentity(item, package);
            var status = item.Status();
            return status.State is AppInstallState.Error or AppInstallState.Canceled or AppInstallState.Completed
                ? new(false, $"Windows Store update state: {status.State} (0x{status.ErrorCode:X8}).", status.State == AppInstallState.Error)
                : new(true, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { return new(false, $"Native Store API: {exception.Message} (0x{exception.HResult:X8})", true); }
    }

    public async Task<PreparedNativeStoreUpdate> PrepareAsync(PublishedStorePackage package, CancellationToken token)
    {
        var refreshed = await _refresh(package, token);
        if (refreshed != package)
            throw new InvalidOperationException("The published Store package changed after approval. Check for updates again.");
        var item = await _client.FindPausedUpdateAsync(StoreProductIdentity.From(package), token)
            ?? throw new InvalidOperationException("Windows Store no longer returns the approved update.");
        ValidateIdentity(item, package);
        return new(package, async cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
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
    public async Task<INativeStoreUpdateItem?> FindPausedUpdateAsync(StoreProductIdentity package, CancellationToken token)
        => await QueryAsync(package, false, token);

    public async Task<INativeStoreUpdateItem?> StartUpdateAsync(PublishedStorePackage package, CancellationToken token)
        => await QueryAsync(StoreProductIdentity.From(package), true, token);

    private static async Task<INativeStoreUpdateItem?> QueryAsync(StoreProductIdentity package, bool apply, CancellationToken token)
    {
        await QuerySlots.WaitAsync(token);
        try
        {
            var manager = new AppInstallManager();
            var operation = manager.SearchForUpdatesAsync(
                package.ProductId, package.SkuId, "", "", NativeStoreUpdateService.QueryOptions(apply));
            var item = await operation.AsTask(token);
            return item is null ? null : new Item(item, manager);
        }
        finally { QuerySlots.Release(); }
    }

    private sealed class Item(AppInstallItem item, AppInstallManager manager) : INativeStoreUpdateItem
    {
        public string ProductId => item.ProductId;
        public string PackageFamilyName => item.PackageFamilyName;
        public NativeStoreItemState Status()
        {
            var status = item.GetCurrentStatus();
            var result = new NativeStoreItemState(status.InstallState, status.ErrorCode?.HResult ?? 0);
            GC.KeepAlive(manager);
            return result;
        }
    }
}
