namespace NaxUpdater.Core.Services;

internal enum StoreBatchCheck { Unavailable, NoUpdates, CheckIndividually, TimedOut }

// One fresh, read-only Store-owned query replaces the repeated per-product/SKU
// searches on a cold scan. An unrecognised response is never evidence of currency.
internal sealed class StoreUpdateBatchQuery
{
    // On the owner's PC the query answered in about 5 s on one day and in 15 to 18 s on another.
    // With a limit of 12 s the slow day fell back to one native query per app, which the Store
    // answers one at a time: 14 apps took 83 s. The limit stays below the 30 s that one source
    // check may take, so the catalog lookup before it still fits.
    internal static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(25);
    private readonly Func<string?> _resolve;
    private readonly Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<ProcessQueryResult>> _run;
    private readonly TimeSpan _timeout;
    private readonly Lock _gate = new();
    private Task<StoreBatchCheck>? _check;

    public StoreUpdateBatchQuery()
        : this(() => StorePackagedExecutable.Resolve("Microsoft.WindowsStore_8wekyb3d8bbwe", "store.exe")) { }

    internal StoreUpdateBatchQuery(Func<string?> resolve)
        : this(resolve, (path, args, timeout, token) => new ProcessQueryRunner().RunAsync(path, args, timeout, token,
            // WinExe hosts otherwise inherit the ANSI code page, corrupting the
            // Store CLI's UTF-8 protocol even though console tests decode it well.
            outputEncoding: new System.Text.UTF8Encoding(false, true))) { }

    internal StoreUpdateBatchQuery(Func<string?> resolve,
        Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<ProcessQueryResult>> run,
        TimeSpan? timeout = null)
    {
        _resolve = resolve;
        _run = run;
        _timeout = timeout ?? QueryTimeout;
    }

    public Task<StoreBatchCheck> CheckAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Task<StoreBatchCheck> check;
        lock (_gate) check = _check ??= QueryAsync();
        // An application timing out must not cancel the shared query for siblings.
        // The read-only helper has its own bounded lifetime.
        return check.WaitAsync(token);
    }

    internal StoreUpdateBatchQuery ForNewScan() => new(_resolve, _run, _timeout);

    private async Task<StoreBatchCheck> QueryAsync()
    {
        Task<ProcessQueryResult>? query = null;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        // A query that ran out of time is its own outcome: the per-app queries go through the
        // same Store service and would take longer still, so they are not started for it.
        bool RanOutOfTime() => System.Diagnostics.Stopwatch.GetElapsedTime(started) >= _timeout * 0.9;
        try
        {
            var path = _resolve();
            if (path is null) return StoreBatchCheck.Unavailable;
            using var deadline = new CancellationTokenSource(_timeout);
            // No --apply, filters, shell, PATH lookup or deployment command.
            query = _run(path, ["updates"], _timeout, deadline.Token);
            var result = await query.WaitAsync(deadline.Token);
            var check = Parse(result);
            // The process runner ends a query at its limit with an error result.
            return check == StoreBatchCheck.CheckIndividually && RanOutOfTime() ? StoreBatchCheck.TimedOut : check;
        }
        catch (Exception exception)
        {
            if (query is not null)
                _ = query.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            // Only the query's own deadline cancels it; no caller's token reaches it.
            return exception is OperationCanceledException or TimeoutException
                ? StoreBatchCheck.TimedOut
                : StoreBatchCheck.CheckIndividually;
        }
    }

    internal static StoreBatchCheck Parse(ProcessQueryResult result)
    {
        if (result.ExitCode != 0 || !string.IsNullOrWhiteSpace(result.StandardError) ||
            result.StandardOutput.Length > 65536) return StoreBatchCheck.CheckIndividually;
        var lines = result.StandardOutput.TrimStart('\uFEFF')
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Fail closed on localisation, changed output, warning/error lines,
        // partial results or an update table. Those require native per-app checks.
        return lines.Length == 2 && lines[0] is "Checking for updates…" or "Checking for updates..." &&
               lines[1] == "No updates found."
            ? StoreBatchCheck.NoUpdates : StoreBatchCheck.CheckIndividually;
    }
}
