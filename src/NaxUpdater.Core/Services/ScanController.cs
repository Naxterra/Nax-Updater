namespace NaxUpdater.Core.Services;

// One read operation owns cancellation until it has stopped publishing results.
// A late completion/disposal can never release a newer scan's ownership.
public sealed class ScanController
{
    private ScanRun? _active;
    public bool IsActive => Volatile.Read(ref _active) is not null;

    public ScanRun? TryBegin()
    {
        var run = new ScanRun(this);
        if (Interlocked.CompareExchange(ref _active, run, null) is null) return run;
        run.Dispose();
        return null;
    }

    public async Task CancelAsync()
    {
        if (Volatile.Read(ref _active) is { } run) await run.CancelAsync();
    }

    public sealed class ScanRun : IDisposable
    {
        private readonly ScanController _owner;
        private readonly CancellationTokenSource _cancellation = new();
        private int _disposed;
        internal ScanRun(ScanController owner) { _owner = owner; Token = _cancellation.Token; }
        public CancellationToken Token { get; }
        internal async Task CancelAsync()
        {
            try { await _cancellation.CancelAsync(); }
            catch (ObjectDisposedException) { } // Completion raced the cancellation request.
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Interlocked.CompareExchange(ref _owner._active, null, this);
            _cancellation.Dispose();
        }
    }
}
