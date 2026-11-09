using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

// One scan shares its manufacturer driver check and its Kyocera catalog between the update list
// and the driver view. The drivers were checked once for the four driver rows of the update list
// and again for the driver view, and Kyocera was asked by both; now the driver check starts
// together with the application inventory and both views use its result.
public sealed class SharedScanSources(HttpClient httpClient, string? cacheDirectory = null,
    CancellationToken scanCancellation = default)
{
    private readonly Lock _gate = new();
    private Task<ManufacturerDriverSnapshot>? _drivers;

    public KyoceraSupportCatalog Kyocera { get; } = new(httpClient, cacheDirectory);

    public DateTimeOffset? DriversStartedAt { get; private set; }

    public Task<ManufacturerDriverSnapshot> DriversAsync()
    {
        lock (_gate)
        {
            if (_drivers is null)
            {
                DriversStartedAt = DateTimeOffset.UtcNow;
                _drivers = Task.Run(() => new ManufacturerDriverService(httpClient, Kyocera).CheckAsync(scanCancellation), scanCancellation);
                _ = _drivers.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            return _drivers;
        }
    }
}
